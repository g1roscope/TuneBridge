using System.Text.Json;
using TuneBridge.Infrastructure.Security;
using TuneBridge.Infrastructure.Spotify;

namespace TuneBridge.Tests;

public class AuthSessionTests
{
    [Fact]
    public void Loopback_ParseQuery_handles_code_and_state()
    {
        var q = LoopbackHttpListener.ParseQuery("?code=abc%20123&state=xyz&extra=1");
        Assert.Equal("abc 123", q["code"]);
        Assert.Equal("xyz", q["state"]);
        Assert.Equal("1", q["extra"]);
    }

    [Fact]
    public void Loopback_ParseQuery_empty()
    {
        Assert.Empty(LoopbackHttpListener.ParseQuery(null));
        Assert.Empty(LoopbackHttpListener.ParseQuery(""));
        Assert.Empty(LoopbackHttpListener.ParseQuery("?"));
    }

    [Fact]
    public void Pkce_state_is_unique()
    {
        var a = SpotifyPkce.GenerateState();
        var b = SpotifyPkce.GenerateState();
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
    }

    [Fact]
    public void BeginPkce_returns_url_with_state_and_challenge()
    {
        var options = new SpotifyOptions { ClientId = "test-client", RedirectUri = "http://127.0.0.1:43821/callback" };
        using var http = new HttpClient();
        var session = new SpotifyAuthSession(http, options, new InMemoryTokenStore());
        var (url, verifier, state) = session.BeginPkce();
        Assert.Contains("client_id=test-client", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains($"state={Uri.EscapeDataString(state)}", url);
        Assert.False(string.IsNullOrWhiteSpace(verifier));
        Assert.Equal(SpotifyPkce.ChallengeS256(verifier), ExtractQuery(url, "code_challenge"));
    }

    [Fact]
    public async Task TokenStore_roundtrip_inmemory()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync("k", "secret-value");
        Assert.Equal("secret-value", await store.LoadAsync("k"));
        await store.DeleteAsync("k");
        Assert.Null(await store.LoadAsync("k"));
    }

    [Fact]
    public async Task AuthSession_Restore_empty_store_is_noop()
    {
        var options = new SpotifyOptions { ClientId = "c" };
        using var http = new HttpClient();
        var session = new SpotifyAuthSession(http, options, new InMemoryTokenStore());
        await session.RestoreAsync();
        Assert.False(session.IsAuthenticated);
        Assert.Null(session.AccessToken);
    }

    [Fact]
    public async Task AuthSession_Logout_clears_store()
    {
        var store = new InMemoryTokenStore();
        var options = new SpotifyOptions { ClientId = "c" };
        using var http = new HttpClient();
        var session = new SpotifyAuthSession(http, options, store);

        var json = JsonSerializer.Serialize(new
        {
            AccessToken = "at",
            RefreshToken = "rt",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            DisplayName = "User"
        });
        await store.SaveAsync(SpotifyAuthSession.TokenStoreKey, json);
        await session.RestoreAsync();
        Assert.True(session.IsAuthenticated);

        await session.LogoutAsync();
        Assert.False(session.IsAuthenticated);
        Assert.Null(await store.LoadAsync(SpotifyAuthSession.TokenStoreKey));
    }

    [Fact]
    public void Authorize_url_does_not_include_client_secret()
    {
        var url = SpotifyPkce.BuildAuthorizeUrl(
            "cid", "http://127.0.0.1:43821/callback", "ch", "st",
            ["playlist-read-private"]);
        Assert.DoesNotContain("client_secret", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("response_type=code", url);
    }

    private static string? ExtractQuery(string url, string key)
    {
        var uri = new Uri(url);
        var q = LoopbackHttpListener.ParseQuery(uri.Query);
        return q.TryGetValue(key, out var v) ? v : null;
    }
}
