using System.Net.Http.Headers;
using System.Text.Json;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Spotify;

public sealed partial class SpotifyProvider
{
    public async Task AddTracksAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("AddTracks requires a user OAuth access token (PKCE).");

        var uris = tracks
            .Select(t => t.ProviderIds.TryGetValue("spotify", out var id) ? id : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => $"spotify:track:{id}")
            .ToList();

        for (var i = 0; i < uris.Count; i += 100)
        {
            var chunk = uris.Skip(i).Take(100).ToArray();
            var body = JsonSerializer.Serialize(new { uris = chunk });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/playlists/{playlistId}/items")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Add tracks failed ({(int)resp.StatusCode}): {err}");
            }
        }
    }

    public string GetPlaylistUrl(string playlistId) => $"https://open.spotify.com/playlist/{playlistId}";

    public (string AuthorizeUrl, string CodeVerifier, string State) BeginAuthorization(string redirectUri, IEnumerable<string> scopes)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException("Spotify ClientId is required for PKCE. Set it in settings (not compiled into the binary as a secret).");

        var verifier = SpotifyPkce.GenerateCodeVerifier();
        var challenge = SpotifyPkce.ChallengeS256(verifier);
        var state = SpotifyPkce.GenerateState();
        var url = SpotifyPkce.BuildAuthorizeUrl(_options.ClientId, redirectUri, challenge, state, scopes);
        return (url, verifier, state);
    }

    public async Task CompleteAuthorizationAsync(string authorizationCode, string codeVerifier, string redirectUri, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException("Spotify ClientId is required.");

        var token = await SpotifyPkce.ExchangeCodeAsync(
            _http, _options.ClientId, authorizationCode, codeVerifier, redirectUri, _options.ClientSecret, cancellationToken)
            .ConfigureAwait(false);
        SetUserTokens(token.access_token, DateTimeOffset.UtcNow.AddSeconds(token.expires_in), token.refresh_token);
    }

    private static Track MapTrack(JsonElement t, int position)
    {
        var artists = t.TryGetProperty("artists", out var arr)
            ? string.Join(", ", arr.EnumerateArray().Select(a => a.GetProperty("name").GetString() ?? ""))
            : "";
        var album = t.TryGetProperty("album", out var al) && al.TryGetProperty("name", out var an)
            ? an.GetString() ?? ""
            : "";
        string? isrc = null;
        if (t.TryGetProperty("external_ids", out var ext) && ext.TryGetProperty("isrc", out var isrcEl))
            isrc = isrcEl.GetString();
        var id = t.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        string? url = null;
        if (t.TryGetProperty("external_urls", out var eu) && eu.TryGetProperty("spotify", out var sp))
            url = sp.GetString();

        return new Track
        {
            Title = t.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Artist = artists,
            Album = album,
            DurationMs = t.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null,
            Isrc = isrc,
            SourceUrl = url,
            Position = position,
            ProviderIds = string.IsNullOrEmpty(id) ? new() : new Dictionary<string, string> { ["spotify"] = id }
        };
    }
}
