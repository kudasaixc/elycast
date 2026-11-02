using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Playback;
using Xunit;
using static ElyCast.UnitTests.TestData;

namespace ElyCast.UnitTests;

public class AudioQueueTests
{
    private static bool Always(PlayItem _) => true;

    [Fact]
    public void Manual_queue_plays_before_the_automatic_continuation()
    {
        var library = Tracks("a", "b", "c");
        var queue = new AudioQueue();
        queue.StartFrom(library, library[0]);
        var extra = Track("extra");
        queue.Enqueue(extra);

        Assert.Same(extra, queue.NextAfterEnd(library[0], library, Always));
        Assert.True(queue.PlayingManual);
        Assert.Empty(queue.Manual);
    }

    [Fact]
    public void Continuation_resumes_from_its_cursor_after_the_manual_queue_drains()
    {
        var library = Tracks("a", "b", "c");
        var queue = new AudioQueue();
        queue.StartFrom(library, library[0]);
        var extra = Track("extra");
        queue.Enqueue(extra);

        var first = queue.NextAfterEnd(library[0], library, Always);
        var second = queue.NextAfterEnd(first, library, Always);

        Assert.Same(extra, first);
        Assert.Same(library[1], second);
        Assert.False(queue.PlayingManual);
    }

    [Fact]
    public void Missing_files_are_skipped_and_dropped_from_the_manual_queue()
    {
        var missing = Track("missing");
        var present = Track("present");
        var queue = new AudioQueue();
        queue.Enqueue(missing);
        queue.Enqueue(present);

        var next = queue.DequeueManual(item => !item.SameAs(missing));

        Assert.Same(present, next);
        Assert.Empty(queue.Manual);
    }

    [Fact]
    public void Adjacent_wraps_in_both_directions()
    {
        var library = Tracks("a", "b", "c");
        var queue = new AudioQueue();

        Assert.Same(library[0], queue.Adjacent(1, library[2], library));
        Assert.Same(library[2], queue.Adjacent(-1, library[0], library));
    }

    [Fact]
    public void Adjacent_returns_null_for_an_empty_library()
    {
        Assert.Null(new AudioQueue().Adjacent(1, null, []));
    }

    [Fact]
    public void Repeat_replays_the_current_track_when_nothing_is_queued()
    {
        var library = Tracks("a", "b");
        var queue = new AudioQueue { Repeat = true };

        Assert.Same(library[0], queue.NextAfterEnd(library[0], library, Always));
    }

    [Fact]
    public void Previous_never_consumes_the_manual_queue()
    {
        var library = Tracks("a", "b", "c");
        var queue = new AudioQueue();
        queue.Enqueue(Track("extra"));

        Assert.Same(library[0], queue.Zap(-1, library[1], library, Always));
        Assert.Single(queue.Manual);
    }

    [Fact]
    public void Next_consumes_the_manual_queue_first()
    {
        var library = Tracks("a", "b");
        var queue = new AudioQueue();
        var extra = Track("extra");
        queue.Enqueue(extra);

        Assert.Same(extra, queue.Zap(1, library[0], library, Always));
    }

    [Fact]
    public void Play_next_moves_an_already_queued_track_to_the_head()
    {
        var queue = new AudioQueue();
        queue.Enqueue(Track("a"));
        queue.Enqueue(Track("b"));

        queue.EnqueueNext(Track("b"));

        Assert.Equal(2, queue.Manual.Count);
        Assert.True(queue.Manual[0].SameAs(Track("b")));
    }

    [Fact]
    public void Enqueue_range_skips_duplicates_and_reports_the_added_count()
    {
        var queue = new AudioQueue();
        queue.Enqueue(Track("a"));

        var added = queue.EnqueueRange(Tracks("a", "b", "c"));

        Assert.Equal(2, added);
        Assert.Equal(3, queue.Manual.Count);
    }

    [Fact]
    public void Removal_keeps_the_continuation_cursor_on_the_same_track()
    {
        var library = Tracks("a", "b", "c", "d");
        var queue = new AudioQueue();
        queue.StartFrom(library.ToList(), library[2]);
        var removed = library[0];

        queue.RemoveWhere(item => item.SameAs(removed));

        Assert.Equal(1, queue.AutoIndex);
        Assert.Same(library[2], queue.Context![queue.AutoIndex]);
    }

    [Fact]
    public void Start_group_returns_the_first_track_and_owns_the_context()
    {
        var tracks = Tracks("a", "b");
        var queue = new AudioQueue();

        Assert.Same(tracks[0], queue.StartGroup(tracks, shuffle: false));
        Assert.Equal(0, queue.AutoIndex);
        Assert.Null(queue.StartGroup([], shuffle: false));
    }

    [Fact]
    public void Shuffle_is_deterministic_with_a_seeded_random()
    {
        var library = Tracks("a", "b", "c", "d", "e");
        var expected = new Random(7);
        var queue = new AudioQueue(new Random(7)) { Shuffle = true };

        Assert.Same(library[expected.Next(library.Count)], queue.Adjacent(1, null, library));
    }

    [Fact]
    public void Reset_forgets_queue_and_continuation()
    {
        var library = Tracks("a", "b");
        var queue = new AudioQueue();
        queue.StartFrom(library, library[1]);
        queue.Enqueue(Track("x"));

        queue.Reset();

        Assert.Empty(queue.Manual);
        Assert.Null(queue.Context);
        Assert.Equal(-1, queue.AutoIndex);
    }
}
