using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;

namespace Elysium_Cast_IPTV.Services.Video;

public sealed partial class MpvNativeInstaller
{
    private const string SourceForgeLibMpvUrl = "https://sourceforge.net/projects/mpv-player-windows/files/libmpv/";
    private static readonly HttpClient Http = new();

    public string? Locate() => MpvBackend.LocateNative();

    public async Task<string> InstallLatestAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var sevenZip = FindSevenZip() ?? throw new InvalidOperationException(LocalizationService.T("7-Zip is required to extract libmpv (.7z)."));
        var flavor = Avx2.IsSupported ? "x86_64-v3" : "x86_64";

        progress?.Report(LocalizationService.T("Searching for libmpv..."));
        using var listingRequest = new HttpRequestMessage(HttpMethod.Get, SourceForgeLibMpvUrl);
        listingRequest.Headers.UserAgent.ParseAdd("ElyCast/2.1");
        var listing = await (await Http.SendAsync(listingRequest, cancellationToken)).Content.ReadAsStringAsync(cancellationToken);

        var packageName = FindPackageName(listing, flavor)
            ?? throw new InvalidOperationException("No Windows x64 libmpv package was found.");

        var toolsRoot = Path.Combine(StateStore.FolderPath, "tools", "mpv");
        var downloads = Path.Combine(toolsRoot, "downloads");
        Directory.CreateDirectory(downloads);

        var archivePath = Path.Combine(downloads, packageName);
        if (!File.Exists(archivePath))
        {
            progress?.Report("Downloading " + packageName + "...");
            var downloadUrl = SourceForgeLibMpvUrl + Uri.EscapeDataString(packageName) + "/download";
            using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            downloadRequest.Headers.UserAgent.ParseAdd("ElyCast/2.1");
            using var response = await Http.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(archivePath);
            await input.CopyToAsync(output, cancellationToken);
        }

        var targetDir = Path.Combine(toolsRoot, Path.GetFileNameWithoutExtension(packageName));
        Directory.CreateDirectory(targetDir);

        progress?.Report(LocalizationService.T("Extracting libmpv..."));
        await ExtractWithSevenZipAsync(sevenZip, archivePath, targetDir);

        var dll = Directory.EnumerateFiles(targetDir, "libmpv-2.dll", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("libmpv-2.dll was not found after extraction.");

        progress?.Report("mpv ready.");
        return dll;
    }

    private static string? FindPackageName(string listing, string flavor)
    {
        var pattern = $@"mpv-dev-{Regex.Escape(flavor)}-\d{{8}}-git-[A-Za-z0-9]+\.7z";
        return Regex.Matches(listing, pattern)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? FindSevenZip()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        };

        foreach (var path in candidates)
            if (File.Exists(path)) return path;

        var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir.Trim(), "7z.exe");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static async Task ExtractWithSevenZipAsync(string sevenZip, string archivePath, string targetDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = sevenZip,
            Arguments = $"x \"{archivePath}\" -o\"{targetDir}\" -y",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de lancer 7-Zip.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException("7-Zip extraction failed: " + (string.IsNullOrWhiteSpace(error) ? output : error));
    }
}
