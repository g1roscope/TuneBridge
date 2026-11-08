using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using TuneBridge.Core.Abstractions;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Spotify;

public sealed partial class SpotifyProvider : IMusicProvider, IOauthPkceFlow
{
    public const string ApiBase = "https://api.spotify.com/v1";

    private readonly HttpClient _http;
    private readonly SpotifyOptions _options;

    // User OAuth (PKCE) — never overwritten by client_credentials
    private string? _userAccessToken;
    private DateTimeOffset _userExpires = DateTimeOffset.MinValue;
    private string? _refreshToken;

    // Client Credentials app token — separate from user session
    private string? _appAccessToken;
    private DateTimeOffset _appExpires = DateTimeOffset.MinValue;

    public SpotifyProvider(HttpClient http, SpotifyOptions options)
    {
        _http = http;
        _options = options;
    }

    public string ProviderId => "spotify";
    public string DisplayName => "Spotify";
    public bool IsExperimental => false;
    string IOauthPkceFlow.ProviderId => ProviderId;

    /// <summary>True when a non-expired user OAuth access token is present.</summary>
    public bool HasValidUserToken =>
        !string.IsNullOrEmpty(_userAccessToken) && DateTimeOffset.UtcNow < _userExpires.AddMinutes(-1);

    /// <summary>True when a non-expired client_credentials token is present.</summary>
    public bool HasValidAppToken =>
        !string.IsNullOrEmpty(_appAccessToken) && DateTimeOffset.UtcNow < _appExpires.AddMinutes(-1);

    /// <summary>Prefer user token; fall back to app token. Does not fetch.</summary>
    public string? EffectiveAccessToken
    {
        get
        {
            if (HasValidUserToken)
                return _userAccessToken;
            if (HasValidAppToken)
                return _appAccessToken;
            // Return expired user token if still set (caller may refresh via AuthSession)
            return _userAccessToken ?? _appAccessToken;
        }
    }

    /// <summary>User access token (for tests / diagnostics; null if none).</summary>
    public string? UserAccessTokenForTests => _userAccessToken;

    /// <summary>App (client_credentials) access token (for tests / diagnostics; null if none).</summary>
    public string? AppAccessTokenForTests => _appAccessToken;

    public ProviderCapabilities GetCapabilities()
    {
        var hasUserToken = !string.IsNullOrWhiteSpace(_userAccessToken);
        var hasAppCreds = !string.IsNullOrWhiteSpace(_options.ClientId)
                          && !string.IsNullOrWhiteSpace(_options.ClientSecret);
        return new ProviderCapabilities
        {
            CanParseUrl = true,
            CanReadPublic = hasUserToken,
            CanReadPrivate = hasUserToken,
            CanSearch = hasUserToken || hasAppCreds || HasValidAppToken,
            CanCreatePlaylist = hasUserToken,
            CanAddTracks = hasUserToken,
            SupportsIsrc = true,
            RequiresOAuth = true,
            IsExperimental = false,
            Notes = hasUserToken
                ? "Signed in. Playlist items only for playlists you own or collaborate on (API 2026)."
                : "Sign in with Spotify (PKCE). ClientId via TUNEBRIDGE_SPOTIFY_CLIENT_ID. No Client Secret required."
        };
    }

    /// <summary>
    /// Apply user OAuth tokens. Does not touch the client_credentials app token slot.
    /// </summary>
    public void SetUserTokens(string accessToken, DateTimeOffset expiresAt, string? refreshToken = null)
    {
        _userAccessToken = accessToken;
        _userExpires = expiresAt;
        if (refreshToken is not null)
            _refreshToken = refreshToken;
    }

    /// <summary>Clear only user OAuth tokens (app token remains if any).</summary>
    public void ClearUserTokens()
    {
        _userAccessToken = null;
        _userExpires = DateTimeOffset.MinValue;
        _refreshToken = null;
    }

    public ParsedPlaylistRef? ParsePlaylistUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var m = Regex.Match(url, @"(?:open\.spotify\.com/playlist/|spotify:playlist:)([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        return new ParsedPlaylistRef
        {
            ProviderId = ProviderId,
            PlaylistId = m.Groups[1].Value,
            Url = url,
            IsPublic = true
        };
    }

    /// <summary>
    /// Ensure a usable access token is available for API calls.
    /// Prefer existing valid user token; only fetch client_credentials into the app slot.
    /// Never overwrites a user OAuth token with an app token.
    /// </summary>
    public async Task EnsureAppTokenAsync(CancellationToken cancellationToken = default)
    {
        // User session takes precedence — do not replace with client_credentials
        if (HasValidUserToken)
            return;

        if (HasValidAppToken)
            return;

        if (!string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            var token = await SpotifyPkce.ClientCredentialsAsync(
                _http, _options.ClientId, _options.ClientSecret, cancellationToken).ConfigureAwait(false);
            _appAccessToken = token.access_token;
            _appExpires = DateTimeOffset.UtcNow.AddSeconds(token.expires_in);
            return;
        }

        // No credentials and no user token
        if (string.IsNullOrEmpty(_userAccessToken) && string.IsNullOrEmpty(_appAccessToken))
        {
            throw new InvalidOperationException(
                "No Spotify access token. Sign in with Spotify (PKCE), or set ClientId+ClientSecret for limited client_credentials. " +
                "Note: since Feb 2026, playlist *items* are only returned for playlists you own or collaborate on.");
        }
    }

    private string RequireAccessToken()
    {
        var t = EffectiveAccessToken;
        if (string.IsNullOrEmpty(t))
            throw new InvalidOperationException(
                "No Spotify access token. Call EnsureAppTokenAsync or complete PKCE first.");
        return t;
    }

    /// <summary>
    /// Write operations (create playlist, add tracks) must use a non-expired user OAuth token.
    /// Client credentials / app token is never acceptable for these calls.
    /// </summary>
    private string RequireUserAccessToken()
    {
        if (!HasValidUserToken || string.IsNullOrEmpty(_userAccessToken))
            throw new InvalidOperationException(
                "This operation requires a valid user OAuth access token (PKCE). " +
                "Client Credentials is not sufficient. Sign in with Spotify and try again.");
        return _userAccessToken;
    }
}
