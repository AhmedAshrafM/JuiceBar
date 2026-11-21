using Bfm.Core;
using Bfm.Interop;
using Bfm.Providers;

namespace Bfm.UI;

/// <summary>
/// Settings window, built only while open (the bar itself never holds these controls).
/// Every change applies and saves immediately; there is no OK/Apply step.
/// </summary>
internal sealed class SettingsForm : Form
{
    public enum Page { General, Alerts, Devices, Audio }

    readonly Settings _settings;
    readonly DeviceMonitor _monitor;
    readonly BarForm _bar;
    readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 14), BackColor = Theme.Panel };
    readonly Dictionary<Page, Button> _tabs = new();
    Page _page;

    public SettingsForm(Settings settings, DeviceMonitor monitor, BarForm bar, Page page)
    {
        _settings = settings;
        _monitor = monitor;
        _bar = bar;

        Text = $"{AppInfo.Name} settings";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 590);
        ShowIcon = false;

        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(14, 10, 14, 0), BackColor = Theme.Panel };
        foreach (var p in Enum.GetValues<Page>())
        {
            var b = MakeButton(p.ToString(), 96);
            b.Margin = new Padding(0, 0, 6, 0);
            b.Click += (_, _) => ShowPage(p);
            _tabs[p] = b;
            tabs.Controls.Add(b);
        }
        Controls.Add(_body);
        Controls.Add(tabs);
        ShowPage(page);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        User32.ApplyDarkRoundedFrame(Handle);
    }

    public void ShowPage(Page page)
    {
        _page = page;
        foreach (var (p, b) in _tabs)
        {
            b.BackColor = p == page ? Theme.ChipHover : Theme.Button;
            b.FlatAppearance.BorderColor = p == page ? Theme.Accent : Theme.Border;
        }
        SuspendLayout();
        foreach (Control c in _body.Controls.Cast<Control>().ToList()) c.Dispose();
        _body.Controls.Clear();
        switch (page)
        {
            case Page.General: BuildGeneral(); break;
            case Page.Alerts: BuildAlerts(); break;
            case Page.Devices: BuildDevices(); break;
            case Page.Audio: BuildAudio(); break;
        }
        ResumeLayout();
    }

    void Changed() => _bar.SettingsChanged();

    // ---------- General ----------

    void BuildGeneral()
    {
        var col = Column();

        col.Controls.Add(Heading("Startup"));
        col.Controls.Add(Check("Start with Windows", Installer.IsStartupEnabled(), Installer.SetStartup));

        col.Controls.Add(Heading("Bar"));
        col.Controls.Add(Check("Compact mode (icon and number only)", _settings.Compact, v => { _settings.Compact = v; Changed(); }));
        col.Controls.Add(Row("In compact mode, hide devices above",
            Number(0, 100, _settings.CompactHideAbove, v => { _settings.CompactHideAbove = v; Changed(); }, step: 5),
            Note("% (0 shows all)")));
        col.Controls.Add(Check("Show disconnected devices", _settings.ShowDisconnected, v => { _settings.ShowDisconnected = v; Changed(); }));
        col.Controls.Add(Check("Show the audio output switch", _settings.AudioChip, v => { _settings.AudioChip = v; Changed(); }));
        col.Controls.Add(Check("Embed in the taskbar (stays visible over Start and Quick Settings)", _settings.EmbedInTaskbar, _bar.SetEmbedded));
        var reset = MakeButton("Reset bar position", 150);
        reset.Click += (_, _) => _bar.ResetPosition();
        col.Controls.Add(reset);

        col.Controls.Add(Heading("Polling"));
        col.Controls.Add(Row("Check USB dongles every",
            Number(1, 30, _settings.DonglePollMinutes, v => { _settings.DonglePollMinutes = v; Changed(); }),
            Note("minutes")));

        col.Controls.Add(Heading("Updates"));
        col.Controls.Add(Check("Install updates automatically", _settings.AutoUpdate, v => { _settings.AutoUpdate = v; Changed(); }));
        var status = Note($"Version {Installer.Version.ToString(3)}{(Installer.IsInstalled ? "" : " · not installed")}");
        var check = MakeButton("Check now", 110);
        check.Enabled = Installer.IsInstalled;
        check.Click += async (_, _) =>
        {
            check.Enabled = false;
            status.Text = "Checking…";
            status.Text = await _bar.CheckForUpdateAsync(manual: true);
            if (!check.IsDisposed) check.Enabled = true;
        };
        col.Controls.Add(Row(null, check, status));
    }

    // ---------- Alerts ----------

    void BuildAlerts()
    {
        var col = Column();

        col.Controls.Add(Heading("Battery alerts"));
        col.Controls.Add(Row("Default low-battery alert below",
            Number(0, 50, _settings.AlertThreshold, v => { _settings.AlertThreshold = v; Changed(); }, step: 5),
            Note("% (0 = off)")));
        col.Controls.Add(Note("Per-device levels and full-charge alerts are on the Devices page."));

        col.Controls.Add(Heading("Charge tonight"));
        col.Controls.Add(Check("Remind me daily which devices need charging", _settings.ChargeReminder,
            v => { _settings.ChargeReminder = v; Changed(); }));
        col.Controls.Add(Row("At", 120, TimeCombo(_settings.ChargeReminderAt, v =>
        {
            _settings.ChargeReminderAt = v;
            // Allow today's reminder again when the time moves later than now.
            if (v > DateTime.Now.Hour * 60 + DateTime.Now.Minute) _settings.LastChargeReminder = null;
            Changed();
        })));
        col.Controls.Add(Row("List devices below", 120,
            Number(5, 90, _settings.ChargeReminderBelow, v => { _settings.ChargeReminderBelow = v; Changed(); }, step: 5),
            Note("%")));
        var preview = MakeButton("Show it now", 110);
        preview.Click += (_, _) => _bar.ShowChargeReminder(always: true);
        col.Controls.Add(preview);

        col.Controls.Add(Heading("Battery health"));
        col.Controls.Add(Check("Tell me when a device drains much faster than usual", _settings.HealthAlerts,
            v => { _settings.HealthAlerts = v; Changed(); }));
        col.Controls.Add(Note($"{BatteryHistory.DrainInfo.FasterRatio}× its earlier charges or more; at most once per charge."));

        col.Controls.Add(Heading("Quiet time"));
        col.Controls.Add(Check("Hold notifications while a fullscreen app or game is open", _settings.QuietFullscreen,
            v => { _settings.QuietFullscreen = v; Changed(); }));
        col.Controls.Add(Check("Quiet hours", _settings.QuietHours, v => { _settings.QuietHours = v; Changed(); }));
        col.Controls.Add(Row("From", 120,
            TimeCombo(_settings.QuietFrom, v => { _settings.QuietFrom = v; Changed(); }),
            Note("to"),
            TimeCombo(_settings.QuietTo, v => { _settings.QuietTo = v; Changed(); })));
        col.Controls.Add(Note("Held notifications appear together once quiet time ends."));
    }

    static ComboBox TimeCombo(int minutes, Action<int> set)
    {
        var slots = Enumerable.Range(0, 48).Select(i => i * 30).ToArray();
        int index = Array.FindIndex(slots, s => s >= minutes);
        var combo = Combo(slots.Select(s => $"{s / 60:00}:{s % 60:00}"), index < 0 ? 0 : index, i => set(slots[i]));
        combo.Width = 80;
        return combo;
    }

    // ---------- Devices ----------

    void BuildDevices()
    {
        var devices = _monitor.Snapshot().ToList();
        var list = new ListBox
        {
            Bounds = new Rectangle(0, 8, 210, 400),
            BackColor = Theme.Chip,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 30,
        };
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var d = devices[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(selected ? Theme.ChipHover : Theme.Chip)) e.Graphics.FillRectangle(bg, e.Bounds);
            var hidden = _settings.Hidden.Contains(d.Id);
            TextRenderer.DrawText(e.Graphics, d.Label, list.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 52, e.Bounds.Height),
                hidden || !d.Connected ? Theme.TextDim : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, d.Battery is int b ? $"{b}%" : "–", list.Font, new Rectangle(e.Bounds.Right - 46, e.Bounds.Y, 40, e.Bounds.Height),
                Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        };
        foreach (var d in devices) list.Items.Add(d.Id);

        var up = MakeButton("▲", 40);
        up.Location = new Point(0, 416);
        var down = MakeButton("▼", 40);
        down.Location = new Point(46, 416);
        var orderNote = Note("bar order");
        orderNote.Location = new Point(92, 422);

        var editor = new Panel { Bounds = new Rectangle(224, 8, 260, 440), BackColor = Theme.Panel };
        _body.Controls.AddRange(new Control[] { list, up, down, orderNote, editor });

        void Move(int delta)
        {
            int i = list.SelectedIndex;
            if (i < 0 || i + delta < 0 || i + delta >= devices.Count) return;
            DeviceMonitorOrder.Move(_settings, devices, devices[i].Id, delta);
            (devices[i], devices[i + delta]) = (devices[i + delta], devices[i]);
            list.Items[i] = devices[i].Id;
            list.Items[i + delta] = devices[i + delta].Id;
            list.SelectedIndex = i + delta;
            Changed();
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);

        list.SelectedIndexChanged += (_, _) =>
        {
            foreach (Control c in editor.Controls.Cast<Control>().ToList()) c.Dispose();
            editor.Controls.Clear();
            if (list.SelectedIndex >= 0) BuildDeviceEditor(editor, devices[list.SelectedIndex], () => list.Invalidate());
        };
        if (devices.Count > 0) list.SelectedIndex = 0;
        else editor.Controls.Add(Note("No devices yet."));
    }

    void BuildDeviceEditor(Panel host, DeviceStatus d, Action redrawList)
    {
        var col = Column();
        col.Dock = DockStyle.Fill;
        col.Padding = Padding.Empty;
        host.Controls.Add(col);
        var prefs = _settings.Prefs(d.Id);

        col.Controls.Add(Note($"{d.Name} · {d.Transport}{(d.Connected ? " · connected" : "")}"));

        col.Controls.Add(Heading("Name on the bar"));
        var name = new TextBox
        {
            Text = prefs.Name ?? "",
            PlaceholderText = d.Name,
            Width = 250,
            BackColor = Theme.Button,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        name.TextChanged += (_, _) =>
        {
            var v = name.Text.Trim();
            _settings.EditPrefs(d.Id).Name = v.Length == 0 || v == d.Name ? null : v;
            Changed();
            redrawList();
        };
        col.Controls.Add(name);

        col.Controls.Add(Heading("Alerts"));
        var levels = new (string Text, int? Value)[]
        {
            ($"Default ({_settings.AlertThreshold}%)", null), ("Off", 0), ("Below 5%", 5), ("Below 10%", 10),
            ("Below 15%", 15), ("Below 20%", 20), ("Below 25%", 25), ("Below 30%", 30), ("Below 40%", 40), ("Below 50%", 50),
        };
        var low = Combo(levels.Select(l => l.Text), Math.Max(0, Array.FindIndex(levels, l => l.Value == prefs.LowAlert)), i =>
        {
            _settings.EditPrefs(d.Id).LowAlert = levels[i].Value;
            Changed();
        });
        col.Controls.Add(Row("Low battery", low));
        col.Controls.Add(Check("Notify when fully charged", prefs.FullAlert, v => { _settings.EditPrefs(d.Id).FullAlert = v; Changed(); }));

        col.Controls.Add(Heading("Bar"));
        col.Controls.Add(Check("Show on the bar", !_settings.Hidden.Contains(d.Id), v =>
        {
            if (v) _settings.Hidden.Remove(d.Id); else _settings.Hidden.Add(d.Id);
            Changed();
            redrawList();
        }));

        if (d.Kind == DeviceKind.Controller && d.Name.StartsWith("DualSense", StringComparison.Ordinal))
        {
            col.Controls.Add(Heading("Lightbar"));
            var swatch = new Panel { Size = new Size(26, 26), Margin = new Padding(0, 3, 8, 0), BorderStyle = BorderStyle.FixedSingle };
            void Paint() => swatch.BackColor = prefs.Lightbar is string h ? ColorTranslator.FromHtml("#" + h) : Theme.Chip;
            Paint();
            var pick = MakeButton("Choose colour…", 120);
            pick.Click += (_, _) =>
            {
                using var dlg = new ColorDialog { FullOpen = true, Color = swatch.BackColor };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var hex = $"{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                prefs = _settings.EditPrefs(d.Id);
                prefs.Lightbar = hex;
                Paint();
                Changed();
                if (d.Connected) _ = Task.Run(() => DualSenseProvider.SetLightbar(d.Id, hex));
            };
            var clear = MakeButton("Default", 70);
            clear.Click += (_, _) =>
            {
                _settings.EditPrefs(d.Id).Lightbar = null;
                prefs = _settings.Prefs(d.Id);
                Paint();
                Changed();
            };
            col.Controls.Add(Row(null, swatch, pick, clear));
            col.Controls.Add(Note("Re-applied whenever the controller connects.\n\"Default\" takes effect on the next reconnect."));
        }

        var forget = MakeButton("Forget device", 120);
        forget.Margin = new Padding(0, 16, 0, 0);
        forget.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Remove {d.Label} and its battery history from {AppInfo.Name}?\nIt comes back if it reports a battery again.",
                    AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _monitor.Forget(d.Id);
            _settings.Devices.Remove(d.Id);
            _settings.Order.Remove(d.Id);
            Changed();
            ShowPage(Page.Devices);
        };
        col.Controls.Add(forget);
    }

    // ---------- Audio ----------

    void BuildAudio()
    {
        var col = Column();
        var outputs = CoreAudio.List(render: true);
        col.Controls.Add(Heading("Audio switch"));
        col.Controls.Add(Note("Clicking the audio button on the bar flips between these two outputs.\n" +
                              "\"Automatic\" picks the headset and the speakers by name."));

        ComboBox Picker(string? current, Action<string?> set)
        {
            var names = new List<string> { "Automatic" };
            names.AddRange(outputs.Select(o => o.Name));
            int index = Math.Max(0, outputs.FindIndex(o => o.Id == current) + 1);
            var combo = Combo(names, index, i =>
            {
                set(i == 0 ? null : outputs[i - 1].Id);
                Changed();
            });
            combo.Width = 330;
            return combo;
        }

        col.Controls.Add(Row("First", 60, Picker(_settings.AudioA, v => _settings.AudioA = v)));
        col.Controls.Add(Row("Second", 60, Picker(_settings.AudioB, v => _settings.AudioB = v)));
        if (outputs.Count == 0) col.Controls.Add(Note("No active playback devices found."));
        col.Controls.Add(Check("Show the audio switch on the bar", _settings.AudioChip, v => { _settings.AudioChip = v; Changed(); }));
        var sound = MakeButton("Windows sound settings", 180);
        sound.Margin = new Padding(0, 12, 0, 0);
        sound.Click += (_, _) => BarForm.OpenUri("ms-settings:sound");
        col.Controls.Add(sound);
    }

    // ---------- control helpers ----------

    FlowLayoutPanel Column()
    {
        var col = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Panel,
        };
        _body.Controls.Add(col);
        return col;
    }

    static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Accent,
        Font = new Font("Segoe UI Semibold", 9.5f),
        Margin = new Padding(0, 12, 0, 4),
    };

    static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.TextDim,
        Margin = new Padding(0, 6, 0, 2),
    };

    static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 3, 0, 3) };
        c.CheckedChanged += (_, _) => set(c.Checked);
        return c;
    }

    static NumericUpDown Number(int min, int max, int value, Action<int> set, int step = 1)
    {
        var n = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = step,
            Value = Math.Clamp(value, min, max),
            Width = 60,
            BackColor = Theme.Button,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        n.ValueChanged += (_, _) => set((int)n.Value);
        return n;
    }

    static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> set)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Button,
            ForeColor = Theme.Text,
            Width = 150,
        };
        c.Items.AddRange(items.Cast<object>().ToArray());
        if (selected < c.Items.Count) c.SelectedIndex = selected;
        c.SelectedIndexChanged += (_, _) => set(c.SelectedIndex);
        return c;
    }

    static FlowLayoutPanel Row(string? label, params Control[] controls) => Row(label, 0, controls);

    static FlowLayoutPanel Row(string? label, int labelWidth, params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2), BackColor = Theme.Panel };
        if (label is not null)
            row.Controls.Add(new Label { Text = label, AutoSize = true, MinimumSize = new Size(labelWidth, 0), ForeColor = Theme.Text, Margin = new Padding(0, 6, 8, 0) });
        foreach (var c in controls)
        {
            if (c is Label l) l.Margin = new Padding(6, 6, 0, 0);
            row.Controls.Add(c);
        }
        return row;
    }

    internal static Button MakeButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Button,
            ForeColor = Theme.Text,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.ChipHover;
        return b;
    }
}
