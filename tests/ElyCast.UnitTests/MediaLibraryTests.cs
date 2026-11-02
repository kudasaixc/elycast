using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Library;
using Xunit;
using static ElyCast.UnitTests.TestData;

namespace ElyCast.UnitTests;

public class MediaLibraryTests
{
    private readonly InMemoryStateStore _store = new();
    private readonly MediaLibrary _library;

    public MediaLibraryTests() => _library = new MediaLibrary(_store);

    [Fact]
    public void Deduplication_ignores_path_case_and_non_local_items()
    {
        var items = new List<PlayItem>
        {
            Track("Song"), PlayItem.FromLocalFile(@"c:\music\SONG.mp3"), Live("1"), Track("Other")
        };

        var result = MediaLibrary.DeduplicateLocal(items);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Favorite_toggle_adds_a_detached_copy_and_removes_it_again()
    {
        var channel = Live("42");
        channel.IsFavorite = true;

        Assert.False(_library.CommitFavoriteToggle(channel));
        var copy = Assert.Single(_library.Profile.Favorites);
        Assert.NotSame(channel, copy);

        channel.IsFavorite = false;
        Assert.True(_library.CommitFavoriteToggle(channel));
        Assert.Empty(_library.Profile.Favorites);
        Assert.Equal(2, _store.SaveCount);
    }

    [Fact]
    public void Opening_a_profile_marks_its_favorites_and_resets_lazy_lists()
    {
        var favorites = _store.ForProfile("http://host|user").Favorites;
        favorites.Add(Live("2"));
        _library.MovieItems = new List<PlayItem>();
        var source = new IptvService();
        var profileKey = typeof(IptvService).GetProperty(nameof(IptvService.ProfileKey))!;
        profileKey.SetValue(source, "http://host|user");

        _library.OpenProfile(source, [Live("1"), Live("2")]);

        Assert.Same(source, _library.Source);
        Assert.Null(_library.MovieItems);
        Assert.Equal([false, true], _library.LiveItems.Select(i => i.IsFavorite));
    }

    [Fact]
    public void Live_favorites_follow_the_stream_address_not_the_row_number()
    {
        _library.Profile.Favorites.Add(Live("7", "http://cdn/stream.ts"));
        var reordered = Live("99", "http://cdn/stream.ts");

        _library.MarkFavorites([reordered]);

        Assert.True(reordered.IsFavorite);
    }

    [Fact]
    public void Removing_local_tracks_cleans_playlists_favorites_and_history()
    {
        var keep = Track("keep");
        var drop = Track("drop");
        _store.Current.LocalAudioLibrary = [keep, drop];
        _library.LoadLocal();
        _store.Current.LocalPlaylists.Add(new LocalPlaylist { TrackPaths = [drop.DirectUrl!, keep.DirectUrl!] });
        var profile = _store.ForProfile("p");
        profile.Favorites.Add(Track("drop"));
        profile.LastPlayed = Track("drop");

        var matches = _library.RemoveLocal([drop]);

        Assert.NotNull(matches);
        Assert.True(matches!(Track("drop")));
        Assert.Equal([keep.Name], _library.LocalAudio.Select(t => t.Name));
        Assert.Equal([keep.DirectUrl!], _store.Current.LocalPlaylists[0].TrackPaths);
        Assert.Empty(profile.Favorites);
        Assert.Null(profile.LastPlayed);
    }

    [Fact]
    public void Removing_nothing_local_is_a_no_op()
    {
        Assert.Null(_library.RemoveLocal([Live("1")]));
    }

    [Fact]
    public void Imports_report_only_new_entries()
    {
        _store.Current.LocalAudioLibrary = [Track("a")];
        _library.LoadLocal();

        var added = _library.ApplyImported([Track("a"), Track("b")], audio: true);

        Assert.Equal(1, added);
        Assert.Equal(2, _library.LocalAudio.Count);
    }

    [Fact]
    public void Background_enrichment_never_resurrects_a_removed_track()
    {
        var a = Track("a");
        var b = Track("b");
        _store.Current.LocalAudioLibrary = [a, b];
        _library.LoadLocal();
        var snapshot = _library.LocalAudio.Select(t => new PlayItem
        {
            Kind = t.Kind, Id = t.Id, DirectUrl = t.DirectUrl, Name = t.Name + " (tagged)", Ext = t.Ext
        }).ToList();
        _library.RemoveLocal([b]);

        _library.MergeEnrichedAudio(snapshot);

        var only = Assert.Single(_library.LocalAudio);
        Assert.Equal("a (tagged)", only.Name);
        Assert.Single(_store.Current.LocalAudioLibrary);
    }

    [Fact]
    public void A_moved_favorite_is_repaired_from_a_unique_file_name_match()
    {
        var moved = Track("song", @"D:\New");
        _store.Current.LocalAudioLibrary = [moved];
        var favorite = Track("song", @"Z:\Gone");
        _store.ForProfile("p").Favorites.Add(favorite);

        _library.LoadLocal();

        Assert.Equal(moved.DirectUrl, favorite.DirectUrl);
    }

    [Fact]
    public void Local_only_mode_uses_a_dedicated_profile_and_no_remote_lists()
    {
        _library.LiveItems.Add(Live("1"));

        _library.OpenLocalOnly();

        Assert.Empty(_library.LiveItems);
        Assert.Same(_store.ForProfile("local-only"), _library.Profile);
        Assert.Equal("", _library.Source.ProfileKey);
    }
}
