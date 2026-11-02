namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Set while controls are being filled from persisted settings, so their
/// change handlers do not write the half-loaded values back.
/// </summary>
internal sealed class UiLoadGate
{
    public bool IsLoading { get; set; }
}
