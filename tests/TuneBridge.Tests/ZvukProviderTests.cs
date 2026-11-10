using System.Net;
using System.Text;
using TuneBridge.Infrastructure.Zvuk;
using Xunit;

namespace TuneBridge.Tests;

public class ZvukProviderTests
{
    [Fact]
    public void Is_experimental_and_read_search_only()
    {
        var provider = new ZvukProvider();
        var caps = provider.GetCapabilities();

        Assert.True(provider.IsExperimental);
        Assert.True(caps.IsExperimental);
        Assert.True(caps.CanReadPublic);
        Assert.True(caps.CanSearch);
        Assert.False(caps.CanCreatePlaylist);
        Assert.False(caps.CanAddTracks);
        Assert.False(caps.RequiresOAuth);
    }

    [Fact]
    public void Parses_zvuk_playlist_urls_and_rejects_spoofed_hosts()
    {
        var provider = new ZvukProvider();

        Assert.Equal("abc", provider.ParsePlaylistUrl("https://zvuk.com/playlist/abc")?.PlaylistId);
        Assert.Equal("123", provider.ParsePlaylistUrl("https://www.sber-zvuk.com/playlist/123")?.PlaylistId);
        Assert.Null(provider.ParsePlaylistUrl("https://open.spotify.com/playlist/x"));
        Assert.Null(provider.ParsePlaylistUrl("https://evilzvuk.com/playlist/123"));
        Assert.Null(provider.ParsePlaylistUrl("https://example.com/?next=zvuk.com/playlist/123"));
        Assert.Null(provider.ParsePlaylistUrl("https://zvuk.com/playlist/123/extra"));
        Assert.Null(provider.ParsePlaylistUrl("https://zvuk.com/other/playlist/123"));
        Assert.Null(provider.ParsePlaylistUrl("https://zvuk.com/playlist/%2Fprivate"));
    }

    [Fact]
    public async Task Reads_public_playlist_and_maps_tracks()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://zvuk.com/api/tiny/profile")
                return JsonResponse("""{"result":{"token":"anonymous-token"}}""");

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("anonymous-token", Assert.Single(request.Headers.GetValues("X-Auth-Token")));
            return JsonResponse("""
                {"data":{"playlists":[{"id":"123","title":"Избранное","description":"Мой список",
                "tracks":[{"id":"55","title":"Моя любовь","duration":181,"position":0,
                "artists":[{"id":"7","title":"Исполнитель"}],"release":{"id":"9","title":"Альбом"}}]}]}}
                """);
        });

        var provider = new ZvukProvider(new HttpClient(handler));
        var reference = provider.ParsePlaylistUrl("https://zvuk.com/playlist/123")!;

        var playlist = await provider.GetPublicPlaylistAsync(reference);

        Assert.Equal("Избранное", playlist.Name);
        Assert.Equal("zvuk", playlist.SourceService);
        var track = Assert.Single(playlist.Tracks);
        Assert.Equal("Моя любовь", track.Title);
        Assert.Equal("Исполнитель", track.Artist);
        Assert.Equal("Альбом", track.Album);
        Assert.Equal(181_000, track.DurationMs);
        Assert.Equal("55", track.ProviderIds["zvuk"]);
    }

    [Fact]
    public async Task Rejects_playlist_not_confirmed_as_public()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://zvuk.com/api/tiny/profile")
                return JsonResponse("""{"result":{"token":"anonymous-token"}}""");

            return JsonResponse("""{"data":{"playlists":[{"id":"123","title":"Private","isPublic":false,"tracks":[]}]}}""");
        });

        var provider = new ZvukProvider(new HttpClient(handler));
        var reference = provider.ParsePlaylistUrl("https://zvuk.com/playlist/123")!;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            provider.GetPublicPlaylistAsync(reference));
    }

    [Fact]
    public async Task Search_returns_only_track_results()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://zvuk.com/api/tiny/profile")
                return JsonResponse("""{"result":{"token":"anonymous-token"}}""");

            return JsonResponse("""
                {"data":{"quickSearch":{"content":[
                  {"__typename":"Artist","id":"7","title":"Исполнитель"},
                  {"__typename":"Track","id":"55","title":"Моя любовь","duration":181,
                   "artistNames":["Исполнитель"],"release":{"title":"Альбом"}}
                ]}}}
                """);
        });

        var provider = new ZvukProvider(new HttpClient(handler));
        var results = await provider.SearchTrackAsync(new TuneBridge.Core.Models.Track
        {
            Title = "Моя любовь",
            Artist = "Исполнитель"
        });

        var track = Assert.Single(results);
        Assert.Equal("Моя любовь", track.Title);
        Assert.Equal("Исполнитель", track.Artist);
        Assert.Equal("55", track.ProviderIds["zvuk"]);
    }

    [Fact]
    public async Task Refreshes_expired_anonymous_token_once_after_unauthorized_response()
    {
        var profileRequests = 0;
        var graphQlRequests = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://zvuk.com/api/tiny/profile")
            {
                profileRequests++;
                var token = profileRequests == 1 ? "expired-token" : "fresh-token";
                return JsonResponse($"""{"result":{"token":"{{token}}"}}""");
            }

            graphQlRequests++;
            var suppliedToken = Assert.Single(request.Headers.GetValues("X-Auth-Token"));
            if (graphQlRequests == 1)
            {
                Assert.Equal("expired-token", suppliedToken);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            Assert.Equal("fresh-token", suppliedToken);
            return JsonResponse("""{"data":{"quickSearch":{"content":[]}}}""");
        });

        var provider = new ZvukProvider(new HttpClient(handler));
        var results = await provider.SearchTrackAsync(new TuneBridge.Core.Models.Track
        {
            Title = "Песня",
            Artist = "Исполнитель"
        });

        Assert.Empty(results);
        Assert.Equal(2, profileRequests);
        Assert.Equal(2, graphQlRequests);
    }

    [Fact]
    public async Task Playlist_creation_and_track_insertion_are_explicitly_unsupported()
    {
        var provider = new ZvukProvider();

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreatePlaylistAsync("Test"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.AddTracksAsync("123", []));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
