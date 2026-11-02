using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Playback;

namespace Elysium_Cast_IPTV.ViewModels;

/// <summary>
/// Display state of the player shell (login card, catalogue header, video
/// overlay, OSD transport and statistics). Controllers write it; XAML binds to
/// it. Animations and HWND-airspace handling stay in the view layer.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly AudioQueue _queue;

    public PlayerViewModel(AudioQueue queue)
    {
        _queue = queue;
        _queue.Manual.CollectionChanged += OnQueueChanged;
        _queueText = QueueLabel();
    }

    // ============ LOGIN ============
    [ObservableProperty] private string _loginStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoginEnabled), nameof(ConnectLabel))]
    private bool _isConnecting;

    public bool IsLoginEnabled => !IsConnecting;
    public string ConnectLabel => LocalizationService.T(IsConnecting ? "Connecting..." : "Sign in");

    // ============ CATALOGUE HEADER ============
    [ObservableProperty] private string _sectionTitle = LocalizationService.T("Channels");
    [ObservableProperty] private string _itemCount = "0";
    [ObservableProperty] private string _resumeText = LocalizationService.T("Resume");
    [ObservableProperty] private bool _isResumeVisible;
    [ObservableProperty] private string _queueText;

    // ============ VIDEO OVERLAY ============
    [ObservableProperty] private bool _isOverlayVisible = true;
    [ObservableProperty] private bool _isOverlaySpinning;
    [ObservableProperty] private string _overlayMessage = LocalizationService.T("Select a channel to start playback");

    // ============ NOW PLAYING ============
    [ObservableProperty] private string _nowPlayingTitle = "";
    [ObservableProperty] private string _nowPlayingSubtitle = "";
    [ObservableProperty] private bool _showPlayGlyph = true;
    [ObservableProperty] private bool _isEpgVisible;
    [ObservableProperty] private double _epgPercent;

    // ============ SEEK ============
    [ObservableProperty] private bool _isSeekVisible;
    [ObservableProperty] private double _seekMaximum = 100;
    [ObservableProperty] private double _seekPosition;
    [ObservableProperty] private string _currentTimeText = "0:00";
    [ObservableProperty] private string _totalTimeText = "0:00";
    [ObservableProperty] private bool _isSkipBackVisible;
    [ObservableProperty] private bool _isSkipForwardVisible;

    // ============ VOLUME / STATS ============
    [ObservableProperty] private string _volumeText = "75%";
    [ObservableProperty] private string _statsText = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatsOverlayVisible))]
    private bool _isStatsEnabled;

    /// <summary>The stats box detaches visibly while Windows drags the window; hide it meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatsOverlayVisible))]
    private bool _isWindowMoving;

    public bool IsStatsOverlayVisible => IsStatsEnabled && !IsWindowMoving;

    /// <summary>Shows a status card over the video (spinning while loading).</summary>
    public void ShowOverlay(string englishText, bool spinning)
    {
        OverlayMessage = LocalizationService.T(englishText);
        IsOverlaySpinning = spinning;
        IsOverlayVisible = true;
    }

    public void HideOverlay()
    {
        IsOverlaySpinning = false;
        IsOverlayVisible = false;
    }

    /// <summary>Applies the skip-button policy for the current position.</summary>
    public void UpdateSkipButtons(long lengthMs, long positionMs)
    {
        (IsSkipBackVisible, IsSkipForwardVisible) = PlaybackText.SkipButtons(IsSeekVisible, lengthMs, positionMs);
    }

    /// <summary>Re-reads texts whose English source is fixed after a language switch.</summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(ConnectLabel));
        QueueText = QueueLabel();
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueText = QueueLabel();

    private string QueueLabel() => LocalizationService.Format("Queue: {0}", _queue.Manual.Count);
}
