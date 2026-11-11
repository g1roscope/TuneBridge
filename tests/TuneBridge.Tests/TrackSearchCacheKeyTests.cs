using TuneBridge.Core.Matching;
using TuneBridge.Core.Models;
using Xunit;

namespace TuneBridge.Tests;

public sealed class TrackSearchCacheKeyTests
{
    [Fact]
    public void Spotify_uses_isrc_when_available()
    {
        var first = new Track { Isrc = "usabc1234567", Artist = "Artist A", Title = "Title A" };
        var second = new Track { Isrc = "USABC1234567", Artist = "Artist B", Title = "Title B" };

        Assert.Equal(
            TrackSearchCacheKey.Create("spotify", first),
            TrackSearchCacheKey.Create("spotify", second));
    }

    [Fact]
    public void Yandex_uses_text_query_even_when_isrc_is_available()
    {
        var first = new Track { Isrc = "USABC1234567", Artist = "Artist A", Title = "Title A" };
        var second = new Track { Isrc = "USABC1234567", Artist = "Artist B", Title = "Title B" };

        Assert.NotEqual(
            TrackSearchCacheKey.Create("yandex", first),
            TrackSearchCacheKey.Create("yandex", second));
    }

    [Fact]
    public void Text_search_key_reuses_only_the_same_artist_and_title()
    {
        var first = new Track { Artist = "Artist", Title = "Title" };
        var duplicate = new Track { Artist = "Artist", Title = "Title" };
        var different = new Track { Artist = "Artist", Title = "Other title" };

        Assert.Equal(
            TrackSearchCacheKey.Create("yandex", first),
            TrackSearchCacheKey.Create("yandex", duplicate));
        Assert.NotEqual(
            TrackSearchCacheKey.Create("yandex", first),
            TrackSearchCacheKey.Create("yandex", different));
    }

    [Fact]
    public void Cache_keys_are_scoped_to_provider()
    {
        var track = new Track { Artist = "Artist", Title = "Title", Isrc = "USABC1234567" };

        Assert.NotEqual(
            TrackSearchCacheKey.Create("spotify", track),
            TrackSearchCacheKey.Create("yandex", track));
    }
}
