using System.Net.Http.Headers;
using System.Text.Json;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Spotify;

public sealed partial class SpotifyProvider
{
    public async Task<IReadOnlyList<Track>> SearchTrackAsync(Track track, int limit = 10, CancellationToken cancellationToken = default)
    {
        await EnsureAppTokenAsync(cancellationToken).ConfigureAwait(false);

        string q;
        if (!string.IsNullOrWhiteSpace(track.Isrc))
            q = $"isrc:{track.Isrc.Trim()}";
        else
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(track.Title))
                parts.Add($"track:\"{track.Title.Replace("\"", "")}\"");
            if (!string.IsNullOrWhiteSpace(track.Artist))
                parts.Add($"artist:\"{track.Artist.Replace("\"", "")}\"");
            q = parts.Count > 0 ? string.Join(' ', parts) : track.Title;
        }

        var url = $"{ApiBase}/search?type=track&limit={Math.Clamp(limit, 1, 50)}&q={Uri.EscapeDataString(q)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spotify search failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var list = new List<Track>();
        if (doc.RootElement.TryGetProperty("tracks", out var tr) && tr.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var t in items.EnumerateArray())
                list.Add(MapTrack(t, i++));
        }
        return list;
    }

    public Task<string> CreatePlaylistAsync(string name, string description = "", bool isPublic = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("CreatePlaylist requires a user OAuth access token (PKCE). Client Credentials is not sufficient.");

        return CreatePlaylistCoreAsync(name, description, isPublic, cancellationToken);
    }

    private bool IsLikelyUserToken() => !string.IsNullOrEmpty(_refreshToken);

    private async Task<string> CreatePlaylistCoreAsync(string name, string description, bool isPublic, CancellationToken cancellationToken)
    {
        using var meReq = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/me");
        meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var meResp = await _http.SendAsync(meReq, cancellationToken).ConfigureAwait(false);
        var meJson = await meResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!meResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spotify /me failed — user token required ({(int)meResp.StatusCode}): {meJson}");
        using var meDoc = JsonDocument.Parse(meJson);
        var userId = meDoc.RootElement.GetProperty("id").GetString()!;

        var body = JsonSerializer.Serialize(new { name, description, @public = isPublic });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/users/{userId}/playlists")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Create playlist failed ({(int)resp.StatusCode}): {json}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()!;
    }
}
