using TuneBridge.Infrastructure.Spotify;

namespace TuneBridge.Tests;

public class SpotifyParseTests
{
    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    public void Parses_playlist_url(string url, string expectedId)
    {
        var provider = new SpotifyProvider(new HttpClient(), new SpotifyOptions());
        var parsed = provider.ParsePlaylistUrl(url);
        Assert.NotNull(parsed);
        Assert.Equal("spotify", parsed!.ProviderId);
        Assert.Equal(expectedId, parsed.PlaylistId);
    }

    [Fact]
    public void Parses_localized_spotify_playlist_url()
    {
        var provider = new SpotifyProvider(new HttpClient(), new SpotifyOptions());
        var parsed = provider.ParsePlaylistUrl("https://open.spotify.com/intl-de/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc");

        Assert.NotNull(parsed);
        Assert.Equal("37i9dQZF1DXcBWIGoYBM5M", parsed!.PlaylistId);
    }

    [Theory]
    [InlineData("https://evil.example/open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com.evil.example/playlist/37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M/extra")]
    [InlineData("https://open.spotify.com/track/37i9dQZF1DXcBWIGoYBM5M")]
    public void Rejects_spoofed_or_non_playlist_urls(string url)
    {
        var provider = new SpotifyProvider(new HttpClient(), new SpotifyOptions());

        Assert.Null(provider.ParsePlaylistUrl(url));
    }

    [Fact]
    public void Unknown_url_returns_null()
    {
        var provider = new SpotifyProvider(new HttpClient(), new SpotifyOptions());
        Assert.Null(provider.ParsePlaylistUrl("https://example.com/not-a-playlist"));
    }

    [Fact]
    public void Capabilities_without_creds_disallow_network()
    {
        var provider = new SpotifyProvider(new HttpClient(), new SpotifyOptions());
        var caps = provider.GetCapabilities();
        Assert.False(caps.CanReadPublic);
        Assert.False(caps.CanSearch);
        Assert.True(caps.CanParseUrl);
    }

    [Fact]
    public async Task Public_playlist_without_creds_throws()
    {
        var provider = new SpotifyProvider(new HttpClient(), new SpotifyOptions { ClientId = null });
        var reference = provider.ParsePlaylistUrl("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M");
        Assert.NotNull(reference);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetPublicPlaylistAsync(reference!));
    }
}
