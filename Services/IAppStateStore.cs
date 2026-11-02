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
}

public sealed class StateStoreAdapter : IAppStateStore
{
    public AppState Current => StateStore.Current;
    public Settings Settings => StateStore.Settings;
    public ProfileState ForProfile(string key) => StateStore.ForProfile(key);
    public void Save() => StateStore.Save();
}
