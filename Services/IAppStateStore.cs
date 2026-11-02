using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Injectable view of the persisted application state. Production code uses
/// <see cref="StateStoreAdapter"/> (DPAPI-backed <see cref="StateStore"/>);
/// tests substitute an in-memory implementation.
/// </summary>
public interface IAppStateStore
{
    AppState Current { get; }
    Settings Settings { get; }
    ProfileState ForProfile(string key);
    void Save();

    /// <summary>True for clean diagnostic runs that must not touch the user's data.</summary>
    bool SuppressSaves { get; }

    string FolderPath { get; }
}

/// <summary>Injectable view of the DPAPI-protected connection profiles.</summary>
public interface IProfileStore
{
    List<Profile> Load();
    void Save(List<Profile> profiles);
    string Protect(string clear);
    string Unprotect(string protectedB64);
}

public sealed class ProfileStoreAdapter : IProfileStore
{
    public List<Profile> Load() => ProfileStore.Load();
    public void Save(List<Profile> profiles) => ProfileStore.Save(profiles);
    public string Protect(string clear) => ProfileStore.Protect(clear);
    public string Unprotect(string protectedB64) => ProfileStore.Unprotect(protectedB64);
}

public sealed class StateStoreAdapter : IAppStateStore
{
    public AppState Current => StateStore.Current;
    public Settings Settings => StateStore.Settings;
    public ProfileState ForProfile(string key) => StateStore.ForProfile(key);
    public void Save() => StateStore.Save();
    public bool SuppressSaves => StateStore.SuppressSaves;
    public string FolderPath => StateStore.FolderPath;
}
