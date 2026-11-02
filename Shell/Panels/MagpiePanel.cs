using System.IO;
using System.Windows.Controls;
using System.Windows.Interop;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Services;

namespace Elysium_Cast_IPTV.Shell.Panels;

/// <summary>
/// External Magpie window upscaler: install/locate, activation hotkey and the
/// "scaling changed" window message. Off by default; activation lives in the
/// video settings only.
/// </summary>
internal sealed class MagpiePanel
{
    private readonly MainWindow _view;
    private readonly IAppStateStore _store;
    private readonly MagpieUpscalerService _magpie;
    private bool _busy;
    private bool _active;
    private int _scalingChangedMessage;

    public MagpiePanel(MainWindow view, IAppStateStore store, MagpieUpscalerService magpie)
    {
        _view = view;
        _store = store;
        _magpie = magpie;
    }

    /// <summary>Asked to open the settings panel (Magpie missing).</summary>
    public event Action? SettingsRequested;

    public string? Locate() => _magpie.Locate();

    /// <summary>Settings load: shows the located executable and keeps the stored path in sync.</summary>
    public void Load()
    {
        var path = _magpie.Locate() ?? "";
        _view.MagpiePathBox.Text = path;
        if (!string.Equals(_store.Settings.MagpiePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _store.Settings.MagpiePath = path;
            _store.Save();
        }
    }

    public void HookWindowMessages()
    {
        var hwnd = MagpieUpscalerService.HwndFor(_view);
        if (hwnd == 0) return;
        _scalingChangedMessage = NativeMethods.RegisterWindowMessage("MagpieScalingChanged");
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _scalingChangedMessage)
        {
            _active = wParam != 0;
            RefreshActiveState();
        }
        else if (msg == NativeMethods.WM_GETMINMAXINFO)
        {
            handled = NativeMethods.TryConstrainMaximizedBounds(hwnd, lParam);
        }
        return 0;
    }

    public void OnEngineChanged()
    {
        if (_view.UpscalerEngineCombo.SelectedItem is not ComboBoxItem { Tag: string engine }) return;
        _store.Settings.UpscalerEngine = engine;
        _store.Save();
        UpdateStatus();
    }

    public void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Magpie.exe|Magpie.exe|Applications (*.exe)|*.exe",
            Title = LocalizationService.T("Choose Magpie.exe")
        };
        if (dlg.ShowDialog(_view) != true) return;

        _view.MagpiePathBox.Text = dlg.FileName;
        _store.Settings.MagpiePath = dlg.FileName;
        _store.Save();
        UpdateStatus();
    }

    public async Task InstallAsync()
    {
        if (_busy) return;
        _busy = true;
        _view.InstallMagpieBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(msg => _view.UpscalerStatusText.Text = LocalizationService.T(msg));
            var exe = await _magpie.InstallLatestAsync(progress);
            _view.MagpiePathBox.Text = exe;
            UpdateStatus();
            DebugConsole.Success("Magpie installed: " + exe);
        }
        catch (Exception ex)
        {
            _view.UpscalerStatusText.Text = LocalizationService.T("Magpie installation failed");
            DebugConsole.Error("Magpie: " + ex.Message);
        }
        finally
        {
            _view.InstallMagpieBtn.IsEnabled = true;
            _busy = false;
        }
    }

    public async Task ToggleScalingAsync()
    {
        if (_busy) return;
        if (_store.Settings.UpscalerEngine == "off")
        {
            _store.Settings.UpscalerEngine = "magpie-fsr";
            ComboTags.Select(_view.UpscalerEngineCombo, "magpie-fsr");
            _store.Save();
        }

        var path = _view.MagpiePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = _magpie.Locate() ?? "";
            _view.MagpiePathBox.Text = path;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _view.UpscalerStatusText.Text = LocalizationService.T("Install Magpie first");
            SettingsRequested?.Invoke();
            return;
        }

        _busy = true;
        try
        {
            _store.Settings.MagpiePath = path;
            _store.Save();
            _magpie.ConfigureForEngine(_store.Settings.UpscalerEngine);
            if (!await _magpie.EnsureRunningAsync(path))
                throw new InvalidOperationException(LocalizationService.T("Magpie could not start."));

            // Let Magpie initialize its hidden window/tray state before sending the hotkey.
            await Task.Delay(250);
            var hwnd = MagpieUpscalerService.HwndFor(_view);
            await _magpie.ActivateScalingAsync(hwnd, _store.Settings.MagpieHotkey);
            _view.UpscalerStatusText.Text = _active
                ? LocalizationService.Format("Stop requested ({0})", EngineLabel())
                : LocalizationService.Format("Activation requested ({0})", EngineLabel());
            await Task.Delay(900);
            RefreshActiveState();
            DebugConsole.Success("Toggle upscaling Magpie / " + EngineLabel());
        }
        catch (Exception ex)
        {
            _view.UpscalerStatusText.Text = LocalizationService.T("Magpie activation failed");
            DebugConsole.Error("Magpie: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    public void UpdateStatus()
    {
        if (_view.UpscalerStatusText == null) return;
        var located = _magpie.Locate();
        if (!string.IsNullOrWhiteSpace(located) && string.IsNullOrWhiteSpace(_view.MagpiePathBox.Text))
            _view.MagpiePathBox.Text = located;

        RefreshActiveState(updateText: false);
        _view.UpscalerStatusText.Text = _store.Settings.UpscalerEngine == "off"
            ? LocalizationService.T("External upscaling disabled")
            : _active
                ? LocalizationService.Format("{0} active", EngineLabel())
                : located != null
                    ? LocalizationService.Format("{0} ready", EngineLabel())
                : LocalizationService.T("Magpie is required for GPU upscaling");
    }

    private void RefreshActiveState(bool updateText = true)
    {
        _active = _magpie.FindScalingWindow() != 0;
        if (_view.UpscalerBtn?.Content is TextBlock text)
            text.Text = _active ? "ON" : "FSR";
        if (updateText && _view.UpscalerStatusText != null && _store.Settings.UpscalerEngine != "off")
            _view.UpscalerStatusText.Text = _active
                ? LocalizationService.Format("{0} active", EngineLabel())
                : LocalizationService.Format("{0} ready", EngineLabel());
    }

    private string EngineLabel() => _store.Settings.UpscalerEngine switch
    {
        "magpie-fsrcnnx" => "Magpie FSRCNNX",
        "magpie-anime4k" => "Magpie Anime4K",
        "magpie-fsr" => "Magpie FSR",
        _ => LocalizationService.T("Disabled")
    };
}
