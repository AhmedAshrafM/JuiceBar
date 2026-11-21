using System.Diagnostics;
using Bfm.Core;
using Bfm.Interop;
using Bfm.Providers;
using Microsoft.Win32;

namespace Bfm.UI;

/// <summary>The strip of battery chips laid over the empty left part of the taskbar.</summary>
internal sealed class BarForm : Form
{
    // Pixels of exactly this colour are transparent and click-through; close to the dark taskbar so
    // anti-aliased chip edges blend in.
    static readonly Color Key = Color.FromArgb(30, 30, 31);

    const int WmDeviceChange = 0x0219, WmDisplayChange = 0x007E, WmSettingChange = 0x001A, WmDpiChanged = 0x02E0;
    const int DbtDevNodesChanged = 0x0007;
    readonly DeviceMonitor _monitor;
    readonly Settings _settings;
    readonly List<(Rectangle Rect, DeviceStatus Dev)> _chips = new();
    readonly ToolTip _tip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
    readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 60_000 };
    readonly System.Windows.Forms.Timer _zOrderTimer = new() { Interval = 5_000 };
    readonly System.Windows.Forms.Timer _deviceChangeTimer = new() { Interval = 3_000 };
    readonly User32.WinEventProc _foregroundProc;
    readonly User32.WinEventProc _trayMovedProc;
    readonly uint _taskbarCreatedMsg;
    IntPtr _trayHwnd, _trayHook;
    bool _taskbarHidden;

    IReadOnlyList<DeviceStatus> _devices = Array.Empty<DeviceStatus>();
    Font _font = null!, _iconFont = null!, _boltFont = null!;
    float _scale = 1f;
    IntPtr _hook;
    bool _fullscreenHidden, _trimmedOnce;
    Point _downAt;
    int _downOffset;
    bool _mouseDown, _dragging;
    int _hover = -1;
    FlyoutForm? _flyout;
    NotifyIcon? _notify;

    public BarForm(DeviceMonitor monitor, Settings settings)
    {
        _monitor = monitor;
        _settings = settings;

        Text = AppInfo.Name;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Key;
        TransparencyKey = Key;
        DoubleBuffered = true;

        ApplyScale(DeviceDpi);
        _devices = _monitor.Snapshot();
        LayoutChips();

        _monitor.Changed += () => BeginInvokeSafe(OnDevicesChanged);
        _monitor.Alert += (d, kind) => BeginInvokeSafe(() => ShowAlert(d, kind));

        _pollTimer.Tick += (_, _) =>
        {
            _ = _monitor.RefreshAsync(false);
            if (UpdateDue()) _ = CheckForUpdateAsync(manual: false);
            ReleaseHeld();
            CheckChargeReminder();
            RefreshTaskbarItems();
        };
        _zOrderTimer.Tick += (_, _) => KeepOnTop();
        _deviceChangeTimer.Tick += (_, _) =>
        {
            _deviceChangeTimer.Stop();
            _ = _monitor.RefreshAsync(true);
        };

        _foregroundProc = OnForegroundChanged;
        _trayMovedProc = OnTrayMoved;
        _taskbarCreatedMsg = User32.RegisterWindowMessageW("TaskbarCreated");
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x80, WsExTopmost = 0x8, WsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow | WsExTopmost | WsExNoActivate;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hook = User32.SetWinEventHook(User32.EventSystemForeground, User32.EventSystemForeground, IntPtr.Zero,
            _foregroundProc, 0, 0, User32.WineventOutOfContext);
        HookTaskbar();
        _rawMouse = new RawMouse(Handle, 30_000);
        _rawMouse.Moved += path =>
        {
            if (UgreenReceiverProvider.IsReceiverPath(path) && UgreenReceiverProvider.NoteActivity())
                _ = _monitor.RefreshAsync(false);
        };
    }

    RawMouse? _rawMouse;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionOnTaskbar();
        UpdateVisibility();
        RefreshTaskbarItems();
        _pollTimer.Start();
        _zOrderTimer.Start();
        _ = _monitor.RefreshAsync(true);
        if (JustUpdated) ShowBalloon($"{AppInfo.Name} updated", $"Now running version {Installer.Version.ToString(3)}", ToolTipIcon.Info);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool JustUpdated { get; init; }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_hook != IntPtr.Zero) User32.UnhookWinEvent(_hook);
        if (_trayHook != IntPtr.Zero) User32.UnhookWinEvent(_trayHook);
        _notify?.Dispose();
        _rawMouse?.Dispose();
        base.OnFormClosed(e);
    }

    internal void BeginInvokeSafe(Action a)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
    }

    void ApplyScale(int dpi)
    {
        _scale = dpi / 96f;
        _font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _iconFont = new Font(Theme.IconFontName, 10.5f, FontStyle.Regular, GraphicsUnit.Point);
        _boltFont = new Font(Theme.IconFontName, 7.5f, FontStyle.Regular, GraphicsUnit.Point);
    }

    int S(float v) => (int)Math.Round(v * _scale);

    const string AudioChipId = "@audio";
    AudioSwitch.State? _audio;

    IEnumerable<DeviceStatus> VisibleDevices()
    {
        var shown = _devices.Where(d => !_settings.Hidden.Contains(d.Id) && (d.Connected || _settings.ShowDisconnected)).ToList();
        if (!_settings.Compact || _settings.CompactHideAbove <= 0 || shown.Count == 0) return shown;
        var lowest = shown.Where(d => d.Battery is not null).MinBy(d => d.Battery);
        return shown.Where(d => d == lowest || d.Charging == true || d.Battery is null || d.Battery <= _settings.CompactHideAbove);
    }

    void RefreshAudio()
    {
        if (!_settings.AudioChip)
        {
            _audio = null;
            return;
        }
        try { _audio = AudioSwitch.Read(_settings); }
        catch (Exception ex)
        {
            _audio = null;
            Log.Write("audio read failed: " + ex.Message);
        }
    }

    void OnDevicesChanged()
    {
        _devices = _monitor.Snapshot();
        RefreshAudio();
        if (!_trimmedOnce)
        {
            _trimmedOnce = true;
            BeginInvokeSafe(Kernel32.TrimWorkingSet);
        }
        LayoutChips();
        PositionOnTaskbar();
        Invalidate();
        _flyout?.UpdateDevice(_devices.FirstOrDefault(d => d.Id == _flyout.DeviceId));
    }

    // ---------- layout & painting ----------

    void LayoutChips()
    {
        _chips.Clear();
        int x = S(2), h = S(30), y = S(2);
        int pad = S(_settings.Compact ? 6 : 9), iconW = S(16), gap = S(_settings.Compact ? 3 : 4);
        var visible = VisibleDevices().ToList();

        if (visible.Count == 0)
        {
            var placeholder = new DeviceStatus { Id = "", Name = $"{AppInfo.Name} — no devices yet", Kind = DeviceKind.Unknown };
            visible.Add(placeholder);
        }
        if (_audio?.Current is not null)
            visible.Add(new DeviceStatus { Id = AudioChipId, Name = _audio.Current.Name, Kind = DeviceKind.Unknown, Connected = true });

        foreach (var d in visible)
        {
            var text = ChipText(d);
            var textW = text.Length == 0 ? 0 : TextRenderer.MeasureText(text, _font, Size.Empty, TextFormatFlags.NoPadding).Width + S(5);
            int w = pad + iconW + textW + (d.Charging == true ? S(10) : 0) + pad;
            _chips.Add((new Rectangle(x, y, w, h), d));
            x += w + gap;
        }
        ClientSize = new Size(x - gap + S(2), h + S(4));
    }

    string ChipText(DeviceStatus d)
    {
        if (d.Id == AudioChipId) return _settings.Compact ? "" : AudioSwitch.Short(d.Name);
        if (d.Id.Length == 0) return "--";
        if (d.Battery is not int b) return "--";
        return _settings.Compact ? $"{b}" : $"{b}%";
    }

    string ChipGlyph(DeviceStatus d) =>
        d.Id == AudioChipId ? (_audio?.CurrentIsHeadset == true ? "\uE7F6" : "\uE767") : Theme.Glyph(d.Kind);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        for (int i = 0; i < _chips.Count; i++)
        {
            var (r, d) = _chips[i];
            bool dim = !d.Connected;
            bool audio = d.Id == AudioChipId;
            var bgColor = i == _hover ? Theme.ChipHover : Theme.Chip;
            using (var bg = new SolidBrush(bgColor))
                Theme.Rounded(g, bg, r, S(6));
            using (var edge = new Pen(i == _hover ? Theme.EdgeHover : Theme.Edge))
                Theme.RoundedOutline(g, edge, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(6));

            int threshold = d.Id.Length > 0 && !audio ? (_settings.Prefs(d.Id).LowAlert ?? _settings.AlertThreshold) : 0;
            bool low = !dim && d.Charging != true && d.Battery is int lv && threshold > 0 && lv < threshold;
            var fg = dim ? Theme.TextDim : low ? Theme.Low : Theme.Text;
            int pad = S(_settings.Compact ? 6 : 9), iconW = S(16);
            var iconRect = new Rectangle(r.X + pad, r.Y, iconW, r.Height - S(audio ? 1 : 3));
            TextRenderer.DrawText(g, ChipGlyph(d), _iconFont, iconRect, dim ? Theme.TextDim : audio ? Theme.Accent : Theme.Text, bgColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var text = ChipText(d);
            if (text.Length > 0)
            {
                var textRect = new Rectangle(iconRect.Right + S(5), r.Y, r.Right - iconRect.Right - S(5) - pad, r.Height - S(audio ? 1 : 3));
                TextRenderer.DrawText(g, text, _font, textRect, fg, bgColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (d.Charging == true)
            {
                var boltRect = new Rectangle(r.Right - pad - S(10), r.Y, S(10), r.Height - S(3));
                TextRenderer.DrawText(g, Theme.Bolt, _boltFont, boltRect, Theme.Mid, bgColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (d.Id.Length > 0 && !audio)
            {
                var track = new RectangleF(r.X + pad, r.Bottom - S(6), r.Width - pad * 2, S(3));
                using var trackBrush = new SolidBrush(Theme.Track);
                Theme.Rounded(g, trackBrush, track, track.Height / 2);
                if (d.Battery is int level && level > 0)
                {
                    using var fill = new SolidBrush(Theme.ForLevel(level, dim));
                    Theme.Rounded(g, fill, new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * level / 100f), track.Height), track.Height / 2);
                }
            }
        }
    }

    // ---------- placement ----------

    /// <summary>
    /// Watches the taskbar window itself (not its docked slot) so the bar slides away and back with an
    /// auto-hiding taskbar. The hook is scoped to Explorer's taskbar thread, so it costs nothing otherwise.
    /// </summary>
    void HookTaskbar()
    {
        if (_trayHook != IntPtr.Zero) User32.UnhookWinEvent(_trayHook);
        _trayHook = IntPtr.Zero;
        _trayHwnd = User32.FindWindowW("Shell_TrayWnd", null);
        if (_trayHwnd == IntPtr.Zero) return;
        uint tid = User32.GetWindowThreadProcessId(_trayHwnd, out uint pid);
        _trayHook = User32.SetWinEventHook(User32.EventObjectLocationChange, User32.EventObjectLocationChange, IntPtr.Zero,
            _trayMovedProc, pid, tid, User32.WineventOutOfContext);
    }

    void OnTrayMoved(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != _trayHwnd || idObject != 0) return;
        bool wasHidden = _taskbarHidden;
        PositionOnTaskbar();
        if (wasHidden != _taskbarHidden) UpdateVisibility();
    }

    /// <summary>The taskbar's on-screen rectangle; while auto-hidden it sits almost entirely off-screen.</summary>
    Rectangle? LiveTaskbarRect(out bool hidden)
    {
        hidden = false;
        if (_trayHwnd == IntPtr.Zero || !User32.GetWindowRect(_trayHwnd, out var wr)) return null;
        var rect = wr.ToRectangle();
        var visible = Rectangle.Intersect(rect, Screen.FromHandle(_trayHwnd).Bounds);
        hidden = !User32.IsWindowVisible(_trayHwnd)
                 || visible.Width * visible.Height < rect.Width * rect.Height / 2;
        return rect;
    }

    /// <summary>Kept visible while the pointer is on it or a flyout is open, so it never vanishes mid-click.</summary>
    bool InUse => _flyout is { IsDisposed: false } || (Visible && Bounds.Contains(Cursor.Position));

    void UpdateVisibility()
    {
        bool want = !_fullscreenHidden && (!_taskbarHidden || InUse);
        if (want && !Visible)
        {
            Show();
            PositionOnTaskbar();
        }
        else if (!want && Visible)
        {
            _flyout?.Close();
            _tip.Hide(this);
            Hide();
        }
    }

    void PositionOnTaskbar()
    {
        if (User32.GetTaskbar() is not { } tb) return;
        var r = tb.Rect;
        if (LiveTaskbarRect(out _taskbarHidden) is { } live && live.Size == r.Size)
            r = live;
        if (tb.Edge is 1 or 3)
        {
            int minX = r.Left + S(6);
            int maxX = Math.Max(minX, r.Right - Width - S(6));
            // In free space between taskbar items when they are known; otherwise the plain offset.
            int x = _taskbarItems is { Count: > 0 } items && TaskbarLayout.Place(r, items, Width, S(6), _settings.OffsetX) is int free
                ? free
                : Math.Clamp(minX + _settings.OffsetX, minX, maxX);
            int y = r.Top + (r.Height - Height) / 2;
            Location = new Point(x, y);
        }
        else
        {
            Location = new Point(r.Left + (r.Width - Width) / 2, r.Top + S(6) + _settings.OffsetX);
        }
        KeepOnTop();
    }

    void KeepOnTop()
    {
        if (!Visible) return;
        User32.SetWindowPos(Handle, User32.HwndTopmost, 0, 0, 0, 0,
            User32.SwpNoMove | User32.SwpNoSize | User32.SwpNoActivate | User32.SwpNoOwnerZOrder);
    }

    void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || hwnd == Handle || (_flyout is not null && hwnd == _flyout.Handle)) return;
        var cls = User32.ClassName(hwnd);

        bool fullscreen = false;
        if (cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            && User32.GetWindowRect(hwnd, out var wr))
        {
            var screen = Screen.FromHandle(hwnd).Bounds;
            var rect = wr.ToRectangle();
            fullscreen = rect.Left <= screen.Left && rect.Top <= screen.Top
                         && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
        }

        if (fullscreen != _fullscreenHidden)
        {
            _fullscreenHidden = fullscreen;
            UpdateVisibility();
            if (!fullscreen) ReleaseHeld();
        }
        else if (cls == "Shell_TrayWnd")
        {
            KeepOnTop();
        }
        // Opening or closing an app adds or removes a taskbar icon and shifts the centred group.
        RefreshTaskbarItems(700);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == RawMouse.WmInput)
            _rawMouse?.Handle(m.LParam);
        else if (m.Msg == WmDeviceChange && (int)m.WParam == DbtDevNodesChanged)
        {
            _deviceChangeTimer.Stop();
            _deviceChangeTimer.Start();
        }
        else if (m.Msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            BeginInvokeSafe(() =>
            {
                HookTaskbar();
                PositionOnTaskbar();
                UpdateVisibility();
                RefreshTaskbarItems(1000);
            });
        }
        else if (m.Msg is WmDisplayChange or WmSettingChange)
        {
            BeginInvokeSafe(() =>
            {
                PositionOnTaskbar();
                UpdateVisibility();
                RefreshTaskbarItems(500);
            });
        }
        else if (m.Msg == WmDpiChanged)
        {
            base.WndProc(ref m);
            ApplyScale(DeviceDpi);
            LayoutChips();
            PositionOnTaskbar();
            Invalidate();
            return;
        }
        base.WndProc(ref m);
    }

    // ---------- mouse ----------

    int HitTest(Point p) => _chips.FindIndex(c => c.Rect.Contains(p));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _mouseDown = true;
        _dragging = false;
        _downAt = Cursor.Position;
        _downOffset = _settings.OffsetX;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mouseDown)
        {
            int dx = Cursor.Position.X - _downAt.X;
            if (!_dragging && Math.Abs(dx) > S(5))
            {
                _dragging = true;
                _tip.Hide(this);
            }
            if (_dragging)
            {
                _settings.OffsetX = Math.Max(0, _downOffset + dx);
                PositionOnTaskbar();
                return;
            }
        }

        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Invalidate();
            _tip.Hide(this);
            if (hit >= 0) _tip.SetToolTip(this, TooltipFor(_chips[hit].Dev));
            else _tip.SetToolTip(this, null);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1)
        {
            _hover = -1;
            Invalidate();
        }
        if (_taskbarHidden) BeginInvokeSafe(UpdateVisibility);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            int at = HitTest(e.Location);
            if (at >= 0 && _chips[at].Dev.Id == AudioChipId) ShowAudioMenu();
            else ShowMenu(at >= 0 && _chips[at].Dev.Id.Length > 0 ? _chips[at].Dev : null);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        bool wasDrag = _dragging;
        _mouseDown = _dragging = false;
        if (wasDrag)
        {
            _settings.Save();
            return;
        }

        int hit = HitTest(e.Location);
        if (hit < 0) return;
        var (rect, dev) = _chips[hit];
        if (dev.Id.Length == 0)
        {
            ShowMenu();
            return;
        }
        if (dev.Id == AudioChipId)
        {
            ToggleAudio();
            return;
        }
        ToggleFlyout(dev, RectangleToScreen(rect));
    }

    void ToggleAudio()
    {
        _tip.Hide(this);
        if (!AudioSwitch.Toggle(_settings)) ShowBalloon(AppInfo.Name, "No other playback device to switch to.");
        OnDevicesChanged();
    }

    string TooltipFor(DeviceStatus d)
    {
        if (d.Id.Length == 0) return "Right-click for options";
        if (d.Id == AudioChipId)
            return $"Playing on {d.Name}" + (_audio?.Next is { } next ? $"\nClick to switch to {next.Name}" : "") + "\nRight-click to choose";
        var state = d.Connected ? "Connected" : $"Not connected · last seen {Theme.Ago(d.LastSeen)}";
        var battery = d.Battery is int b ? $"{b}%" + (d.Charging == true ? " (charging)" : "") : "battery unknown";
        var estimate = d.Battery is int lvl && d.Connected && _monitor.History.Estimate(d.Id, lvl, d.Charging == true) is { } left
            ? $"\n{(d.Charging == true ? "Full in" : "About")} {Theme.Duration(left)}{(d.Charging == true ? "" : " left")}" : "";
        var detail = d.Detail is null ? "" : "\n" + d.Detail;
        var updated = d.Battery is null ? "" : $"\nUpdated {Theme.Ago(d.Updated)}";
        var note = d.Experimental ? "\nExperimental reading" : "";
        return $"{d.Label}\n{battery} · {d.TransportLabel}\n{state}{estimate}{detail}{updated}{note}";
    }

    void ShowAudioMenu()
    {
        _tip.Hide(this);
        var menu = NewMenu();
        var current = CoreAudio.GetDefaultId(render: true);
        foreach (var o in CoreAudio.List(render: true))
        {
            var item = new ToolStripMenuItem(o.Name) { Checked = o.Id == current };
            item.Click += (_, _) =>
            {
                CoreAudio.SetDefault(o.Id);
                OnDevicesChanged();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Choose the two devices to switch between…", null, (_, _) => OpenSettings(SettingsForm.Page.Audio));
        menu.Items.Add("Sound settings", null, (_, _) => OpenUri("ms-settings:sound"));
        menu.Items.Add("Hide this button", null, (_, _) =>
        {
            _settings.AudioChip = false;
            _settings.Save();
            OnDevicesChanged();
        });
        ShowMenuAtCursor(menu);
    }

    ContextMenuStrip NewMenu() => new()
    {
        Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
        ForeColor = Theme.Text,
        ShowImageMargin = false,
        ShowCheckMargin = true,
    };

    void ShowMenuAtCursor(ContextMenuStrip menu)
    {
        foreach (ToolStripItem i in menu.Items) i.ForeColor = Theme.Text;
        menu.Closed += (_, _) => BeginInvokeSafe(menu.Dispose);
        User32.SetForegroundWindow(Handle);
        menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveRight);
    }

    SettingsForm? _settingsForm;

    internal void OpenSettings(SettingsForm.Page page = SettingsForm.Page.General)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.ShowPage(page);
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, _monitor, this, page);
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            Kernel32.TrimWorkingSet();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    /// <summary>Called by the settings window and menus after changing anything that affects the bar.</summary>
    internal void SettingsChanged()
    {
        _settings.TidyPrefs();
        _settings.Save();
        OnDevicesChanged();
    }

    internal DeviceMonitor Monitor => _monitor;
    internal Settings AppSettings => _settings;

    readonly DateTime _started = DateTime.UtcNow;
    bool _updating;

    bool UpdateDue() =>
        _settings.AutoUpdate && Installer.IsInstalled && !_updating &&
        DateTime.UtcNow - _started > TimeSpan.FromMinutes(2) &&
        (_settings.LastUpdateCheck is not DateTime last || DateTime.UtcNow - last > TimeSpan.FromHours(24));

    /// <summary>Checks GitHub for a newer release and, if found, installs it and restarts. Returns a status line.</summary>
    internal async Task<string> CheckForUpdateAsync(bool manual)
    {
        if (_updating) return "Already checking…";
        _updating = true;
        try
        {
            _settings.LastUpdateCheck = DateTime.UtcNow;
            _settings.Save();
            var release = await Installer.CheckAsync();
            if (release is null) return $"Up to date (version {Installer.Version.ToString(3)})";
            if (!await Installer.ApplyAsync(release)) return $"Couldn't install {release.Tag}; see the log.";
            Log.Write($"updated to {release.Tag}; restarting");
            Installer.Restart("--updated");
            Close();
            return $"Updated to {release.Tag}";
        }
        catch (Exception ex)
        {
            Log.Write("update check failed: " + ex.Message);
            return manual ? "Couldn't reach GitHub: " + ex.Message : "";
        }
        finally
        {
            _updating = false;
            Kernel32.TrimWorkingSet();
        }
    }

    internal void RenameDevice(DeviceStatus device)
    {
        var name = InputBox.Show(this, "Rename device", $"Name shown for {device.Name}:", device.Label);
        if (name is null) return;
        _settings.EditPrefs(device.Id).Name = string.IsNullOrWhiteSpace(name) || name.Trim() == device.Name ? null : name.Trim();
        SettingsChanged();
    }

    internal void MoveDevice(string id, int delta)
    {
        DeviceMonitorOrder.Move(_settings, VisibleDevices().ToList(), id, delta);
        SettingsChanged();
    }

    void ToggleFlyout(DeviceStatus dev, Rectangle anchor)
    {
        if (_flyout is not null)
        {
            var same = _flyout.DeviceId == dev.Id;
            _flyout.Close();
            if (same) return;
        }
        _tip.Hide(this);
        _flyout = new FlyoutForm(dev, anchor, this);
        _flyout.FormClosed += (_, _) =>
        {
            _flyout = null;
            if (_taskbarHidden) BeginInvokeSafe(UpdateVisibility);
            Kernel32.TrimWorkingSet();
        };
        _flyout.Show();
        _flyout.Activate();
    }

    internal void HideDevice(string id)
    {
        _settings.Hidden.Add(id);
        _settings.Save();
        OnDevicesChanged();
    }

    internal void RefreshSoon(int delayMs)
    {
        var t = new System.Windows.Forms.Timer { Interval = delayMs };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            _ = _monitor.RefreshAsync(true);
        };
        t.Start();
    }

    // ---------- context menu ----------

    void ShowMenu(DeviceStatus? device = null)
    {
        _tip.Hide(this);
        var menu = NewMenu();

        if (device is not null)
        {
            menu.Items.Add(new ToolStripMenuItem(device.Label) { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
            if (DeviceControl.CanDisconnect(device))
                menu.Items.Add(DeviceControl.DisconnectLabel(device), null, async (_, _) => await RunDeviceAction(DeviceControl.DisconnectAsync(device), device, "disconnect"));
            else if (DeviceControl.CanConnect(device))
                menu.Items.Add("Connect", null, async (_, _) => await RunDeviceAction(DeviceControl.ConnectAsync(device), device, "connect"));
            menu.Items.Add("Rename…", null, (_, _) => RenameDevice(device));
            menu.Items.Add("Move left", null, (_, _) => MoveDevice(device.Id, -1));
            menu.Items.Add("Move right", null, (_, _) => MoveDevice(device.Id, +1));

            var prefs = _settings.Prefs(device.Id);
            var alerts = new ToolStripMenuItem("Alerts");
            foreach (var v in new int?[] { null, 0, 10, 15, 20, 30 })
            {
                var text = v switch { null => $"Low battery: default ({_settings.AlertThreshold}%)", 0 => "Low battery: off", _ => $"Low battery: below {v}%" };
                var item = new ToolStripMenuItem(text) { Checked = prefs.LowAlert == v, ForeColor = Theme.Text };
                item.Click += (_, _) =>
                {
                    _settings.EditPrefs(device.Id).LowAlert = v;
                    SettingsChanged();
                };
                alerts.DropDownItems.Add(item);
            }
            alerts.DropDownItems.Add(new ToolStripSeparator());
            var full = new ToolStripMenuItem("Notify when fully charged") { Checked = prefs.FullAlert, ForeColor = Theme.Text };
            full.Click += (_, _) =>
            {
                _settings.EditPrefs(device.Id).FullAlert = !prefs.FullAlert;
                SettingsChanged();
            };
            alerts.DropDownItems.Add(full);
            menu.Items.Add(alerts);

            menu.Items.Add("Hide from bar", null, (_, _) => HideDevice(device.Id));
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add("Refresh now", null, (_, _) => _ = _monitor.RefreshAsync(true));
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());

        var compact = new ToolStripMenuItem("Compact mode") { Checked = _settings.Compact, CheckOnClick = true };
        compact.CheckedChanged += (_, _) =>
        {
            _settings.Compact = compact.Checked;
            SettingsChanged();
        };
        menu.Items.Add(compact);

        var audioChip = new ToolStripMenuItem("Show audio switch") { Checked = _settings.AudioChip, CheckOnClick = true };
        audioChip.CheckedChanged += (_, _) =>
        {
            _settings.AudioChip = audioChip.Checked;
            SettingsChanged();
        };
        menu.Items.Add(audioChip);

        var devices = new ToolStripMenuItem("Show on bar");
        foreach (var d in _devices)
        {
            var item = new ToolStripMenuItem(d.Label) { Checked = !_settings.Hidden.Contains(d.Id), CheckOnClick = true, ForeColor = Theme.Text };
            item.CheckedChanged += (_, _) =>
            {
                if (item.Checked) _settings.Hidden.Remove(d.Id); else _settings.Hidden.Add(d.Id);
                _settings.Save();
                OnDevicesChanged();
            };
            devices.DropDownItems.Add(item);
        }
        if (devices.DropDownItems.Count == 0) devices.Enabled = false;
        menu.Items.Add(devices);

        var showDisconnected = new ToolStripMenuItem("Show disconnected devices") { Checked = _settings.ShowDisconnected, CheckOnClick = true };
        showDisconnected.CheckedChanged += (_, _) =>
        {
            _settings.ShowDisconnected = showDisconnected.Checked;
            _settings.Save();
            OnDevicesChanged();
        };
        menu.Items.Add(showDisconnected);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Bluetooth settings", null, (_, _) => OpenUri("ms-settings:bluetooth"));
        menu.Items.Add("Exit", null, (_, _) => Close());

        ShowMenuAtCursor(menu);
    }

    internal void ResetPosition() => SetOffset(0);

    internal void SetOffset(int offset)
    {
        _settings.OffsetX = Math.Max(0, offset);
        _settings.Save();
        PositionOnTaskbar();
    }

    internal void SetAvoidTaskbarItems(bool avoid)
    {
        _settings.AvoidTaskbarItems = avoid;
        _settings.Save();
        if (!avoid) _taskbarItems = null;
        PositionOnTaskbar();
        RefreshTaskbarItems();
    }

    List<Rectangle>? _taskbarItems;
    int _layoutRequest;

    /// <summary>
    /// Re-reads where the taskbar's items are, off the UI thread, then moves the bar if they changed.
    /// Runs at start, on the minute poll and shortly after the foreground window changes (opening or
    /// closing an app adds or removes an icon and shifts the centred group). Calls inside the delay collapse.
    /// </summary>
    internal async void RefreshTaskbarItems(int delayMs = 0, bool confirming = false)
    {
        int request = ++_layoutRequest;
        if (delayMs > 0) await Task.Delay(delayMs);
        if (request != _layoutRequest || IsDisposed || !_settings.AvoidTaskbarItems || _trayHwnd == IntPtr.Zero) return;
        var tray = _trayHwnd;
        var items = await Task.Run(() => TaskbarLayout.Occupied(tray));
        if (IsDisposed || items is null || !_settings.AvoidTaskbarItems) return;
        if (_taskbarItems is not null && _taskbarItems.SequenceEqual(items)) return;
        // While a shell panel (Quick Settings, Start) opens, Explorer briefly reports an empty or partial
        // taskbar. Acting on that would slide the bar over the weather and back, so keep the last good
        // layout and look again shortly; a real large change (many apps closed) is still there then.
        bool suspicious = items.Count == 0 || (_taskbarItems is { Count: > 0 } known && items.Count < known.Count / 2);
        if (suspicious && !confirming)
        {
            RefreshTaskbarItems(1000, confirming: true);
            return;
        }
        if (items.Count == 0) return;
        _taskbarItems = items;
        PositionOnTaskbar();
    }

    async Task RunDeviceAction(Task<bool> action, DeviceStatus device, string verb)
    {
        bool ok = await action;
        if (!ok) ShowBalloon(AppInfo.Name, $"Couldn't {verb} {device.Label}. Try Bluetooth settings.");
        RefreshSoon(3000);
    }

    internal static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("open failed: " + ex.Message);
        }
    }

    // ---------- alerts ----------

    void ShowAlert(DeviceStatus d, AlertKind kind)
    {
        if (kind == AlertKind.Full)
            Notify($"full:{d.Id}", "Fully charged", $"{d.Label} is at 100% — you can unplug it", ToolTipIcon.Info);
        else if (kind == AlertKind.Health)
        {
            if (_monitor.History.Drain(d.Id) is { Current: double now } drain)
                Notify($"health:{d.Id}", "Draining faster than usual",
                    $"{d.Label} is losing about {Theme.Rate(now)} this charge, usually {Theme.Rate(drain.Typical)}.", ToolTipIcon.Info);
        }
        else
            Notify($"low:{d.Id}", "Low battery", $"{d.Label} is at {d.Battery}%", ToolTipIcon.Warning,
                () => _devices.FirstOrDefault(x => x.Id == d.Id)?.Charging != true);
    }

    readonly record struct Held(string Key, string Title, string Text, ToolTipIcon Icon, Func<bool>? StillValid);
    readonly List<Held> _held = new();

    bool Quiet => (_settings.QuietFullscreen && _fullscreenHidden) || _settings.InQuietHours(DateTime.Now);

    /// <summary>Shows an automatic notification now, or holds it (latest per key) until quiet time ends.</summary>
    void Notify(string key, string title, string text, ToolTipIcon icon, Func<bool>? stillValid = null)
    {
        if (!Quiet)
        {
            ShowBalloon(title, text, icon);
            return;
        }
        _held.RemoveAll(h => h.Key == key);
        _held.Add(new Held(key, title, text, icon, stillValid));
    }

    void ReleaseHeld()
    {
        if (_held.Count == 0 || Quiet) return;
        var due = _held.Where(h => h.StillValid?.Invoke() != false).ToList();
        _held.Clear();
        if (due.Count == 1)
            ShowBalloon(due[0].Title, due[0].Text, due[0].Icon);
        else if (due.Count > 1)
            ShowBalloon("While you were busy", string.Join("\n", due.Select(h => $"{h.Title}: {h.Text}")),
                due.Any(h => h.Icon == ToolTipIcon.Warning) ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    void CheckChargeReminder()
    {
        var now = DateTime.Now;
        if (!_settings.ChargeReminder || now.Hour * 60 + now.Minute < _settings.ChargeReminderAt
            || _settings.LastChargeReminder?.Date == now.Date || Quiet) return;
        _settings.LastChargeReminder = now;
        _settings.Save();
        ShowChargeReminder(always: false);
    }

    /// <summary>Lists devices under the reminder level; with <paramref name="always"/> it also reports when there are none.</summary>
    internal void ShowChargeReminder(bool always)
    {
        var low = _devices
            .Where(d => d.Battery is int b && b < _settings.ChargeReminderBelow && d.Charging != true && !_settings.Hidden.Contains(d.Id))
            .OrderBy(d => d.Battery)
            .ToList();
        if (low.Count > 0)
            ShowBalloon("Charge tonight", string.Join("\n", low.Select(d => $"{d.Label} — {d.Battery}%{(d.Connected ? "" : " (last seen)")}")), ToolTipIcon.Info);
        else if (always)
            ShowBalloon("Charge tonight", $"Nothing is below {_settings.ChargeReminderBelow}% right now.", ToolTipIcon.Info);
    }

    internal void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Warning)
    {
        _notify ??= new NotifyIcon { Icon = AppIcon(), Text = AppInfo.Name };
        _notify.Visible = true;
        if (text.Length > 250) text = text[..249] + "…";
        _notify.ShowBalloonTip(8000, title, text, icon);
        var t = new System.Windows.Forms.Timer { Interval = 15_000 };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            if (_notify is not null) _notify.Visible = false;
        };
        t.Start();
    }

    static Icon AppIcon()
    {
        try
        {
            // Ask for the tray size so Windows uses the pixel-tuned small image instead of shrinking the 32 px one.
            if (Environment.ProcessPath is string exe
                && (Icon.ExtractIcon(exe, 0, SystemInformation.SmallIconSize.Width) ?? Icon.ExtractAssociatedIcon(exe)) is Icon icon)
                return icon;
        }
        catch (ArgumentException) { }
        return SystemIcons.Information;
    }
}
