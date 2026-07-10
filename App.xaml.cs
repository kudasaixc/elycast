using System.Windows;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Services;

namespace Elysium_Cast_IPTV;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 0) load persisted settings + favourites, apply the accent colour
        StateStore.Load();
        ThemeManager.Apply(StateStore.Settings.AccentColor);

        // 1) bring up the debug console immediately
        DebugConsole.Initialize();

        // 2) play the animated boot sequence (~5.5s). Backend-specific native
        //    libraries are initialized later by the selected video backend.
        DebugConsole.RunBootSequence(StateStore.Settings.BootSeconds, () =>
        {
            DebugConsole.Info("Préparation du shell ElyCast...");
            DebugConsole.Success("Runtime prêt.");
        });

        // 3) show the UI
        DispatcherUnhandledException += OnUnhandledException;
        var window = new MainWindow();
        window.Show();

        // 4) interactive command interpreter
        DebugConsole.StartCommandLoop();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "elycast_error.txt"),
            e.Exception.ToString()); } catch { }
        DebugConsole.Error("Exception non gérée : " + e.Exception);
        MessageBox.Show(e.Exception.Message, "ElyCast – Erreur",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
