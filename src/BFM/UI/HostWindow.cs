namespace Bfm.UI;

/// <summary>
/// Hidden top-level window that stays put while the bar itself may live inside Explorer's taskbar as a
/// child window. Child windows never receive broadcasts (device changes, TaskbarCreated, setting and
/// display changes), can't be the foreground window for a menu, and would make Explorer's taskbar the
/// owner of any dialog, so all of that goes through this window instead.
/// </summary>
internal sealed class HostWindow : NativeWindow
{
    readonly BarForm _bar;

    public HostWindow(BarForm bar)
    {
        _bar = bar;
        const int WsPopup = unchecked((int)0x80000000), WsExToolWindow = 0x80;
        CreateHandle(new CreateParams { Caption = "Juice Bar host", Style = WsPopup, ExStyle = WsExToolWindow });
    }

    protected override void WndProc(ref Message m)
    {
        if (_bar.OnHostMessage(ref m)) return;
        base.WndProc(ref m);
    }
}
