using System.Collections.ObjectModel;
using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services.Playback;

/// <summary>
/// Local-audio continuation policy. Two layers: the visible manual queue
/// (tracks the user explicitly enqueued, always played first in insertion
/// order) and an invisible automatic continuation through the list the
/// current track was launched from. After the manual layer drains, automatic
/// continuation resumes from its preserved cursor.
/// </summary>
public sealed class AudioQueue
{
    private readonly Random _random;

    public AudioQueue(Random? random = null) => _random = random ?? new Random();

    /// <summary>Tracks the user enqueued, in play order.</summary>
    public ObservableCollection<PlayItem> Manual { get; } = new();

    /// <summary>The list automatic continuation walks through; null means the whole library.</summary>
    public List<PlayItem>? Context { get; private set; }

    public int AutoIndex { get; private set; } = -1;
    public bool PlayingManual { get; private set; }
    public bool Shuffle { get; set; }
    public bool Repeat { get; set; }

    /// <summary>Starts a group (album, playlist...) from its first track, optionally shuffled.</summary>
    public PlayItem? StartGroup(IReadOnlyList<PlayItem> tracks, bool shuffle)
    {
        if (tracks.Count == 0) return null;
        var order = shuffle ? tracks.OrderBy(_ => _random.Next()).ToList() : tracks.ToList();
        Context = order;
        AutoIndex = 0;
        PlayingManual = false;
        return order[0];
    }

    /// <summary>Continuation will follow <paramref name="context"/> from <paramref name="track"/>.</summary>
    public void StartFrom(List<PlayItem>? context, PlayItem track)
    {
        Context = context;
        AutoIndex = context?.FindIndex(item => item.SameAs(track)) ?? -1;
        PlayingManual = false;
    }

    public void Enqueue(PlayItem item) => Manual.Add(item);

    /// <summary>Moves <paramref name="item"/> to the head of the manual queue.</summary>
    public void EnqueueNext(PlayItem item)
    {
        var duplicate = Manual.FirstOrDefault(queued => queued.SameAs(item));
        if (duplicate != null) Manual.Remove(duplicate);
        Manual.Insert(0, item);
    }

    /// <summary>Appends the tracks not already queued; returns how many were added.</summary>
    public int EnqueueRange(IEnumerable<PlayItem> tracks)
    {
        var before = Manual.Count;
        foreach (var track in tracks)
            if (!Manual.Any(q => q.SameAs(track))) Manual.Add(track);
        return Manual.Count - before;
    }

    public void ClearManual() => Manual.Clear();

    /// <summary>Forgets everything (sign-out).</summary>
    public void Reset()
    {
        Manual.Clear();
        Context = null;
        AutoIndex = -1;
        PlayingManual = false;
    }

    /// <summary>Pops manual entries until one is playable (missing files are skipped).</summary>
    public PlayItem? DequeueManual(Func<PlayItem, bool> isPlayable)
    {
        while (Manual.Count > 0)
        {
            var next = Manual[0];
            Manual.RemoveAt(0);
            if (!isPlayable(next)) continue;
            PlayingManual = true;
            return next;
        }
        return null;
    }

    /// <summary>Next/previous track of the automatic continuation (or a random one when shuffling).</summary>
    public PlayItem? Adjacent(int direction, PlayItem? current, IReadOnlyList<PlayItem> library)
    {
        IReadOnlyList<PlayItem> source = Context is { Count: > 0 } context ? context : library;
        if (source.Count == 0) return null;
        if (!PlayingManual && current != null)
        {
            var currentIndex = IndexOf(source, current);
            if (currentIndex >= 0) AutoIndex = currentIndex;
        }
        AutoIndex = Shuffle
            ? _random.Next(source.Count)
            : (AutoIndex + direction + source.Count) % source.Count;
        PlayingManual = false;
        return source[AutoIndex];
    }

    /// <summary>What plays when a local track reaches its natural end.</summary>
    public PlayItem? NextAfterEnd(PlayItem? current, IReadOnlyList<PlayItem> library, Func<PlayItem, bool> isPlayable) =>
        DequeueManual(isPlayable)
        ?? (Repeat && current != null ? current : Adjacent(1, current, library));

    /// <summary>What plays for the previous/next transport buttons.</summary>
    public PlayItem? Zap(int direction, PlayItem? current, IReadOnlyList<PlayItem> library, Func<PlayItem, bool> isPlayable) =>
        (direction > 0 ? DequeueManual(isPlayable) : null) ?? Adjacent(direction, current, library);

    /// <summary>Drops removed tracks from both layers while keeping the continuation cursor aligned.</summary>
    public void RemoveWhere(Func<PlayItem, bool> matches)
    {
        for (var i = Manual.Count - 1; i >= 0; i--)
            if (matches(Manual[i])) Manual.RemoveAt(i);
        if (Context == null) return;
        AutoIndex -= Context.Take(AutoIndex + 1).Count(matches);
        Context.RemoveAll(item => matches(item));
    }

    private static int IndexOf(IReadOnlyList<PlayItem> source, PlayItem current)
    {
        for (var i = 0; i < source.Count; i++)
            if (source[i].SameAs(current)) return i;
        return -1;
    }
}
