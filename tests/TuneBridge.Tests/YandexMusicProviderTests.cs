using System.Net;
using System.Text;
using TuneBridge.Core.Models;
using TuneBridge.Infrastructure.Security;
using TuneBridge.Infrastructure.Yandex;
using YandexMusic;
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
          "isrc": "RUTEST000001",
          "artists": [{ "id": 7, "name": "Тестовый исполнитель" }],
          "albums": [{ "id": "201", "title": "Тестовый альбом" }]
        }
        """;

    [Fact]
    public void Anonymous_capabilities_allow_public_read_and_search_only()
    {
        using var client = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """{"result":{}}"""));
        using var provider = new YandexMusicProvider(client, new InMemoryTokenStore());

        var capabilities = provider.GetCapabilities();

        Assert.True(capabilities.CanParseUrl);
        Assert.True(capabilities.CanReadPublic);
        Assert.False(capabilities.CanReadPrivate);
        Assert.True(capabilities.CanSearch);
        Assert.False(capabilities.CanCreatePlaylist);
        Assert.False(capabilities.CanAddTracks);
        Assert.True(capabilities.RequiresOAuth);
    }

    [Fact]
    public void Authenticated_capabilities_enable_private_read_and_playlist_writes()
    {
        using var client = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """{"result":{}}"""));
        client.Authentication.SignInWithToken("unit-test-token");
        using var provider = new YandexMusicProvider(client, new InMemoryTokenStore());

        var capabilities = provider.GetCapabilities();

        Assert.True(capabilities.CanReadPublic);
        Assert.True(capabilities.CanReadPrivate);
        Assert.True(capabilities.CanCreatePlaylist);
        Assert.True(capabilities.CanAddTracks);
    }

    [Theory]
    [InlineData("https://music.yandex.ru/users/test-user/playlists/3", "test-user|3")]
    [InlineData("https://music.yandex.ru/playlists/lk.abc-123", "lk.abc-123")]
    [InlineData("https://music.yandex.com/playlists/abc", "abc")]
    [InlineData("https://music.yandex.kz/users/123/playlists/77", "123|77")]
    public void Parses_supported_playlist_urls(string url, string expectedId)
    {
        using var client = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """{"result":{}}"""));
        using var provider = new YandexMusicProvider(client);

        Assert.Equal(expectedId, provider.ParsePlaylistUrl(url)?.PlaylistId);
    }

    [Theory]
    [InlineData("https://evil-yandex.ru/playlists/abc")]
    [InlineData("https://music.yandex.ru.evil.example/playlists/abc")]
    [InlineData("https://music.yandex.ru/album/123")]
    [InlineData("not a url")]
    public void Rejects_non_playlist_or_spoofed_urls(string url)
    {
        using var client = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """{"result":{}}"""));
        using var provider = new YandexMusicProvider(client);

        Assert.Null(provider.ParsePlaylistUrl(url));
    }

    [Fact]
    public async Task Search_maps_cyrillic_track_metadata_and_album_id()
    {
        HttpRequestMessage? captured = null;
        using var client = CreateClient(request =>
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
        using var provider = new YandexMusicProvider(client);

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
        Assert.Equal("Тестовый альбом", results[0].Album);
        Assert.Equal(183000, results[0].DurationMs);
        Assert.Equal("RUTEST000001", results[0].Isrc);
        Assert.Equal("101", results[0].ProviderIds["yandex"]);
        Assert.Equal("201", results[0].Metadata["yandex.albumId"]);
    }

    [Fact]
    public async Task Reads_user_playlist_and_preserves_track_metadata()
    {
        HttpRequestMessage? captured = null;
        using var client = CreateClient(request =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "result": {
                    "uid": 42,
                    "kind": 3,
                    "trackCount": 1,
                    "title": "Публичный список",
                    "revision": 5,
                    "tracks": [
                      { "id": "101", "albumId": "201", "originalIndex": 0, "track": {{TrackJson}} }
                    ]
                  }
                }
                """);
        });
        using var provider = new YandexMusicProvider(client);

        var reference = provider.ParsePlaylistUrl(
            "https://music.yandex.ru/users/test-user/playlists/3")!;
        var playlist = await provider.GetPublicPlaylistAsync(reference);

        Assert.NotNull(captured);
        Assert.Contains("/users/test-user/playlists/3", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("Публичный список", playlist.Name);
        Assert.Single(playlist.Tracks);
        Assert.Equal("Тестовая песня", playlist.Tracks[0].Title);
        Assert.Equal("Тестовый исполнитель", playlist.Tracks[0].Artist);
        Assert.Equal("201", playlist.Tracks[0].Metadata["yandex.albumId"]);
        Assert.Equal("yandex", playlist.SourceService);
    }

    [Fact]
    public async Task Reads_uuid_playlist()
    {
        using var client = CreateClient(_ => JsonResponse(HttpStatusCode.OK, $$"""
            {
              "result": {
                "uid": 42,
                "kind": 3,
                "trackCount": 1,
                "title": "UUID playlist",
                "tracks": [{ "id": "101", "albumId": "201", "track": {{TrackJson}} }]
              }
            }
            """));
        using var provider = new YandexMusicProvider(client);

        var reference = provider.ParsePlaylistUrl(
            "https://music.yandex.ru/playlists/lk.abc-123")!;
        var playlist = await provider.GetPublicPlaylistAsync(reference);

        Assert.Equal("UUID playlist", playlist.Name);
        Assert.Single(playlist.Tracks);
    }

    [Fact]
    public async Task Creates_private_playlist_and_appends_tracks()
    {
        var changeRequests = 0;
        using var client = CreateClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/account/status", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"result":{"account":{"uid":42}}}""");

            if (path.EndsWith("/users/42/playlists/create", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"result":{"uid":42,"kind":7,"revision":1,"trackCount":0,"title":"Новое имя"}}""");

            if (path.EndsWith("/users/42/playlists/7", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return JsonResponse(HttpStatusCode.OK, """{"result":{"uid":42,"kind":7,"revision":1,"trackCount":0,"title":"Новое имя","tracks":[]}}""");

            if (path.EndsWith("/users/42/playlists/7/change", StringComparison.Ordinal))
            {
                changeRequests++;
                var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("diff=", form);
                Assert.Contains("revision=1", form);
                Assert.Contains("201", Uri.UnescapeDataString(form));
                return JsonResponse(HttpStatusCode.OK, """{"result":{"uid":42,"kind":7,"revision":2,"trackCount":1,"title":"Новое имя"}}""");
            }

            throw new InvalidOperationException($"Unexpected Yandex API call: {request.Method} {request.RequestUri}");
        });

        client.Authentication.SignInWithToken("unit-test-token");
        using var provider = new YandexMusicProvider(client, new InMemoryTokenStore());

        var id = await provider.CreatePlaylistAsync("Новое имя", isPublic: false);
        await provider.AddTracksAsync(id,
        [
            new Track
            {
                Title = "Тестовая песня",
                ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["yandex"] = "101" },
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["yandex.albumId"] = "201" }
            }
        ]);

        Assert.Equal("42|7", id);
        Assert.Equal("https://music.yandex.ru/users/42/playlists/7", provider.GetPlaylistUrl(id));
        Assert.Equal(1, changeRequests);
    }

    [Fact]
    public async Task First_insert_network_failure_is_reported_as_unknown_outcome()
    {
        using var client = CreateClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/account/status", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"result":{"account":{"uid":42}}}""");

            if (path.EndsWith("/users/42/playlists/create", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"result":{"uid":42,"kind":7,"revision":1,"trackCount":0,"title":"Новое имя"}}""");

            if (path.EndsWith("/users/42/playlists/7", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return JsonResponse(HttpStatusCode.OK, """{"result":{"uid":42,"kind":7,"revision":1,"trackCount":0,"title":"Новое имя","tracks":[]}}""");

            if (path.EndsWith("/users/42/playlists/7/change", StringComparison.Ordinal))
                throw new HttpRequestException("Connection lost after request dispatch.");

            throw new InvalidOperationException($"Unexpected Yandex API call: {request.Method} {request.RequestUri}");
        });

        client.Authentication.SignInWithToken("unit-test-token");
        using var provider = new YandexMusicProvider(client, new InMemoryTokenStore());
        var playlistId = await provider.CreatePlaylistAsync("Новое имя");

        var exception = await Assert.ThrowsAsync<YandexPartialTransferException>(() =>
            provider.AddTracksAsync(playlistId,
            [
                new Track
                {
                    Title = "Тестовая песня",
                    ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["yandex"] = "101" },
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["yandex.albumId"] = "201" }
                }
            ]));

        Assert.Equal(0, exception.ConfirmedTracksAdded);
        Assert.True(exception.CurrentOperationOutcomeUnknown);
    }

    [Fact]
    public async Task Search_rejects_empty_query_and_non_positive_limit()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("No request expected"));
        using var provider = new YandexMusicProvider(client);

        Assert.Empty(await provider.SearchTrackAsync(new Track()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.SearchTrackAsync(new Track { Title = "test" }, 0));
    }

    [Fact]
    public async Task Playlist_writes_require_authentication()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("No request expected"));
        using var provider = new YandexMusicProvider(client);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.CreatePlaylistAsync("test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.AddTracksAsync("42|7", Array.Empty<Track>()));
    }

    private static YandexMusicClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => new(new YandexMusicClientOptions
        {
            ApiBaseUri = new Uri("https://api.music.yandex.net/"),
            Language = "ru",
            HandlerFactory = () => new StubHandler(responseFactory)
        });

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
