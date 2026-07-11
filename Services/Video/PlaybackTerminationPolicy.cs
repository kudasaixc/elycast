namespace Elysium_Cast_IPTV.Services.Video;

public enum PlaybackEndReason
{
    NaturalEnd,
    UserStop,
    Replaced,
    Teardown
}

public enum PlaybackTerminationAction
{
    Ignore,
    CleanFiniteEnd,
    ManualStop,
    ShowError,
    ReconnectLive
}

/// <summary>Single decision table for EOF, user stop, replacement and failures.</summary>
public static class PlaybackTerminationPolicy
{
    public static PlaybackTerminationAction ForEnd(PlaybackEndReason reason, bool isLive) => reason switch
    {
        PlaybackEndReason.Replaced or PlaybackEndReason.Teardown => PlaybackTerminationAction.Ignore,
        PlaybackEndReason.UserStop => PlaybackTerminationAction.ManualStop,
        PlaybackEndReason.NaturalEnd when isLive => PlaybackTerminationAction.ReconnectLive,
        _ => PlaybackTerminationAction.CleanFiniteEnd
    };

    public static PlaybackTerminationAction ForFailure(bool isLive, bool autoReconnect) =>
        isLive && autoReconnect
            ? PlaybackTerminationAction.ReconnectLive
            : PlaybackTerminationAction.ShowError;
}
