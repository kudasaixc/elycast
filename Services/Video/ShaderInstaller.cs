using System.IO;
using System.Net.Http;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// Downloads the GLSL upscaling shaders on demand — the first time a method is
/// selected — and generates the sharpen-tuned CAS / NVSharpen companions (same
/// runtime-download pattern as MpvNativeInstaller). Without this, selecting
/// FSR / NVScaler / FSRCNNX on a fresh install silently did nothing because the
/// shader files simply were not there.
/// </summary>
public sealed class ShaderInstaller
{
    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Ensures every shader the method needs exists on disk.</summary>
    public async Task EnsureAsync(string method, string sharpen, CancellationToken cancellationToken = default)
    {
        if (ShaderCatalog.MissingFor(method, sharpen).Count == 0) return;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            // Re-evaluated under the lock: a concurrent call may have done it.
            foreach (var file in ShaderCatalog.MissingFor(method, sharpen))
            {
                if (ShaderCatalog.IsTuned(file, out var baseFile))
                {
                    var basePath = await DownloadIfMissingAsync(baseFile, cancellationToken);
                    var pristine = await File.ReadAllTextAsync(basePath, cancellationToken);
                    var tunedPath = ShaderCatalog.PathFor(file);
                    Directory.CreateDirectory(Path.GetDirectoryName(tunedPath)!);
                    await File.WriteAllTextAsync(tunedPath, ShaderCatalog.GenerateTuned(baseFile, pristine, sharpen), cancellationToken);
                    DebugConsole.Success($"Shader généré : {file}");
                }
                else
                {
                    await DownloadIfMissingAsync(file, cancellationToken);
                }
            }
        }
        finally { Gate.Release(); }
    }

    private static async Task<string> DownloadIfMissingAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = ShaderCatalog.PathFor(fileName);
        if (File.Exists(path)) return path;

        if (!ShaderCatalog.DownloadUrls.TryGetValue(fileName, out var url))
            throw new InvalidOperationException($"Pas d'URL connue pour le shader {fileName}.");

        DebugConsole.Step($"Shader manquant, téléchargement : {fileName}…");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ElyCast/2.1");
        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!text.Contains("//!HOOK", StringComparison.Ordinal))
            throw new InvalidOperationException($"Le contenu téléchargé pour {fileName} ne ressemble pas à un shader mpv.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, cancellationToken);
        DebugConsole.Success($"Shader installé : {fileName}");
        return path;
    }
}
