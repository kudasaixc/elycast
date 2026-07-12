using System.Windows;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Services;

namespace Elysium_Cast_IPTV;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The onboarding wizard is the first (and briefly the only) window: with
        // the default OnLastWindowClose policy, closing it shuts the app down
        // before MainWindow is even created. Stay explicit until the real main
        // window exists.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 0) load persisted settings + favourites, apply the accent colour
        StateStore.Load();
        // Opt-in renderer validation runs must be reproducible on a clean
        // machine and must not persist test preferences into the user's state.
        // The diagnostic media hook below already scopes the process to a local
        // file; this companion selector bypasses onboarding and pins ELYCORE.
        var diagnosticFile = Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_FILE");
        var diagnosticAudioRenderer = Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_AUDIO_RENDERER");
        if (!string.IsNullOrWhiteSpace(diagnosticFile) &&
            diagnosticAudioRenderer is "classic" or "audiocore")
        {
            StateStore.SuppressSaves = true;
            StateStore.Settings.OnboardingCompleted = true;
            StateStore.Settings.PreferredConnection = "local";
            StateStore.Settings.VideoBackend = "elycore";
            StateStore.Settings.AudioVisualizerRenderer = diagnosticAudioRenderer;
        }
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

        // First run: the onboarding wizard collects the profile, connection
        // method, accent colour and content tastes, detects the hardware and
        // downloads the missing dependencies before the player appears.
        if (!StateStore.Settings.OnboardingCompleted)
        {
            DebugConsole.Step("Premier lancement détecté — ouverture de l'assistant de configuration…");
            try { new OnboardingWindow().ShowDialog(); }
            catch (Exception ex)
            {
                DebugConsole.Exception("Onboarding : échec de l'assistant, configuration par défaut", ex);
                StateStore.Settings.OnboardingCompleted = true;
                StateStore.Save();
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
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
