using System.Net;
using System.Text;
using System.Text.Json;
using TuneBridge.Core.Models;
using TuneBridge.Infrastructure.Security;
using TuneBridge.Infrastructure.Spotify;

namespace TuneBridge.Tests;

/// <summary>
/// User OAuth token must never be overwritten by client_credentials app token.
/// Pagination, state validation, refresh-token keep, network resilience.
/// </summary>
public class SpotifyTokenSeparationTests
{
    // ─── 1. User token survives EnsureAppToken ───────────────────────────

    [Fact]
    public async Task EnsureAppToken_does_not_overwrite_user_token_when_secret_missing()
    {
        using var http = new HttpClient();
        var options = new SpotifyOptions { ClientId = "cid" }; // no secret
        var provider = new SpotifyProvider(http, options);

        provider.SetUserTokens("user-access-xyz", DateTimeOffset.UtcNow.AddHours(1), "refresh-xyz");
        Assert.Equal("user-access-xyz", provider.UserAccessTokenForTests);
        Assert.True(provider.HasValidUserToken);

        // No ClientSecret → cannot fetch app token, but must not clear user
        // HasValidUserToken is true → EnsureAppToken returns early without network
        await provider.EnsureAppTokenAsync();

        Assert.Equal("user-access-xyz", provider.UserAccessTokenForTests);
        Assert.Null(provider.AppAccessTokenForTests);
        Assert.True(provider.HasValidUserToken);
        Assert.Equal("user-access-xyz", provider.EffectiveAccessToken);
    }

    [Fact]
    public async Task EnsureAppToken_writes_only_app_slot_and_preserves_user()
    {
        var handler = new QueueHandler();
        // client_credentials response
        handler.Enqueue(JsonResponse(200, """{"access_token":"app-token-abc","token_type":"Bearer","expires_in":3600}"""));

        using var http = new HttpClient(handler);
        var options = new SpotifyOptions
        {
            ClientId = "cid",
            ClientSecret = "secret"
        };
        var provider = new SpotifyProvider(http, options);

        // User token still valid → EnsureAppToken must NOT call network / overwrite
        provider.SetUserTokens("user-tok", DateTimeOffset.UtcNow.AddHours(2), "rt");
        await provider.EnsureAppTokenAsync();
        Assert.Equal("user-tok", provider.UserAccessTokenForTests);
        Assert.Null(provider.AppAccessTokenForTests); // never requested
        Assert.Equal(0, handler.RequestCount);

        // Expire user token → now app token may be fetched into app slot only
        provider.SetUserTokens("user-tok-expired", DateTimeOffset.UtcNow.AddMinutes(-10), "rt");
        Assert.False(provider.HasValidUserToken);

        await provider.EnsureAppTokenAsync();
        Assert.Equal("user-tok-expired", provider.UserAccessTokenForTests); // user slot unchanged
        Assert.Equal("app-token-abc", provider.AppAccessTokenForTests);
        Assert.True(provider.HasValidAppToken);
        Assert.Equal(1, handler.RequestCount);
    }

    // ─── 2. Refresh keeps old refresh_token when Spotify omits it ────────

    [Fact]
    public async Task Refresh_keeps_previous_refresh_token_when_response_omits_it()
    {
        var handler = new QueueHandler();
        handler.Enqueue(JsonResponse(200,
            """{"access_token":"new-access","token_type":"Bearer","expires_in":3600}""")); // no refresh_token

        using var http = new HttpClient(handler);
        var store = new InMemoryTokenStore();
        var options = new SpotifyOptions { ClientId = "cid" };
        var session = new SpotifyAuthSession(http, options, store);

        // Seed via store + restore
        var seed = JsonSerializer.Serialize(new
        {
            AccessToken = "old-access",
            RefreshToken = "keep-me-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10), // force refresh path
            DisplayName = "U"
        });
        await store.SaveAsync(SpotifyAuthSession.TokenStoreKey, seed);
        await session.RestoreAsync(); // may attempt refresh (handler will respond)

        // If restore already refreshed, check; otherwise call Refresh explicitly
        if (session.AccessToken == "old-access" || string.IsNullOrEmpty(session.AccessToken))
            await session.RefreshAsync();

        Assert.Equal("new-access", session.AccessToken);
        Assert.Equal("keep-me-refresh", session.RefreshToken);
    }

    // ─── 3. Temporary network error does not wipe authorization ──────────

