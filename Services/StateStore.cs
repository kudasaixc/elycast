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
    public static bool SuppressSaves { get; set; }

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
        if (SuppressSaves) return;
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
        state.LocalAudioLibrary ??= new List<PlayItem>();
        state.LocalVideoLibrary ??= new List<PlayItem>();
        state.LocalPlaylists ??= new List<LocalPlaylist>();

        // v1.1 migration: the former mixed local library is split once without
        // losing paths, favourites or resume entries.
        if (state.LocalLibrary.Count > 0)
        {
            foreach (var item in state.LocalLibrary.Where(item => item.Kind == PlayItemKind.Local))
            {
                var target = LocalLibraryService.IsAudio(item.DirectUrl ?? item.Id)
                    ? state.LocalAudioLibrary
                    : state.LocalVideoLibrary;
                if (!target.Any(existing => string.Equals(existing.DirectUrl ?? existing.Id,
                        item.DirectUrl ?? item.Id, StringComparison.OrdinalIgnoreCase)))
                    target.Add(item);
            }
            state.LocalLibrary.Clear();
        }
        state.LocalPlaylists.RemoveAll(playlist => playlist == null);
        foreach (var playlist in state.LocalPlaylists)
        {
            playlist.Id = string.IsNullOrWhiteSpace(playlist.Id) ? Guid.NewGuid().ToString("N") : playlist.Id;
            playlist.Name = string.IsNullOrWhiteSpace(playlist.Name) ? "Playlist" : playlist.Name.Trim();
            playlist.TrackPaths ??= new List<string>();
            playlist.TrackPaths = playlist.TrackPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var s = state.Settings;
        s.DefaultVolume = Math.Clamp(s.DefaultVolume, 0, 100);
        s.BootSeconds = double.IsFinite(s.BootSeconds) ? Math.Clamp(s.BootSeconds, 0, 30) : 5.5;
        s.AudioBackgroundMode = s.AudioBackgroundMode is "solid" or "cover" or "image" ? s.AudioBackgroundMode : "solid";
        s.AudioBackgroundImage = s.AudioBackgroundImage is "mountains" or "sunset" or "night-sky" or "paris" or "new-york" ? s.AudioBackgroundImage : "sunset";
        s.AudioBackgroundBlur = double.IsFinite(s.AudioBackgroundBlur) ? Math.Clamp(s.AudioBackgroundBlur, 0, 48) : 45.6;
        s.AudioBackgroundDim = double.IsFinite(s.AudioBackgroundDim) ? Math.Clamp(s.AudioBackgroundDim, 0.15, 0.85) : 0.85;
        s.AudioBackgroundParallaxIntensity = double.IsFinite(s.AudioBackgroundParallaxIntensity)
            ? Math.Clamp(s.AudioBackgroundParallaxIntensity, 0, 2) : 1.0;
        // 0 = illimité (aucun plafond de cadence côté renderer natif).
        s.AudioVisualizerTargetFps = s.AudioVisualizerTargetFps <= 0
            ? 0 : Math.Clamp(s.AudioVisualizerTargetFps, 30, 480);
        // One-time bump of the former 96 default to the new 192 default; runs once
        // so a user who deliberately picks 96 later is left alone.
        if (!s.AudioParticleCountMigratedV2)
        {
            if (s.AudioParticleCount == 96) s.AudioParticleCount = 192;
            s.AudioParticleCountMigratedV2 = true;
        }
        s.AudioParticleCount = Math.Clamp(s.AudioParticleCount, 24, 384);
        s.AudioParticleDistance = double.IsFinite(s.AudioParticleDistance) ? Math.Clamp(s.AudioParticleDistance, 0.55, 1.65) : 1.0;
        s.AudioVisualizerRenderer = s.AudioVisualizerRenderer is "classic" or "audiocore"
            ? s.AudioVisualizerRenderer : "classic";
        s.AudioBrowseMode = s.AudioBrowseMode is "albums" or "artists" or "genres" or "playlists" or "tracks"
            ? s.AudioBrowseMode : "albums";
        s.ElySmartWorkload = s.ElySmartWorkload is "Iptv" or "Films" or "Series" or "Anime" or "Audio" or "Mixed" ? s.ElySmartWorkload : "Mixed";
        s.ElySmartIgnoredHealthIssues ??= new List<string>();
        s.ElySmartIgnoredHealthIssues.RemoveAll(value => !Enum.TryParse<ElySmart.HealthIssueKind>(value, out _));
        s.ElyColorCustomFilters ??= new List<ElyColorFilter>();
        s.ElySoundCustomProfile ??= ElySoundProfile.DefaultCustom();
        NormalizeElySoundProfile(s.ElySoundCustomProfile);
        s.OsdUpscaleModes ??= new List<string>();
        s.ContentInterests ??= new List<string>();
        if (string.IsNullOrWhiteSpace(s.PreferredConnection)) s.PreferredConnection = "xtream";

        // "fsr" was removed from the OSD quick-selector defaults (redundant with
        // FSRCNNX/Anime4K); drop it from lists persisted before the change. The
        // full method stays available in the settings panel.
        s.OsdUpscaleModes.RemoveAll(m => string.Equals(m, "fsr", StringComparison.OrdinalIgnoreCase));

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

    private static void NormalizeElySoundProfile(ElySoundProfile profile)
    {
        profile.Preamp = Math.Clamp(profile.Preamp, -12, 6);
        profile.Bass = Math.Clamp(profile.Bass, -12, 12);
        profile.LowMid = Math.Clamp(profile.LowMid, -12, 12);
        profile.Mid = Math.Clamp(profile.Mid, -12, 12);
        profile.Presence = Math.Clamp(profile.Presence, -12, 12);
        profile.Treble = Math.Clamp(profile.Treble, -12, 12);
        profile.Clarity = Math.Clamp(profile.Clarity, -12, 12);
        profile.Width = Math.Clamp(profile.Width, 0, 60);
        profile.Compressor = Math.Clamp(profile.Compressor, 0, 60);
        if (profile.Version < 2)
        {
            profile.LimiterCeilingDb = profile.Limiter is >= 70 and <= 100
                ? 20.0 * Math.Log10(profile.Limiter / 100.0)
                : -2.3;
            profile.Version = 2;
        }
        profile.LimiterCeilingDb = double.IsFinite(profile.LimiterCeilingDb)
            ? Math.Clamp(profile.LimiterCeilingDb, -3.0, -0.3)
            : -2.3;
        profile.Limiter = 0;
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
