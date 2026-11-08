using System.Net.Http.Headers;
using System.Text.Json;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Spotify;

public sealed partial class SpotifyProvider
{
    public async Task EnsureAppTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _accessTokenExpires.AddMinutes(-1))
            return;

        if (string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new InvalidOperationException(
                "Spotify ClientId and ClientSecret are required for client_credentials (public playlist/search). " +
                "Provide them via environment or settings — they are not embedded in the binary.");

        var token = await SpotifyPkce.ClientCredentialsAsync(
            _http, _options.ClientId, _options.ClientSecret, cancellationToken).ConfigureAwait(false);
        _accessToken = token.access_token;
        _accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(token.expires_in);
    }

    public async Task<Playlist> GetPublicPlaylistAsync(ParsedPlaylistRef reference, CancellationToken cancellationToken = default)
    {
        await EnsureAppTokenAsync(cancellationToken).ConfigureAwait(false);
        return await GetPlaylistWithTokenAsync(reference.PlaylistId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Playlist> GetPlaylistWithTokenAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("No Spotify access token. Call EnsureAppTokenAsync or complete PKCE first.");

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/playlists/{playlistId}?fields=id,name,description,external_urls,tracks.items(track(id,name,artists,album,duration_ms,external_ids,external_urls,is_local)),tracks.next");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spotify playlist fetch failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tracks = new List<Track>();
        var pos = 0;
        if (root.TryGetProperty("tracks", out var tracksNode) && tracksNode.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("track", out var t) || t.ValueKind == JsonValueKind.Null)
                    continue;
                if (t.TryGetProperty("is_local", out var local) && local.GetBoolean())
                    continue;
                tracks.Add(MapTrack(t, pos++));
            }
        }

        var next = tracksNode.TryGetProperty("next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String
            ? nextEl.GetString()
            : null;
        while (!string.IsNullOrEmpty(next))
        {
            using var pageReq = new HttpRequestMessage(HttpMethod.Get, next);
            pageReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            using var pageResp = await _http.SendAsync(pageReq, cancellationToken).ConfigureAwait(false);
            var pageJson = await pageResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            pageResp.EnsureSuccessStatusCode();
            using var pageDoc = JsonDocument.Parse(pageJson);
            foreach (var item in pageDoc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (!item.TryGetProperty("track", out var t) || t.ValueKind == JsonValueKind.Null)
                    continue;
                tracks.Add(MapTrack(t, pos++));
            }
            next = pageDoc.RootElement.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
        }

        return new Playlist
        {
            Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? "Spotify playlist" : "Spotify playlist",
            Description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
            Tracks = tracks,
            SourceService = ProviderId,
            SourceUrl = root.TryGetProperty("external_urls", out var eu) && eu.TryGetProperty("spotify", out var sp)
                ? sp.GetString()
                : null,
            ExternalId = playlistId
        };
    }
}
