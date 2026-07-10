using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Persists global settings + per-profile favourites/resume under
/// %AppData%\ElyCast\state.json. Loaded once at startup and kept in memory.
/// </summary>
public static class StateStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElyCast");
    private static readonly string FilePath = Path.Combine(Dir, "state.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private const string ProtectedHeader = "ElyCastState:v1:";

    public static AppState Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var content = File.ReadAllText(FilePath);
                if (content.StartsWith(ProtectedHeader, StringComparison.Ordinal))
                {
                    var cipher = Convert.FromBase64String(content[ProtectedHeader.Length..]);
                    content = Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
                }
                Current = Normalize(JsonSerializer.Deserialize<AppState>(content));
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Error("Lecture de l'état impossible : " + ex.Message);
            Current = new();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(Normalize(Current), Options);
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            WriteAtomically(FilePath, ProtectedHeader + Convert.ToBase64String(cipher));
        }
        catch (Exception ex)
        {
            DebugConsole.Error("Sauvegarde de l'état impossible : " + ex.Message);
        }
    }

    public static Settings Settings => Current.Settings ??= new();

    public static ProfileState ForProfile(string key)
    {
        Current.Profiles ??= new(StringComparer.Ordinal);
        if (!Current.Profiles.TryGetValue(key, out var st))
        {
            st = new ProfileState();
            Current.Profiles[key] = st;
        }
        return st;
    }

    public static string FolderPath => Dir;

    private static AppState Normalize(AppState? state)
    {
        state ??= new AppState();
        state.Settings ??= new Settings();
        state.Profiles ??= new Dictionary<string, ProfileState>(StringComparer.Ordinal);
        state.LocalLibrary ??= new List<PlayItem>();

        var s = state.Settings;
        s.DefaultVolume = Math.Clamp(s.DefaultVolume, 0, 100);
        s.BootSeconds = double.IsFinite(s.BootSeconds) ? Math.Clamp(s.BootSeconds, 0, 30) : 5.5;
        s.ElyColorCustomFilters ??= new List<ElyColorFilter>();
        s.ElySoundCustomProfile ??= ElySoundProfile.DefaultCustom();
        s.OsdUpscaleModes ??= new List<string>();

        // The native renderer backend was renamed "elyflow" -> "elycore";
        // migrate values persisted before the rename.
        if (string.Equals(s.VideoBackend, "elyflow", StringComparison.OrdinalIgnoreCase))
            s.VideoBackend = "elycore";
        foreach (var filter in s.ElyColorCustomFilters)
        {
            if (string.Equals(filter?.VideoBackend, "elyflow", StringComparison.OrdinalIgnoreCase))
                filter!.VideoBackend = "elycore";
        }
        return state;
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
