using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.ElySmart;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV;

/// <summary>
/// First-run wizard: profile name, connection method, accent colour, content
/// preferences, an ELYSMART measured benchmark with explainable recommendations, then the
/// dependency download (libmpv, shaders) and the RTX VSR functional test.
/// Nothing is persisted until the user finishes or skips.
/// </summary>
public partial class OnboardingWindow : Window
{
    private int _step;
    private string _connection = "xtream";
    private string _accentHex = StateStore.Settings.AccentColor;
    private HardwareSnapshot? _hardware;
    private BenchmarkReport? _elySmartReport;
    private CancellationTokenSource? _elySmartCts;
    private bool _hardwareProbed;
    private bool _installDone;
    private bool _installBusy;

    private StackPanel[] Steps => new[] { Step1, Step2, Step3, Step4, Step5, Step6 };

    public OnboardingWindow()
    {
        InitializeComponent();
        NameBox.Text = StateStore.Settings.UserDisplayName;
        AccentList.ItemsSource = ThemeManager.Presets
            .Select(hex => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)))
            .ToList();
        ConnXtream.IsChecked = true;
        EngineCombo.SelectionChanged += (_, _) => UpdateEngineToggles();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Skip_Click(this, new RoutedEventArgs()); };
        ShowStep(0);
    }

    private void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            try { DragMove(); } catch { /* resize edge */ }
    }

    // ---------------------------------------------------------------- steps
    private void ShowStep(int index)
    {
        _step = Math.Clamp(index, 0, Steps.Length - 1);
        var steps = Steps;
        for (var i = 0; i < steps.Length; i++)
            steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < StepList.Children.Count; i++)
            if (StepList.Children[i] is TextBlock label)
            {
                label.Foreground = i == _step
                    ? (Brush)FindResource("TextBrush")
                    : (Brush)FindResource("MutedBrush");
                label.FontWeight = i == _step ? FontWeights.SemiBold : FontWeights.Normal;
            }

        BackBtn.Visibility = _step > 0 ? Visibility.Visible : Visibility.Hidden;
        if (_step == Steps.Length - 1)
        {
            NextBtn.Content = "Terminer";
            NextBtn.IsEnabled = _installDone;
        }
        else
        {
            NextBtn.Content = "Continuer";
            NextBtn.IsEnabled = true;
        }

        if (_step == 4) _ = RunElySmartAsync();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Steps.Length - 1)
        {
            Finish();
            return;
        }
        ShowStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 4) _elySmartCts?.Cancel();
        ShowStep(_step - 1);
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _elySmartCts?.Cancel();
        DebugConsole.Info("Onboarding : assistant passé, configuration par défaut conservée.");
        Persist(applyEngine: false);
        DialogResult = true;
        Close();
    }

    private void Finish()
    {
        Persist(applyEngine: true);
        DebugConsole.Success("Onboarding terminé — configuration appliquée.");
        DialogResult = true;
        Close();
    }

    // ------------------------------------------------------------- choices
    private void Connection_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton chosen || chosen.Tag is not string tag) return;
        _connection = tag;
        foreach (var other in new[] { ConnXtream, ConnM3u, ConnLocal })
            if (!ReferenceEquals(other, chosen))
                other.IsChecked = false;

        ObXtreamPanel.Visibility = tag == "xtream" ? Visibility.Visible : Visibility.Collapsed;
        ObM3uPanel.Visibility = tag == "m3u" ? Visibility.Visible : Visibility.Collapsed;
        ObTestRow.Visibility = tag == "local" ? Visibility.Collapsed : Visibility.Visible;
        ObTestStatus.Text = "";
    }

    private void ObBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir une playlist M3U",
            Filter = "Playlists M3U (*.m3u;*.m3u8)|*.m3u;*.m3u8|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true) ObM3uBox.Text = dlg.FileName;
    }

    private async void ObTestConnection_Click(object sender, RoutedEventArgs e)
    {
        ObTestBtn.IsEnabled = false;
        ObTestStatus.Text = "Connexion en cours…";
        try
        {
            var iptv = new IptvService();
            int channels;
            if (_connection == "m3u")
            {
                var path = ObM3uBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(path)) { ObTestStatus.Text = "Indique un fichier ou une URL M3U."; return; }
                channels = (await iptv.LoadM3uAsync(path)).Item2.Count;
            }
            else
            {
                var url = ObUrlBox.Text.Trim();
                var user = ObUserBox.Text.Trim();
                var pass = ObPassBox.Password;
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                { ObTestStatus.Text = "Remplis l'URL, l'utilisateur et le mot de passe."; return; }
                channels = (await iptv.ConnectAsync(url, user, pass)).Item2.Count;
            }
            ObTestStatus.Text = $"✓ Connexion réussie — {channels} chaînes détectées.";
            DebugConsole.Success($"Onboarding : test de connexion OK ({channels} chaînes).");
        }
        catch (Exception ex)
        {
            ObTestStatus.Text = "✗ " + ex.Message;
            DebugConsole.Warn("Onboarding : test de connexion échoué — " + ex.Message);
        }
        finally
        {
            ObTestBtn.IsEnabled = true;
        }
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not SolidColorBrush brush) return;
        _accentHex = brush.Color.ToString();
        ThemeManager.Apply(brush.Color);
        AccentValue.Text = "Accent actuel : " + _accentHex;
    }

    private List<string> SelectedInterests()
    {
        var chips = new[] { IntSport, IntAnime, IntCinema, IntTv, IntMusic, IntDocs };
        return chips.Where(c => c.IsChecked == true)
                    .Select(c => (string)c.Tag)
                    .ToList();
    }

    // ------------------------------------------------------------ hardware
    private async Task RunElySmartAsync()
    {
        if (_hardwareProbed) return;
        _hardwareProbed = true;
        _elySmartCts = new CancellationTokenSource();
        ElySmartOnboardingProgress.Visibility = Visibility.Visible;
        ElySmartCancelButton.Visibility = Visibility.Visible;
        NextBtn.IsEnabled = false;
        DebugConsole.Step("Onboarding : benchmark ELYSMART…");
        var workload = WorkloadFromInterests(SelectedInterests());
        var progress = new Progress<BenchmarkProgress>(p =>
        {
            ElySmartOnboardingProgress.Value = p.Percent;
            ElySmartStageText.Text = $"{p.Stage} — {p.Detail}";
        });
        try
        {
            var report = await new BenchmarkEngine().RunAsync(workload, progress, _elySmartCts.Token);
            if (report.Cancelled) { ElySmartStageText.Text = "Analyse annulée. Reviens sur cette étape pour la relancer."; _hardwareProbed = false; return; }
            _elySmartReport = report;
            _hardware = report.Hardware;
            var hw = report.Hardware;
            var gpuLines = hw.Gpus.Count == 0 ? "GPU ......... non détecté" : string.Join("\n", hw.Gpus.Select(g => $"GPU ......... {g.Name}" + (string.IsNullOrWhiteSpace(g.Driver) ? "" : $" (driver {g.Driver})")));
            HwSummary.Text = $"CPU ......... {hw.Cpu} ({hw.Cores} cœurs / {hw.Threads} threads)\nRAM ......... {hw.RamGb:0.#} Go\n{gpuLines}\nScore ....... {report.GlobalScore}/100 — {report.Rating} (provisoire, couverture {report.MeasurementCoveragePercent} %)";
            var c = report.Configuration;
            var reasons = report.Recommendations.Take(3).Select(r => $"• {r.Title}: {r.Reason} (confiance {r.Confidence}%)");
            RecoText.Text = "ELYSMART recommande :\n" + string.Join("\n", reasons);
            SelectCombo(EngineCombo, c.Renderer);
            VsrSwitch.IsChecked = c.RtxVsr;
            FrucSwitch.IsChecked = c.ElyFlow;
            UpdateEngineToggles();
            NextBtn.IsEnabled = true;
            DebugConsole.Info($"Onboarding ELYSMART : score={report.GlobalScore}, backend={c.Renderer}, VSR={c.RtxVsr}, ELYFLOW={c.ElyFlow}, upscale={c.Upscaling}");
        }
        catch (Exception ex)
        {
            _hardwareProbed = false;
            ElySmartStageText.Text = "ELYSMART n'a pas pu terminer : " + ex.Message;
            DebugConsole.Exception("Onboarding ELYSMART", ex);
        }
        finally
        {
            ElySmartOnboardingProgress.Visibility = Visibility.Collapsed;
            ElySmartCancelButton.Visibility = Visibility.Collapsed;
            _elySmartCts?.Dispose(); _elySmartCts = null;
        }
    }

    private static ElySmartWorkload WorkloadFromInterests(IReadOnlyCollection<string> interests)
    {
        if (interests.Count != 1) return ElySmartWorkload.Mixed;
        var interest = interests.First();
        return interest switch { "anime" => ElySmartWorkload.Anime, "cinema" => ElySmartWorkload.Films, "music" => ElySmartWorkload.Audio, "sport" or "tv" => ElySmartWorkload.Iptv, _ => ElySmartWorkload.Mixed };
    }

    private void ElySmartCancel_Click(object sender, RoutedEventArgs e) => _elySmartCts?.Cancel();

    private void UpdateEngineToggles()
    {
        var backend = SelectedEngine();
        switch (backend)
        {
            case "elycore":
                // FrucCapable, not FrucReady: no FRUC session exists while the
                // wizard runs, readiness only means "installed and usable".
                var frucCapable = ElyFlowService.Probe().FrucCapable;
                FrucSwitch.IsEnabled = frucCapable;
                if (!frucCapable) FrucSwitch.IsChecked = false;
                FrucSwitch.ToolTip = frucCapable ? null : "Runtime NVIDIA FRUC absent sur ce PC.";
                VsrSwitch.IsEnabled = _hardware?.Gpus.Any(g => g.Rtx) == true;
                VsrSwitch.ToolTip = "Passe native ELYCORE indépendante de l'interpolation FRUC.";
                break;
            case "rtx-sdk":
                VsrSwitch.IsChecked = true;
                VsrSwitch.IsEnabled = false;
                VsrSwitch.ToolTip = "RTX VSR fait partie intégrante de ce backend.";
                FrucSwitch.IsChecked = false;
                FrucSwitch.IsEnabled = false;
                FrucSwitch.ToolTip = "L'interpolation FRUC nécessite le renderer ELYCORE.";
                break;
            default:
                VsrSwitch.IsChecked = false;
                VsrSwitch.IsEnabled = false;
                VsrSwitch.ToolTip = "RTX VSR nécessite le backend RTX ou ELYCORE.";
                FrucSwitch.IsChecked = false;
                FrucSwitch.IsEnabled = false;
                FrucSwitch.ToolTip = "L'interpolation FRUC nécessite le renderer ELYCORE.";
                break;
        }
    }

    private string SelectedEngine() =>
        EngineCombo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "mpv-gpu";

    private static void SelectCombo(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    // ------------------------------------------------------------- install
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installBusy) return;
        _installBusy = true;
        InstallBtn.IsEnabled = false;
        InstallBtn.Content = "Installation en cours…";
        try
        {
            var backend = SelectedEngine();

            // 1) libmpv — required by every backend except the VLC fallback.
            if (backend != "vlc-bitmap")
            {
                if (string.IsNullOrWhiteSpace(MpvHwndBackend.LocateNative()))
                {
                    Log("• libmpv absent : téléchargement…");
                    try
                    {
                        var progress = new Progress<string>(msg => Log("  " + msg));
                        var dll = await new MpvNativeInstaller().InstallLatestAsync(progress);
                        Log("  ✓ libmpv installé : " + dll);
                    }
                    catch (Exception ex)
                    {
                        Log("  ✗ libmpv : " + ex.Message);
                        Log("  → ElyCast démarrera sur VLC ; installe 7-Zip puis relance l'installation de mpv depuis les réglages.");
                    }
                }
                else
                {
                    Log("• libmpv déjà présent ✓");
                }
            }

            // 2) GLSL shaders for the recommended upscaling method.
            var method = _elySmartReport?.Configuration.Upscaling ?? StateStore.Settings.UpscaleMethod;
            if (ShaderCatalog.MissingFor(method, "off").Count > 0)
            {
                Log($"• Téléchargement des shaders ({method})…");
                try
                {
                    await new ShaderInstaller().EnsureAsync(method, "off");
                    Log("  ✓ Shaders prêts.");
                }
                catch (Exception ex)
                {
                    Log("  ✗ Shaders : " + ex.Message + " (repli sur le scaler mpv)");
                }
            }
            else
            {
                Log($"• Shaders ({method}) déjà présents ✓");
            }

            // 3) RTX VSR functional test when the chosen pipeline relies on it.
            var wantsVsr = backend == "rtx-sdk" || (backend == "elycore" && VsrSwitch.IsChecked == true);
            if (wantsVsr)
            {
                Log("• Test RTX Video Super Resolution…");
                var result = await RtxVsrTester.RunAsync(
                    elyCore: backend == "elycore",
                    progress: new Progress<string>(msg => Log("  " + msg)));
                switch (result.Outcome)
                {
                    case VsrTestOutcome.Passed:
                        Log("  ✓ " + result.Message);
                        break;
                    case VsrTestOutcome.Skipped:
                        Log("  ~ " + result.Message);
                        break;
                    default:
                        Log("  ✗ " + result.Message);
                        if (backend == "elycore")
                        {
                            VsrSwitch.IsChecked = false;
                            Log("  → RTX VSR désactivé dans la configuration ELYFLOW (réactivable dans les réglages).");
                        }
                        else
                        {
                            Log("  → Le backend RTX retombera automatiquement sur le pipeline mpv GPU standard.");
                        }
                        break;
                }
            }

            // 4) ELYCORE native preflight sanity note.
            if (backend == "elycore")
            {
                var code = ElyFlowRendererInterop.Available
                    ? ElyFlowRendererInterop.Preflight(out var msg) == 0
                        ? 0 : -1
                    : -1;
                Log(code == 0
                    ? "• Préflight ELYCORE (D3D11 + WGL interop) ✓"
                    : "• ⚠ Préflight ELYCORE refusé — repli automatique sur mpv GPU au lancement.");
            }

            Log("");
            Log("Installation terminée. Clique sur « Terminer » pour lancer ElyCast.");
            _installDone = true;
            NextBtn.IsEnabled = true;
            InstallBtn.Content = "Relancer l'installation";
        }
        finally
        {
            _installBusy = false;
            InstallBtn.IsEnabled = true;
        }
    }

    private void Log(string line)
    {
        InstallLog.Text += (InstallLog.Text.Length == 0 ? "" : "\n") + line;
        InstallScroll.ScrollToEnd();
        if (!string.IsNullOrWhiteSpace(line)) DebugConsole.Info("Onboarding : " + line.Trim());
    }

    // -------------------------------------------------------------- persist
    private void Persist(bool applyEngine)
    {
        var s = StateStore.Settings;
        s.UserDisplayName = NameBox.Text.Trim();
        s.PreferredConnection = _connection;
        s.AccentColor = _accentHex;
        s.ContentInterests = SelectedInterests();
        s.AutoConnectProfile = SaveConnectionProfile();

        if (applyEngine)
        {
            s.VideoBackend = SelectedEngine();
            s.ElyFlowRtxVsrEnabled = VsrSwitch.IsChecked == true;
            s.ElyFlowEnabled = s.VideoBackend == "elycore" && FrucSwitch.IsChecked == true;
            if (_elySmartReport != null)
            {
                var c = _elySmartReport.Configuration;
                s.UpscaleMethod = c.Upscaling;
                s.ElyColorFilterId = c.ElyColor;
                s.ElySoundEnabled = c.ElySound;
                s.AudioVisualizerTargetFps = c.VisualizerFps;
                s.AudioParticleCount = c.Particles;
                s.AudioBackgroundMouseParallax = c.Parallax;
                s.AudioBackgroundBlur = c.Blur;
                s.ElySmartWorkload = _elySmartReport.Profile.ToString();
            }
        }

        s.OnboardingCompleted = true;
        StateStore.Save();
    }

    /// <summary>
    /// Saves the credentials entered in step 2 as a regular connection profile
    /// (DPAPI-protected, like the login screen would) and returns its name so
    /// MainWindow can connect automatically at startup. Empty when the user
    /// gave no usable connection details.
    /// </summary>
    private string SaveConnectionProfile()
    {
        Models.Profile? profile = null;
        if (_connection == "xtream")
        {
            var url = ObUrlBox.Text.Trim();
            var user = ObUserBox.Text.Trim();
            var pass = ObPassBox.Password;
            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
                profile = new Models.Profile
                {
                    Name = user + " @ " + url,
                    Kind = Models.ProfileKind.Xtream,
                    Url = url,
                    Username = user,
                    ProtectedPassword = ProfileStore.Protect(pass)
                };
        }
        else if (_connection == "m3u")
        {
            var path = ObM3uBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(path))
                profile = new Models.Profile
                {
                    Name = "Playlist M3U",
                    Kind = Models.ProfileKind.M3u,
                    M3uPath = path
                };
        }

        if (profile == null) return "";

        try
        {
            var profiles = ProfileStore.Load();
            profiles.RemoveAll(p => p.Name == profile.Name);
            profiles.Add(profile);
            ProfileStore.Save(profiles);
            DebugConsole.Success("Onboarding : profil de connexion enregistré (" + profile.Name + ").");
            return profile.Name;
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Onboarding : impossible d'enregistrer le profil — " + ex.Message);
            return "";
        }
    }
}
