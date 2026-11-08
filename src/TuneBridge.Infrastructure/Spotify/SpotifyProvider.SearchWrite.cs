using System.Net.Http.Headers;
using System.Text.Json;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Spotify;

public sealed partial class SpotifyProvider
{
    public async Task<IReadOnlyList<Track>> SearchTrackAsync(Track track, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(EffectiveAccessToken) ||
            (!HasValidUserToken && !HasValidAppToken))
            await EnsureAppTokenAsync(cancellationToken).ConfigureAwait(false);

        var accessToken = RequireAccessToken();

        // API 2026: search limit maximum is 10 (was 50)
        limit = Math.Clamp(limit, 1, 10);

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

        var url = $"{ApiBase}/search?type=track&limit={limit}&q={Uri.EscapeDataString(q)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
        // Strict: non-expired user OAuth only — never fall back to client_credentials
        _ = RequireUserAccessToken();
        return CreatePlaylistCoreAsync(name, description, isPublic, cancellationToken);
    }

    private async Task<string> CreatePlaylistCoreAsync(string name, string description, bool isPublic, CancellationToken cancellationToken)
    {
        var accessToken = RequireUserAccessToken();
        // API 2026 preferred: POST /me/playlists
        var body = JsonSerializer.Serialize(new { name, description, @public = isPublic });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/me/playlists")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Create playlist failed ({(int)resp.StatusCode}): {json}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()!;
    }
}
