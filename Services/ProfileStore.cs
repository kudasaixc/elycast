using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    private static readonly ProtectedJsonStore<List<Profile>> Store = new(FilePath, ProtectedHeader);

    public static List<Profile> Load()
    {
        return Store.Load(profiles => (profiles ?? []).Where(profile => profile != null).ToList());
    }

    public static void Save(List<Profile> profiles)
    {
        try
        {
            Store.Save(profiles);
            DebugConsole.Debug($"Profiles saved ({profiles.Count}) -> {FilePath}");
        }
        catch (Exception ex)
        {
            DebugConsole.Error("Could not save profiles: " + ex.Message);
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

}
