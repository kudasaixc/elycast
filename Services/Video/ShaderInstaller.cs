using System.IO;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// Prepares the shader files a method needs. Every pristine shader ships with
/// ElyCast (see <see cref="ShaderCatalog.Bundled"/>); this only generates the
/// sharpen-tuned CAS / NVSharpen companions from the bundled originals, once
/// per sharpen level, into the user data folder. Nothing is downloaded.
/// </summary>
public sealed class ShaderInstaller
{
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
                if (!ShaderCatalog.IsTuned(file, out var baseFile))
                    throw new InvalidOperationException(LocalizationService.Format("Shader {0} is missing from the ElyCast installation.", file));

                var basePath = ShaderCatalog.PathFor(baseFile);
                if (!File.Exists(basePath))
                    throw new InvalidOperationException(LocalizationService.Format("Shader {0} is missing from the ElyCast installation.", baseFile));
                var pristine = await File.ReadAllTextAsync(basePath, cancellationToken);
                var tunedPath = ShaderCatalog.PathFor(file);
                Directory.CreateDirectory(Path.GetDirectoryName(tunedPath)!);
                await File.WriteAllTextAsync(tunedPath, ShaderCatalog.GenerateTuned(baseFile, pristine, sharpen), cancellationToken);
                DebugConsole.Success($"Shader generated: {file}");
            }
        }
        finally { Gate.Release(); }
    }
}
