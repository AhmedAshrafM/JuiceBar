using Bfm.Core;
using Bfm.Interop;
using Bfm.Providers;
using Bfm.UI;

namespace Bfm;

internal static class Program
{
    internal static IReadOnlyList<IBatteryProvider> CreateProviders() => new IBatteryProvider[]
    {
        new BluetoothProvider(),
        new DualSenseProvider(),
        new AirPodsProvider(),
        new HyperXCloud3Provider(),
        new LogitechHidppProvider(),
        new CompxReceiverProvider(),
        new UgreenReceiverProvider(),
    };

    [STAThread]
    static int Main(string[] args)
    {
        bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

        if (Has("--probe"))
            return Probe.Run();
        if (args.Length >= 3 && args[0].Equals("--listen", StringComparison.OrdinalIgnoreCase))
            return Probe.Listen(args[1], int.Parse(args[2]));
        if (args.Length >= 3 && args[0].Equals("--lightbar", StringComparison.OrdinalIgnoreCase))
        {
            Log.Verbose = true;
            return DualSenseProvider.SetLightbar(args[1], args[2]) ? 0 : 1;
        }

        ApplicationConfiguration.Initialize();
        if (Has("--uninstall"))
            return Installer.Uninstall(Has("--quiet"));
        if (Has("--install"))
            return Installer.Install(Has("--quiet"));
        if (Installer.IsSingleFile && !Installer.IsInstalled && !Has("--portable") && Installer.OfferInstall())
            return 0;

        using var mutex = new Mutex(true, $@"Local\{AppInfo.Id}.BatteryBar", out bool firstInstance);
        // After a self-update the old process may still be closing; wait for it rather than exiting.
        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{AppInfo.Id}.ShowSettings");
        if (!firstInstance && !(Has("--updated") && WaitForMutex(mutex)))
        {
            AllowSetForegroundWindow(-1);
            showSettings.Set();
            return 0;
        }

        // The app used to be called BFM; take over its data (and, when installed, its install) once it isn't running.
        if (System.Diagnostics.Process.GetProcessesByName(AppInfo.LegacyId).Length == 0)
        {
            Settings.MigrateLegacyFolder();
            if (Installer.IsInstalled && Installer.RemoveLegacyInstall()) Installer.SetStartup(true);
        }
        if (Installer.IsInstalled)
        {
            Installer.CleanupAfterUpdate();
            Installer.RepairStartup();
        }

        var settings = Settings.Load();
        DualSenseProvider.LightbarFor = id => settings.Prefs(id).Lightbar;
        UgreenReceiverProvider.FindPartner = () =>
        {
            lock (settings.SyncRoot)
            {
                var match = settings.Cache.FirstOrDefault(kv => kv.Key.StartsWith("bt:", StringComparison.Ordinal)
                    && kv.Value.Kind == DeviceKind.Mouse && kv.Value.Name.StartsWith("UGREEN", StringComparison.OrdinalIgnoreCase));
                return match.Key is null ? null : (match.Key, match.Value.Name);
            }
        };
        var monitor = new DeviceMonitor(settings, CreateProviders());
        var bar = new BarForm(monitor, settings) { JustUpdated = Has("--updated") };
        var wait = ThreadPool.RegisterWaitForSingleObject(showSettings,
            (_, _) => bar.BeginInvokeSafe(() => bar.OpenSettings()), null, Timeout.Infinite, executeOnlyOnce: false);
        // Not Application.Run(bar): while embedded, the bar's window is a child of Explorer's taskbar and is
        // destroyed when Explorer restarts, which would end a run tied to it. Only closing the bar (Exit, an
        // update restart) ends the app; the bar re-attaches to the new taskbar on its own.
        var context = new ApplicationContext();
        bar.FormClosed += (_, _) => context.ExitThread();
        bar.Show();
        Application.Run(context);
        wait.Unregister(null);
        settings.Save();
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    static bool WaitForMutex(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}
