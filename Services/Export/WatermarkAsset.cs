using System.IO;

namespace Elysium_Cast_IPTV.Services.Export;

/// <summary>
/// The mandatory export watermark lives inside ElyCast.dll as an embedded
/// resource, so an installation can never lose it or ship without it.
/// libavfilter's <c>movie</c> source only reads a file, so before each
/// export the logo is written to <see cref="FilePath"/> under the
/// per-user cache (skipped when an identical copy is already there).
/// </summary>
public static class WatermarkAsset
{
    public const string ResourceName = "ElyCast.Export.watermark.png";

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElyCast", "cache", "watermark.png");

    public static byte[] ReadEmbedded()
    {
        using var stream = typeof(WatermarkAsset).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The watermark resource is missing from ElyCast.dll.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Writes the embedded logo to <see cref="FilePath"/> atomically; false with the reason when it cannot.</summary>
    public static bool TryMaterialize(out string error)
    {
        error = "";
        try
        {
            var logo = ReadEmbedded();
            if (File.Exists(FilePath) && File.ReadAllBytes(FilePath).AsSpan().SequenceEqual(logo)) return true;

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temp, logo);
                File.Move(temp, FilePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { /* best effort: a stray temp file is harmless */ }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }
}
