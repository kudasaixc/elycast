using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Persists user profiles under %AppData%\ElyCast\profiles.json.
/// Passwords are encrypted with Windows DPAPI (per-user) before being written.
/// </summary>
public static class ProfileStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElyCast");
    private static readonly string FilePath = Path.Combine(Dir, "profiles.json");
    private const string ProtectedHeader = "ElyCastProfiles:v1:";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<Profile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            if (json.StartsWith(ProtectedHeader, StringComparison.Ordinal))
            {
                var cipher = Convert.FromBase64String(json[ProtectedHeader.Length..]);
                json = Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
            }
            return JsonSerializer.Deserialize<List<Profile>>(json) ?? new();
        }
        catch (Exception ex)
        {
            DebugConsole.Error("Lecture des profils impossible : " + ex.Message);
            return new();
        }
    }

    public static void Save(List<Profile> profiles)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(profiles, Options);
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            WriteAtomically(FilePath, ProtectedHeader + Convert.ToBase64String(cipher));
            DebugConsole.Debug($"Profils enregistrés ({profiles.Count}) -> {FilePath}");
        }
        catch (Exception ex)
        {
            DebugConsole.Error("Sauvegarde des profils impossible : " + ex.Message);
        }
    }

    public static string Protect(string clear)
    {
        if (string.IsNullOrEmpty(clear)) return "";
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(clear), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch { return ""; }
    }

    public static string Unprotect(string protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedB64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }

    public static string FolderPath => Dir;

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
