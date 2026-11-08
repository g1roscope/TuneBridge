using System.Net.Http.Headers;
using System.Text.Json;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Spotify;

public sealed partial class SpotifyProvider
{
    public async Task<Playlist> GetPublicPlaylistAsync(ParsedPlaylistRef reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(EffectiveAccessToken) ||
            (!HasValidUserToken && !HasValidAppToken))
            await EnsureAppTokenAsync(cancellationToken).ConfigureAwait(false);
        return await GetPlaylistWithTokenAsync(reference.PlaylistId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Playlist> GetPlaylistWithTokenAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        var accessToken = RequireAccessToken();

        using var metaReq = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/playlists/{playlistId}?fields=id,name,description,external_urls,owner(id,display_name)");
        metaReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var metaResp = await _http.SendAsync(metaReq, cancellationToken).ConfigureAwait(false);
        var metaJson = await metaResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!metaResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spotify playlist metadata failed ({(int)metaResp.StatusCode}): {metaJson}");

        using var metaDoc = JsonDocument.Parse(metaJson);
        var root = metaDoc.RootElement;
        var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "Spotify playlist" : "Spotify playlist";
        var description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
        string? sourceUrl = null;
        if (root.TryGetProperty("external_urls", out var eu) && eu.TryGetProperty("spotify", out var sp))
            sourceUrl = sp.GetString();

        var tracks = new List<Track>();
        var pos = 0;
        // API 2026: GET /playlists/{id}/items — only for owned/collaborative playlists
        string? next = $"{ApiBase}/playlists/{playlistId}/items?limit=50&fields=items(item(id,name,artists,album,duration_ms,external_ids,external_urls,is_local,type)),next";

        while (!string.IsNullOrEmpty(next))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var pageReq = new HttpRequestMessage(HttpMethod.Get, next);
            pageReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var pageResp = await _http.SendAsync(pageReq, cancellationToken).ConfigureAwait(false);
            var pageJson = await pageResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (pageResp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                pageResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    "Cannot read playlist items. Since Spotify API February 2026, full track lists are only available " +
                    "for playlists you own or collaborate on. Sign in with the owner account, or ask to be added as a collaborator. " +
                    $"HTTP {(int)pageResp.StatusCode}: {pageJson}");
            }

            if (!pageResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Spotify playlist items failed ({(int)pageResp.StatusCode}): {pageJson}");

            using var pageDoc = JsonDocument.Parse(pageJson);
            var pageRoot = pageDoc.RootElement;
            if (pageRoot.TryGetProperty("items", out var items))
            {
                foreach (var row in items.EnumerateArray())
                {
                    JsonElement media;
                    if (row.TryGetProperty("item", out var itemEl) && itemEl.ValueKind == JsonValueKind.Object)
                        media = itemEl;
                    else if (row.TryGetProperty("track", out var trackEl) && trackEl.ValueKind == JsonValueKind.Object)
                        media = trackEl; // legacy fallback
                    else
                        continue;

                    if (media.TryGetProperty("type", out var typeEl) &&
                        typeEl.GetString() is string type &&
                        !string.Equals(type, "track", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (media.TryGetProperty("is_local", out var local) && local.ValueKind == JsonValueKind.True)
                        continue;

                    tracks.Add(MapTrack(media, pos++));
                }
            }

            next = pageRoot.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
        }

        return new Playlist
        {
            Name = name,
            Description = description,
            Tracks = tracks,
            SourceService = ProviderId,
            SourceUrl = sourceUrl,
            ExternalId = playlistId
        };
    }
}