    [Fact]
    public async Task Restore_network_error_on_refresh_keeps_tokens_in_store()
    {
        var handler = new QueueHandler();
        handler.Enqueue(new HttpRequestException("simulated network down"));

        using var http = new HttpClient(handler);
        var store = new InMemoryTokenStore();
        var options = new SpotifyOptions { ClientId = "cid" };

        var seed = JsonSerializer.Serialize(new
        {
            AccessToken = "still-valid-access",
            RefreshToken = "rt-keep",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-30), // expired → try refresh
            DisplayName = "NetUser"
        });
        await store.SaveAsync(SpotifyAuthSession.TokenStoreKey, seed);

        var session = new SpotifyAuthSession(http, options, store);
        await session.RestoreAsync(); // must swallow HttpRequestException

        // Tokens remain in memory and store
        Assert.Equal("still-valid-access", session.AccessToken);
        Assert.Equal("rt-keep", session.RefreshToken);
        Assert.NotNull(await store.LoadAsync(SpotifyAuthSession.TokenStoreKey));
    }

    // ─── 4. Pagination loads all pages ───────────────────────────────────

    [Fact]
    public async Task Playlist_items_pagination_loads_all_pages()
    {
        var handler = new QueueHandler();
        // metadata
        handler.Enqueue(JsonResponse(200, """{"id":"pl1","name":"Test PL","description":"","external_urls":{"spotify":"https://open.spotify.com/playlist/pl1"}}"""));
        // page 1 with next
        handler.Enqueue(JsonResponse(200, """
            {
              "items": [
                {"item":{"id":"t1","name":"Song One","type":"track","artists":[{"name":"A"}],"album":{"name":"Alb"},"duration_ms":180000,"external_ids":{},"external_urls":{},"is_local":false}}
              ],
              "next":"https://api.spotify.com/v1/playlists/pl1/items?offset=50&limit=50"
            }
            """));
        // page 2 final
        handler.Enqueue(JsonResponse(200, """
            {
              "items": [
                {"item":{"id":"t2","name":"Song Two","type":"track","artists":[{"name":"B"}],"album":{"name":"Alb2"},"duration_ms":200000,"external_ids":{},"external_urls":{},"is_local":false}}
              ],
              "next":null
            }
            """));

        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "c" });
        provider.SetUserTokens("tok", DateTimeOffset.UtcNow.AddHours(1));

        var pl = await provider.GetPlaylistWithTokenAsync("pl1");
        Assert.Equal(2, pl.Tracks.Count);
        Assert.Equal("Song One", pl.Tracks[0].Title);
        Assert.Equal("Song Two", pl.Tracks[1].Title);
        Assert.Equal(3, handler.RequestCount); // meta + 2 pages
    }

    // ─── 5. Callback rejects wrong state ─────────────────────────────────

    [Fact]
    public async Task Callback_rejects_incorrect_state()
    {
        const string expectedState = "expected-state-value";
        await using var listener = new LoopbackHttpListener(43999, expectedState);
        listener.Start();

        var waitTask = listener.WaitForCodeAsync(TimeSpan.FromSeconds(5));

        // Simulate browser hitting callback with wrong state
        using var client = new HttpClient();
        var url = $"http://127.0.0.1:43999/callback/?code=abc&state=WRONG_STATE";
        try
        {
            await client.GetAsync(url);
        }
        catch
        {
            // connection may close after response
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await waitTask);
        Assert.Contains("state", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Callback_accepts_matching_state_and_returns_code()
    {
        const string expectedState = "good-state";
        await using var listener = new LoopbackHttpListener(43998, expectedState);
        listener.Start();

        var waitTask = listener.WaitForCodeAsync(TimeSpan.FromSeconds(5));

        using var client = new HttpClient();
        var url = $"http://127.0.0.1:43998/callback/?code=auth-code-99&state={Uri.EscapeDataString(expectedState)}";
        try
        {
            await client.GetAsync(url);
        }
        catch
        {
        }

        var code = await waitTask;
        Assert.Equal("auth-code-99", code);
    }

    // ─── 6. OAuth / HTTP cancellation ────────────────────────────────────

    [Fact]
    public async Task WaitForCode_respects_cancellation_token()
    {
        await using var listener = new LoopbackHttpListener(43997, "st");
        listener.Start();

        using var cts = new CancellationTokenSource();
        var waitTask = listener.WaitForCodeAsync(TimeSpan.FromMinutes(1), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
    }

    // ─── 7. Logout / ClearUserTokens ─────────────────────────────────────

    [Fact]
    public async Task Logout_clears_user_tokens_on_session_and_provider_keeps_app_token()
    {
        var store = new InMemoryTokenStore();
        var options = new SpotifyOptions { ClientId = "cid" };
        using var http = new HttpClient();
        var session = new SpotifyAuthSession(http, options, store);
        var provider = new SpotifyProvider(http, options);

        // Simulate signed-in user + optional app token
        var seed = JsonSerializer.Serialize(new
        {
            AccessToken = "user-at",
            RefreshToken = "user-rt",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            DisplayName = "U"
        });
        await store.SaveAsync(SpotifyAuthSession.TokenStoreKey, seed);
        await session.RestoreAsync();
        Assert.True(session.IsAuthenticated);
        Assert.Equal("user-rt", session.RefreshToken);

        provider.SetUserTokens("user-at", DateTimeOffset.UtcNow.AddHours(1), "user-rt");
        Assert.Equal("user-at", provider.UserAccessTokenForTests);

        await session.LogoutAsync();
        provider.ClearUserTokens(); // same sequence as MainViewModel.DisconnectSpotifyAsync

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.AccessToken);
        Assert.Null(session.RefreshToken);
        Assert.Null(await store.LoadAsync(SpotifyAuthSession.TokenStoreKey));

        Assert.Null(provider.UserAccessTokenForTests);
        Assert.False(provider.HasValidUserToken);
        // App token was never set in this scenario — remains null (not wiped by ClearUserTokens)
        Assert.Null(provider.AppAccessTokenForTests);
    }

    [Fact]
    public async Task ClearUserTokens_does_not_clear_app_token_slot()
    {
        var handler = new QueueHandler();
        handler.Enqueue(JsonResponse(200,
            """{"access_token":"app-only","token_type":"Bearer","expires_in":3600}"""));

        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions
        {
            ClientId = "cid",
            ClientSecret = "sec"
        });

        // No user token → EnsureAppToken fills app slot
        await provider.EnsureAppTokenAsync();
        Assert.Equal("app-only", provider.AppAccessTokenForTests);
        Assert.True(provider.HasValidAppToken);

        // Also set a user token, then clear only user
        provider.SetUserTokens("user-x", DateTimeOffset.UtcNow.AddHours(1), "rt");
        Assert.True(provider.HasValidUserToken);

        provider.ClearUserTokens();

        Assert.Null(provider.UserAccessTokenForTests);
        Assert.False(provider.HasValidUserToken);
        Assert.Equal("app-only", provider.AppAccessTokenForTests);
        Assert.True(provider.HasValidAppToken);
        Assert.Equal("app-only", provider.EffectiveAccessToken);
    }

    // ─── 8. Write ops reject app token / expired user ────────────────────

    [Fact]
    public async Task CreatePlaylist_rejects_app_token_only()
    {
        var handler = new QueueHandler();
        handler.Enqueue(JsonResponse(200,
            """{"access_token":"app-tok","token_type":"Bearer","expires_in":3600}"""));

        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions
        {
            ClientId = "cid",
            ClientSecret = "sec"
        });
        await provider.EnsureAppTokenAsync();
        Assert.True(provider.HasValidAppToken);
        Assert.False(provider.HasValidUserToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CreatePlaylistAsync("My PL"));
        Assert.Contains("user OAuth", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount); // only client_credentials; no /me/playlists
    }

    [Fact]
    public async Task CreatePlaylist_rejects_expired_user_token_even_if_app_present()
    {
        var handler = new QueueHandler();
        handler.Enqueue(JsonResponse(200,
            """{"access_token":"app-tok","token_type":"Bearer","expires_in":3600}"""));

        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions
        {
            ClientId = "cid",
            ClientSecret = "sec"
        });
        await provider.EnsureAppTokenAsync();

        // Expired user token still in slot — must NOT fall through to app token
        provider.SetUserTokens("expired-user", DateTimeOffset.UtcNow.AddMinutes(-30), "rt");
        Assert.False(provider.HasValidUserToken);
        Assert.True(provider.HasValidAppToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CreatePlaylistAsync("My PL"));
        Assert.Contains("user OAuth", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AddTracks_rejects_app_token_only()
    {
        var handler = new QueueHandler();
        handler.Enqueue(JsonResponse(200,
            """{"access_token":"app-tok","token_type":"Bearer","expires_in":3600}"""));

        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions
        {
            ClientId = "cid",
            ClientSecret = "sec"
        });
        await provider.EnsureAppTokenAsync();

        var track = new Track
        {
            Title = "T",
            ProviderIds = new Dictionary<string, string> { ["spotify"] = "trackid1" }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.AddTracksAsync("pl1", new[] { track }));
        Assert.Contains("user OAuth", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Write_ops_use_user_token_when_both_user_and_app_exist()
    {
        var capture2 = new CapturingHandler();
        capture2.Enqueue(JsonResponse(200,
            """{"access_token":"app-should-not-be-used","token_type":"Bearer","expires_in":3600}"""));
        capture2.Enqueue(JsonResponse(200, """{"id":"pl-ok"}"""));
        using var http3 = new HttpClient(capture2);
        var p3 = new SpotifyProvider(http3, new SpotifyOptions
        {
            ClientId = "cid",
            ClientSecret = "sec"
        });
        // First call Ensure while user is expired so app fills
        p3.SetUserTokens("stale", DateTimeOffset.UtcNow.AddMinutes(-5), "rt");
        await p3.EnsureAppTokenAsync();
        Assert.Equal("app-should-not-be-used", p3.AppAccessTokenForTests);
        // Restore valid user
        p3.SetUserTokens("user-write-tok", DateTimeOffset.UtcNow.AddHours(1), "rt");
        Assert.True(p3.HasValidUserToken);
        Assert.True(p3.HasValidAppToken);

        var id = await p3.CreatePlaylistAsync("X");
        Assert.Equal("pl-ok", id);
        Assert.Equal(2, capture2.RequestCount);
        Assert.Equal("user-write-tok", capture2.LastBearerToken);
    }


    [Fact]
    public async Task Search_retries_rate_limited_request_using_retry_after()
    {
        var handler = new QueueHandler();
        var limited = JsonResponse(429, """{"error":"rate limited"}""");
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        handler.Enqueue(limited);
        handler.Enqueue(JsonResponse(200,
            """{"tracks":{"items":[{"id":"track-1","name":"Song","artists":[{"name":"Artist"}],"album":{"name":"Album"},"duration_ms":180000,"external_urls":{"spotify":"https://open.spotify.com/track/track-1"}}]}}"""));

        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "cid" });
        provider.SetUserTokens("user-token", DateTimeOffset.UtcNow.AddHours(1));

        var tracks = await provider.SearchTrackAsync(new Track { Title = "Song", Artist = "Artist" });

        Assert.Equal(2, handler.RequestCount);
        Assert.Single(tracks);
        Assert.Equal("track-1", tracks[0].ProviderIds["spotify"]);
    }

    [Fact]
    public async Task Search_deduplicates_repeated_track_ids()
    {
        var handler = new QueueHandler();
        handler.Enqueue(JsonResponse(200,
            """{"tracks":{"items":[
                {"id":"track-1","name":"Song","artists":[{"name":"Artist"}],"duration_ms":180000},
                {"id":"track-1","name":"Song","artists":[{"name":"Artist"}],"duration_ms":180000},
                {"id":"track-2","name":"Song","artists":[{"name":"Artist"}],"duration_ms":180000}
            ]}}"""));

        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "cid" });
        provider.SetUserTokens("user-token", DateTimeOffset.UtcNow.AddHours(1));

        var tracks = await provider.SearchTrackAsync(new Track { Title = "Song", Artist = "Artist" });

        Assert.Equal(2, tracks.Count);
        Assert.Equal(new[] { "track-1", "track-2" },
            tracks.Select(track => track.ProviderIds["spotify"]));
    }

    [Fact]
    public async Task Add_tracks_retries_explicit_rate_limit_but_not_ambiguous_server_errors()
    {
        var handler = new QueueHandler();
        var limited = JsonResponse(429, """{"error":"rate limited"}""");
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        handler.Enqueue(limited);
        handler.Enqueue(JsonResponse(201, """{"snapshot_id":"snapshot"}"""));

        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "cid" });
        provider.SetUserTokens("user-token", DateTimeOffset.UtcNow.AddHours(1));

        var track = new Track
        {
            Title = "Song",
            ProviderIds = new Dictionary<string, string> { ["spotify"] = "track-1" }
        };
        await provider.AddTracksAsync("playlist-1", [track]);

        Assert.Equal(2, handler.RequestCount);
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private static HttpResponseMessage JsonResponse(int status, string json) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    /// <summary>Sequential mock: returns next enqueued response or throws enqueued exception.</summary>
    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<object> _queue = new();
        public int RequestCount { get; private set; }

        public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);
        public void Enqueue(Exception ex) => _queue.Enqueue(ex);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (_queue.Count == 0)
                throw new InvalidOperationException($"Unexpected HTTP call: {request.Method} {request.RequestUri}");
            var item = _queue.Dequeue();
            if (item is Exception ex)
                throw ex;
            return Task.FromResult((HttpResponseMessage)item);
        }
    }

    /// <summary>Records the last Authorization Bearer token used.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Queue<object> _queue = new();
        public int RequestCount { get; private set; }
        public string? LastBearerToken { get; private set; }

        public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Headers.Authorization is { Scheme: "Bearer" } auth)
                LastBearerToken = auth.Parameter;
            if (_queue.Count == 0)
                throw new InvalidOperationException($"Unexpected HTTP call: {request.Method} {request.RequestUri}");
            var item = _queue.Dequeue();
            if (item is Exception ex)
                throw ex;
            return Task.FromResult((HttpResponseMessage)item);
        }
    }
}
