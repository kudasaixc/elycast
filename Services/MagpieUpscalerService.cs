using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Interop;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Integrates Magpie as an external real-time GPU window upscaler.
/// Magpie provides FSR/Anime4K/FSRCNNX-style scalers without forcing this WPF app
/// to own a full DirectX swapchain renderer.
/// </summary>
public sealed class MagpieUpscalerService
{
    private const string RepoApi = "https://api.github.com/repos/Blinue/Magpie/releases/latest";
    private static readonly string InstallRoot =
        Path.Combine(StateStore.FolderPath, "tools", "Magpie");

    public string? Locate()
    {
        var configured = StateStore.Settings.MagpiePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured) && IsCompatiblePath(configured))
            return configured;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "Magpie", "Magpie.exe"),
            Path.Combine(InstallRoot, "Magpie.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Magpie", "Magpie.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Magpie", "Magpie.exe")
        };
        foreach (var c in candidates)
            if (File.Exists(c) && IsCompatiblePath(c)) return c;

        if (Directory.Exists(InstallRoot))
        {
            var found = Directory.GetFiles(InstallRoot, "Magpie.exe", SearchOption.AllDirectories)
                .Where(IsCompatiblePath)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (found != null) return found;
        }

        return null;
    }

    public async Task<bool> EnsureRunningAsync(string exePath)
    {
        if (!File.Exists(exePath)) return false;

        var existing = Process.GetProcessesByName("Magpie")
            .FirstOrDefault(p =>
            {
                try { return string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase); }
                catch { return true; }
            });
        if (existing != null)
        {
            HideMagpieWindows(existing.Id);
            return true;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });
            if (process == null) return false;
            await Task.Delay(700);
            if (process.HasExited) return false;
            HideMagpieWindows(process.Id);
            return true;
        }
        catch { return false; }
    }

    public bool IsRunning(string? exePath = null) =>
        Process.GetProcessesByName("Magpie").Any(p =>
        {
            if (string.IsNullOrWhiteSpace(exePath)) return true;
            try { return string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase); }
            catch { return true; }
        });

    public void ConfigureForEngine(string engine)
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Magpie", "config", "v4", "config.json");
        if (!File.Exists(configPath)) return;

        var root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
        var scalingModes = root?["scalingModes"]?.AsArray();
        var profile = root?["profiles"]?.AsArray().FirstOrDefault()?.AsObject();
        if (root == null || scalingModes == null || profile == null) return;

        var modeName = engine switch
        {
            "magpie-fsrcnnx" => "FSRCNNX",
            "magpie-anime4k" => "Anime4K",
            "magpie-fsr" => "FSR",
            _ => ""
        };
        if (modeName.Length == 0) return;

        var index = -1;
        for (var i = 0; i < scalingModes.Count; i++)
        {
            var name = scalingModes[i]?["name"]?.GetValue<string>() ?? "";
            if (name.Equals(modeName, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index < 0) return;

        root["allowScalingMaximized"] = true;
        root["simulateExclusiveFullscreen"] = false;
        profile["scalingMode"] = index;
        profile["captureMethod"] = 0;
        profile["multiMonitorUsage"] = 0;
        profile["captureTitleBar"] = false;
        profile["autoHideCursorEnabled"] = false;
        var temp = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<string> InstallLatestAsync(IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(InstallRoot);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ElyCast-IPTV");

        progress?.Report("Recherche de la dernière release Magpie…");
        using var releaseStream = await http.GetStreamAsync(RepoApi);
        using var doc = await JsonDocument.ParseAsync(releaseStream);
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Where(a => a.TryGetProperty("name", out var n) &&
                        n.GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => new { Asset = a, Name = a.GetProperty("name").GetString() ?? "" })
            .Where(a => !a.Name.Contains("source", StringComparison.OrdinalIgnoreCase))
            .Select(a => new { a.Asset, a.Name, Score = ArchitectureScore(a.Name) })
            .Where(a => a.Score > 0)
            .OrderByDescending(a => a.Score)
            .FirstOrDefault()?.Asset ?? default;

        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Aucune archive Magpie compatible {RuntimeInformation.OSArchitecture} trouvée dans la dernière release.");

        var name = asset.GetProperty("name").GetString() ?? "Magpie.zip";
        var url = asset.GetProperty("browser_download_url").GetString()
            ?? throw new InvalidOperationException("URL de téléchargement Magpie introuvable.");
        var zipPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(name));
        var target = Path.Combine(InstallRoot, Path.GetFileNameWithoutExtension(name));

        progress?.Report("Téléchargement de Magpie…");
        await using (var remote = await http.GetStreamAsync(url))
        await using (var local = File.Create(zipPath))
            await remote.CopyToAsync(local);

        progress?.Report("Installation de Magpie…");
        Directory.CreateDirectory(target);
        ZipFile.ExtractToDirectory(zipPath, target, overwriteFiles: true);

        var exe = Directory.GetFiles(target, "Magpie.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("Magpie.exe introuvable après extraction.");

        StateStore.Settings.MagpiePath = exe;
        StateStore.Save();
        progress?.Report("Magpie installé.");
        return exe;
    }

    private static bool IsCompatiblePath(string path)
    {
        var p = path.ToLowerInvariant();
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => !p.Contains("arm64") && !p.Contains("aarch64"),
            Architecture.Arm64 => !p.Contains("x64") && !p.Contains("x86"),
            Architecture.X86 => p.Contains("x86") && !p.Contains("x64") && !p.Contains("arm"),
            _ => true
        };
    }

    private static int ArchitectureScore(string assetName)
    {
        var n = assetName.ToLowerInvariant();
        if (n.Contains("arm64") || n.Contains("aarch64"))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? 100 : -100;
        if (n.Contains("x64") || n.Contains("x86_64") || n.Contains("amd64"))
            return RuntimeInformation.OSArchitecture == Architecture.X64 ? 100 : -100;
        if (n.Contains("x86") || n.Contains("win32"))
            return RuntimeInformation.OSArchitecture == Architecture.X86 ? 90 :
                   RuntimeInformation.OSArchitecture == Architecture.X64 ? 40 : -100;

        // Generic Windows zip: acceptable fallback, below explicit architecture packages.
        return n.Contains("win") || n.Contains("magpie") ? 10 : -100;
    }

    public async Task ActivateScalingAsync(nint ownerHwnd, string hotkey)
    {
        if (ownerHwnd == 0) return;

        BringToFront(ownerHwnd);
        await Task.Delay(260);
        SendHotkey(hotkey);
    }

    public nint FindScalingWindow() =>
        FindWindow("Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22", null);

    private static void SendHotkey(string hotkey)
    {
        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var keys = parts.Select(ParseKey).Where(k => k != 0).ToArray();
        if (keys.Length == 0) return;

        foreach (var key in keys) Keybd(key, 0);
        for (var i = keys.Length - 1; i >= 0; i--) Keybd(keys[i], KEYEVENTF_KEYUP);
    }

    private static ushort ParseKey(string key) => key.ToUpperInvariant() switch
    {
        "ALT" => 0x12,
        "SHIFT" => 0x10,
        "CTRL" or "CONTROL" => 0x11,
        "F11" => 0x7A,
        "Q" => 0x51,
        _ when key.Length == 1 && key[0] is >= 'A' and <= 'Z' => key[0],
        _ => 0
    };

    private static void Keybd(ushort key, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = key, dwFlags = flags }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static nint HwndFor(System.Windows.Window window) =>
        new WindowInteropHelper(window).Handle;

    private static void BringToFront(nint hwnd)
    {
        ShowWindow(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        SetForegroundWindow(hwnd);
    }

    private static void HideMagpieWindows(int processId)
    {
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == processId && IsWindowVisible(hwnd))
                ShowWindow(hwnd, SW_HIDE);
            return true;
        }, 0);
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;
    private static readonly nint HWND_TOPMOST = new(-1);
    private static readonly nint HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }
}
