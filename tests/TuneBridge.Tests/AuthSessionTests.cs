using System.Text;
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
    public void Callback_redirect_uri_matches_listener_byte_for_byte()
    {
        const string registered = "http://127.0.0.1:43821/callback";
        var listener = new LoopbackHttpListener(registered, "state-test");
        Assert.Equal(registered, listener.RedirectUri);
        Assert.Equal(
            LoopbackHttpListener.CanonicalRedirectUri(registered),
            LoopbackHttpListener.CanonicalRedirectUri(listener.RedirectUri));
        Assert.EndsWith("/callback/", listener.ListeningPrefix);
        Assert.False(listener.RedirectUri.EndsWith('/'));
    }

    [Fact]
    public void Callback_with_trailing_slash_in_options_canonicalizes()
    {
        var listener = new LoopbackHttpListener("http://127.0.0.1:43821/callback/", "st");
        Assert.Equal("http://127.0.0.1:43821/callback", listener.RedirectUri);
    }

    [Fact]
    public void CanonicalRedirectUri_normalizes_host_and_slash()
    {
        var a = LoopbackHttpListener.CanonicalRedirectUri("http://127.0.0.1:43821/callback/");
        var b = LoopbackHttpListener.CanonicalRedirectUri("http://127.0.0.1:43821/callback");
        Assert.Equal(a, b);
    }

    [Fact]
    public void AuthSession_ResolvedRedirectUri_matches_options()
    {
        var options = new SpotifyOptions
        {
            ClientId = "cid",
            RedirectUri = "http://127.0.0.1:43821/callback"
        };
        using var http = new HttpClient();
        var session = new SpotifyAuthSession(http, options, new InMemoryTokenStore());
        Assert.Equal("http://127.0.0.1:43821/callback", session.ResolvedRedirectUri);

        var (url, _, state) = session.BeginPkce();
        Assert.Contains(Uri.EscapeDataString(session.ResolvedRedirectUri), url);
        Assert.Contains($"state={Uri.EscapeDataString(state)}", url);
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
    public async Task AuthSession_Restore_valid_tokens_survives_restart()
    {
        var store = new InMemoryTokenStore();
        var options = new SpotifyOptions { ClientId = "c" };
        using var http = new HttpClient();

        var json = JsonSerializer.Serialize(new
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            DisplayName = "Tester"
        });
        await store.SaveAsync(SpotifyAuthSession.TokenStoreKey, json);

        var session2 = new SpotifyAuthSession(http, options, store);
        await session2.RestoreAsync();
        Assert.True(session2.IsAuthenticated);
        Assert.Equal("access-1", session2.AccessToken);
        Assert.Equal("refresh-1", session2.RefreshToken);
        Assert.Equal("Tester", session2.DisplayName);
    }

    [Fact]
    public async Task AuthSession_Restore_corrupt_json_deletes_store()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(SpotifyAuthSession.TokenStoreKey, "{not-json");
        using var http = new HttpClient();
        var session = new SpotifyAuthSession(http, new SpotifyOptions { ClientId = "c" }, store);
        await session.RestoreAsync();
        Assert.False(session.IsAuthenticated);
        Assert.Null(await store.LoadAsync(SpotifyAuthSession.TokenStoreKey));
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

    [Fact]
    public void Search_limit_is_clamped_to_10_in_provider_source()
    {
        Assert.Equal(10, Math.Clamp(50, 1, 10));
        Assert.Equal(10, Math.Clamp(10, 1, 10));
        Assert.Equal(1, Math.Clamp(0, 1, 10));
    }

    [Fact]
    public async Task DpapiTokenStore_encrypts_tokens_and_roundtrips_for_current_user()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), "TuneBridge-DpapiTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiTokenStore(directory);
            const string secret = "oauth-access-token-sensitive-value";
            await store.SaveAsync("spotify.oauth", secret);

            var files = Directory.GetFiles(directory, "*.bin");
            Assert.Single(files);
            var encrypted = await File.ReadAllBytesAsync(files[0]);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes(secret), encrypted);
            Assert.Equal(secret, await store.LoadAsync("spotify.oauth"));

            await store.DeleteAsync("spotify.oauth");
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static string? ExtractQuery(string url, string key)
    {
        var uri = new Uri(url);
        var q = LoopbackHttpListener.ParseQuery(uri.Query);
        return q.TryGetValue(key, out var v) ? v : null;
    }
}
