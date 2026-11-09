using System.Net;
using System.Text;
using TuneBridge.Core.Models;
using TuneBridge.Infrastructure.Yandex;
using Xunit;

namespace TuneBridge.Tests;

public sealed class YandexMusicProviderTests
{
    private const string TrackJson = """
        {
          "id": "101",
          "title": "Тестовая песня",
          "available": true,
          "durationMs": 183000,
          "artists": [{ "id": 7, "name": "Тестовый исполнитель" }],
          "albums": []
        }
        """;

    [Fact]
    public void Capabilities_are_anonymous_read_and_search_only()
    {
        var provider = new YandexMusicProvider(new HttpClient(new StubHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"result":{}}"""))));

        var capabilities = provider.GetCapabilities();

        Assert.True(capabilities.CanParseUrl);
        Assert.True(capabilities.CanReadPublic);
        Assert.False(capabilities.CanReadPrivate);
        Assert.True(capabilities.CanSearch);
        Assert.False(capabilities.CanCreatePlaylist);
        Assert.False(capabilities.CanAddTracks);
        Assert.False(capabilities.RequiresOAuth);
    }

    [Theory]
    [InlineData("https://music.yandex.ru/users/test-user/playlists/3", "test-user|3")]
    [InlineData("https://music.yandex.ru/playlists/lk.abc-123", "lk.abc-123")]
    [InlineData("https://music.yandex.com/playlists/abc", "abc")]
    public void Parses_supported_public_playlist_urls(string url, string expectedId)
    {
        var provider = CreateProvider(_ => JsonResponse(HttpStatusCode.OK, """{"result":{}}"""));

        Assert.Equal(expectedId, provider.ParsePlaylistUrl(url)?.PlaylistId);
    }

    [Theory]
    [InlineData("https://evil-yandex.ru/playlists/abc")]
    [InlineData("https://music.yandex.ru.evil.example/playlists/abc")]
    [InlineData("https://music.yandex.ru/album/123")]
    [InlineData("not a url")]
    public void Rejects_non_playlist_or_spoofed_urls(string url)
    {
        var provider = CreateProvider(_ => JsonResponse(HttpStatusCode.OK, """{"result":{}}"""));

        Assert.Null(provider.ParsePlaylistUrl(url));
    }

    [Fact]
    public async Task Search_uses_anonymous_http_client_and_maps_track_metadata()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(request =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "result": {
                    "tracks": {
                      "results": [{{TrackJson}}]
                    }
                  }
                }
                """);
        });

        var results = await provider.SearchTrackAsync(new Track
        {
            Title = "Тестовая песня",
            Artist = "Тестовый исполнитель"
        });

        Assert.NotNull(captured);
        Assert.Equal("api.music.yandex.net", captured!.RequestUri!.Host);
        Assert.Contains("/search?", captured.RequestUri.PathAndQuery);
        Assert.Single(results);
        Assert.Equal("Тестовая песня", results[0].Title);
        Assert.Equal("Тестовый исполнитель", results[0].Artist);
        Assert.Equal(183000, results[0].DurationMs);
        Assert.Equal("101", results[0].ProviderIds["yandex"]);
        Assert.Equal(string.Empty, results[0].Album);
    }

    [Fact]
    public async Task Reads_legacy_public_playlist_and_maps_tracks()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(request =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "result": {
                    "uid": 42,
                    "kind": 3,
                    "trackCount": 1,
                    "title": "Публичный список",
                    "owner": { "uid": 42, "login": "test-user", "name": "Test User", "verified": false },
                    "cover": { "type": "pic", "uri": "example.org/cover/%%" },
                    "tracks": [
                      { "id": "101", "track": {{TrackJson}} }
                    ]
                  }
                }
                """);
        });

        var reference = provider.ParsePlaylistUrl(
            "https://music.yandex.ru/users/test-user/playlists/3")!;
        var playlist = await provider.GetPublicPlaylistAsync(reference);

        Assert.NotNull(captured);
        Assert.Contains("/users/test-user/playlists/3", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("Публичный список", playlist.Name);
        Assert.Single(playlist.Tracks);
        Assert.Equal("Тестовая песня", playlist.Tracks[0].Title);
        Assert.Equal("yandex", playlist.SourceService);
    }

    [Fact]
    public async Task Reads_uuid_public_playlist()
    {
        var provider = CreateProvider(_ => JsonResponse(HttpStatusCode.OK, $$"""
            {
              "result": {
                "uid": 42,
                "kind": 3,
                "trackCount": 1,
                "title": "UUID playlist",
                "owner": { "uid": 42, "login": "test-user", "name": "Test User", "verified": false },
                "cover": { "type": "pic", "uri": "example.org/cover/%%" },
                "tracks": [{ "id": "101", "track": {{TrackJson}} }]
              }
            }
            """));

        var reference = provider.ParsePlaylistUrl(
            "https://music.yandex.ru/playlists/lk.abc-123")!;
        var playlist = await provider.GetPublicPlaylistAsync(reference);

        Assert.Equal("UUID playlist", playlist.Name);
        Assert.Single(playlist.Tracks);
    }

    [Fact]
    public async Task Search_maps_401_to_unauthorized_access()
    {
        var provider = CreateProvider(_ => JsonResponse(HttpStatusCode.Unauthorized,
            """{"result":{"name":"Unauthorized","message":"Authentication required"}}"""));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            provider.SearchTrackAsync(new Track { Title = "test" }));
    }

    [Fact]
    public async Task Search_preserves_network_failure()
    {
        var provider = CreateProvider(_ => throw new HttpRequestException("network down"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.SearchTrackAsync(new Track { Title = "test" }));
    }

    [Fact]
    public async Task Search_rejects_empty_query_and_non_positive_limit()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No request expected"));

        Assert.Empty(await provider.SearchTrackAsync(new Track()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.SearchTrackAsync(new Track { Title = "test" }, 0));
    }

    [Fact]
    public async Task Playlist_write_operations_are_explicitly_unsupported()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No request expected"));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreatePlaylistAsync("test"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.AddTracksAsync("id", Array.Empty<Track>()));
    }

    private static YandexMusicProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => new(new HttpClient(new StubHandler(responseFactory)));

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}