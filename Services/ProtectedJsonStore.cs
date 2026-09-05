using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Elysium_Cast_IPTV.Services;

/// <summary>Atomic DPAPI persistence with one last-good backup and recovery.</summary>
internal sealed class ProtectedJsonStore<T>(string path, string header) where T : new()
{
    private bool _canSave = true;
    private bool _recovered;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    internal T Load(Func<T?, T>? normalize = null)
    {
        _canSave = true;
        _recovered = false;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var text = File.ReadAllText(candidate);
                if (text.StartsWith(header, StringComparison.Ordinal))
                    text = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                        Convert.FromBase64String(text[header.Length..]), null, DataProtectionScope.CurrentUser));
                var value = JsonSerializer.Deserialize<T>(text);
                var result = normalize != null ? normalize(value) : value ?? new T();
                _recovered = candidate != path;
                if (_recovered) DebugConsole.Warn("Recovered saved data from its backup.");
                return result;
            }
            catch (Exception ex) { DebugConsole.Error("Could not read saved data: " + ex.Message); }
        }
        // A failed read must never turn into an automatic destructive reset.
        _canSave = !File.Exists(path) && !File.Exists(path + ".bak");
        return new T();
    }

    internal void Save(T value)
    {
        if (!_canSave) throw new IOException("Saved data could not be read. The original files have been preserved.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, Options);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupTemp = temp + ".bak";
        try
        {
            File.WriteAllText(temp, Protect(json));
            if (File.Exists(path))
            {
                var previous = File.ReadAllText(path);
                if (!_recovered && !previous.StartsWith(header, StringComparison.Ordinal))
                {
                    // Migrating old plaintext must not leave a plaintext backup.
                    File.WriteAllText(backupTemp, Protect(previous));
                    File.Move(backupTemp, path + ".bak", overwrite: true);
                    File.Replace(temp, path, null);
                }
                else File.Replace(temp, path, _recovered ? null : path + ".bak");
            }
            else File.Move(temp, path);
            _recovered = false;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(backupTemp)) File.Delete(backupTemp);
        }
    }

    private string Protect(string json) => header + Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));
}
