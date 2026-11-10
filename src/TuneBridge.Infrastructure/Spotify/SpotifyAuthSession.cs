using System.Diagnostics;
using System.Text.Json;
using TuneBridge.Core.Abstractions;
using TuneBridge.Infrastructure.Security;
using TuneBridge.Infrastructure.Diagnostics;

namespace TuneBridge.Infrastructure.Spotify;

/// <summary>
/// Orchestrates Authorization Code + PKCE with loopback, refresh, and secure token persistence.
/// Does not embed Client Secret; public PKCE client only.
/// </summary>
public sealed class SpotifyAuthSession : IAuthSession
{
    public const string TokenStoreKey = "spotify.oauth";
    public const int DefaultLoopbackPort = 43821;

    private readonly HttpClient _http;
    private readonly SpotifyOptions _options;
    private readonly ITokenStore _tokenStore;
    private readonly int _loopbackPort;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private string? _displayName;

    public string ProviderId => "spotify";
    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _expiresAt;
    public DateTimeOffset? AccessTokenExpiresAt => string.IsNullOrEmpty(_accessToken) ? null : _expiresAt;
    public string? DisplayName => _displayName;
    public string? AccessToken => _accessToken;
    public string? RefreshToken => _refreshToken;

    public SpotifyAuthSession(HttpClient http, SpotifyOptions options, ITokenStore tokenStore, int loopbackPort = DefaultLoopbackPort)
    {
        _http = http;
        _options = options;
        _tokenStore = tokenStore;
        _loopbackPort = loopbackPort;
    }

    /// <summary>
    /// Resolved redirect URI used for authorize + token exchange.
    /// Prefer options.RedirectUri when set; otherwise default loopback without trailing slash.
    /// </summary>
    public string ResolvedRedirectUri =>
        string.IsNullOrWhiteSpace(_options.RedirectUri)
            ? $"http://127.0.0.1:{_loopbackPort}/callback"
            : _options.RedirectUri.TrimEnd('/');

    /// <summary>
    /// Load tokens from store. Network errors during optional refresh do NOT wipe the store.
    /// Only delete store when JSON is corrupt / unreadable.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        var json = await _tokenStore.LoadAsync(TokenStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
            return;

