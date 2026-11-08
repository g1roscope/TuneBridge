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
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires = DateTimeOffset.MinValue;
    private string? _refreshToken;

    public SpotifyProvider(HttpClient http, SpotifyOptions options)
    {
        _http = http;
        _options = options;
    }

    public string ProviderId => "spotify";
    public string DisplayName => "Spotify";
    public bool IsExperimental => false;
    string IOauthPkceFlow.ProviderId => ProviderId;

    public ProviderCapabilities GetCapabilities()
    {
        var hasAppCreds = !string.IsNullOrWhiteSpace(_options.ClientId)
                          && !string.IsNullOrWhiteSpace(_options.ClientSecret);
        var hasUserToken = !string.IsNullOrWhiteSpace(_accessToken);
        return new ProviderCapabilities
        {
            CanParseUrl = true,
            CanReadPublic = hasAppCreds || hasUserToken,
            CanReadPrivate = hasUserToken,
            CanSearch = hasAppCreds || hasUserToken,
            CanCreatePlaylist = hasUserToken,
            CanAddTracks = hasUserToken,
            SupportsIsrc = true,
            RequiresOAuth = true,
            IsExperimental = false,
            Notes = hasAppCreds || hasUserToken
                ? "Configured for API calls."
                : "Set ClientId (+ ClientSecret for client_credentials, or complete PKCE for user token)."
        };
    }

    public void SetUserTokens(string accessToken, DateTimeOffset expiresAt, string? refreshToken = null)
    {
        _accessToken = accessToken;
        _accessTokenExpires = expiresAt;
        _refreshToken = refreshToken;
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
}
