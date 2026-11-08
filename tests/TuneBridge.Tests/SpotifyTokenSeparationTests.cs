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
}