        StoredTokens? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredTokens>(json);
        }
        catch (JsonException)
        {
            // Confirmed corrupt — remove
            await _tokenStore.DeleteAsync(TokenStoreKey, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (stored is null || string.IsNullOrEmpty(stored.AccessToken))
            return;

        _accessToken = stored.AccessToken;
        _refreshToken = stored.RefreshToken;
        _expiresAt = stored.ExpiresAt;
        _displayName = stored.DisplayName;

        if (!string.IsNullOrEmpty(_refreshToken) && DateTimeOffset.UtcNow >= _expiresAt.AddMinutes(-5))
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // Temporary network failure — keep stored tokens; caller may retry later
            }
            catch (InvalidOperationException)
            {
                // Token rejected by Spotify — keep for user to reconnect; do not wipe silently
            }
        }
    }

    /// <summary>
    /// Full browser PKCE login: start loopback → open system browser → exchange code → persist.
    /// Redirect URI sent to Spotify matches listener RedirectUri byte-for-byte (no trailing slash mismatch).
    /// </summary>
    public async Task LoginInteractiveAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException(
                "Spotify ClientId is required. Set TUNEBRIDGE_SPOTIFY_CLIENT_ID (public client — no secret needed for PKCE).");

        var timeoutValue = timeout ?? TimeSpan.FromMinutes(5);
        var redirectUri = ResolvedRedirectUri;

        var verifier = SpotifyPkce.GenerateCodeVerifier();
        var challenge = SpotifyPkce.ChallengeS256(verifier);
        var state = SpotifyPkce.GenerateState();
        var authorizeUrl = SpotifyPkce.BuildAuthorizeUrl(
            _options.ClientId!,
            redirectUri,
            challenge,
            state,
            _options.DefaultScopes);

        await using var listener = new LoopbackHttpListener(redirectUri, state);

        // Sanity: listener URI must match what we send to Spotify
        if (!string.Equals(
                LoopbackHttpListener.CanonicalRedirectUri(listener.RedirectUri),
                LoopbackHttpListener.CanonicalRedirectUri(redirectUri),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Redirect URI mismatch: authorize uses '{redirectUri}', listener is '{listener.RedirectUri}'.");
        }

        listener.Start();
        OpenSystemBrowser(authorizeUrl);

        string code;
        try
        {
            code = await listener.WaitForCodeAsync(timeoutValue, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        var token = await SpotifyPkce.ExchangeCodeAsync(
            _http,
            _options.ClientId!,
            code,
            verifier,
            redirectUri, // must match authorize redirect_uri exactly
            clientSecret: null,
            cancellationToken).ConfigureAwait(false);

        ApplyTokenResponse(token);
        await TryLoadProfileAsync(cancellationToken).ConfigureAwait(false);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public (string AuthorizeUrl, string CodeVerifier, string State) BeginPkce()
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException("Spotify ClientId is required for PKCE.");

        var redirect = ResolvedRedirectUri;
        var verifier = SpotifyPkce.GenerateCodeVerifier();
        var challenge = SpotifyPkce.ChallengeS256(verifier);
        var state = SpotifyPkce.GenerateState();
        var url = SpotifyPkce.BuildAuthorizeUrl(_options.ClientId, redirect, challenge, state, _options.DefaultScopes);
        return (url, verifier, state);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_refreshToken))
            throw new InvalidOperationException("No refresh token. Sign in with Spotify again.");
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException("Spotify ClientId is required to refresh tokens.");

        var previousRefresh = _refreshToken;
        var token = await SpotifyPkce.RefreshTokenAsync(
            _http, _options.ClientId!, _refreshToken, cancellationToken).ConfigureAwait(false);

        // Spotify often omits refresh_token on refresh — keep the old one
        if (string.IsNullOrEmpty(token.refresh_token))
            token.refresh_token = previousRefresh;

        ApplyTokenResponse(token);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ensure a valid access token, refreshing if needed.</summary>
    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsAuthenticated)
            return _accessToken!;

        if (!string.IsNullOrEmpty(_refreshToken))
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (IsAuthenticated)
                return _accessToken!;
        }

        throw new InvalidOperationException("Not signed in to Spotify. Use Connect Spotify first.");
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = DateTimeOffset.MinValue;
        _displayName = null;
        await _tokenStore.DeleteAsync(TokenStoreKey, cancellationToken).ConfigureAwait(false);
    }

    public void ApplyToProvider(SpotifyProvider provider)
    {
        if (!string.IsNullOrEmpty(_accessToken))
            provider.SetUserTokens(_accessToken, _expiresAt, _refreshToken);
    }

    private void ApplyTokenResponse(SpotifyTokenResponse token)
    {
        _accessToken = token.access_token;
        if (!string.IsNullOrEmpty(token.refresh_token))
            _refreshToken = token.refresh_token;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.expires_in));
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var stored = new StoredTokens
        {
            AccessToken = _accessToken ?? "",
            RefreshToken = _refreshToken,
            ExpiresAt = _expiresAt,
            DisplayName = _displayName
        };
        var json = JsonSerializer.Serialize(stored);
        await _tokenStore.SaveAsync(TokenStoreKey, json, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryLoadProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return;
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("display_name", out var n))
                _displayName = n.GetString();
            else if (doc.RootElement.TryGetProperty("id", out var id))
                _displayName = id.GetString();
        }
        catch (Exception ex)
        {
            // Profile metadata is optional; retain the authenticated session but keep a local diagnostic.
            DiagnosticsLogger.Write(ex, "spotify.profile.lookup");
        }
    }

    private static void OpenSystemBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not open system browser. Open this URL manually: {url}", ex);
        }
    }

    private sealed class StoredTokens
    {
        public string AccessToken { get; set; } = "";
        public string? RefreshToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string? DisplayName { get; set; }
    }
}
