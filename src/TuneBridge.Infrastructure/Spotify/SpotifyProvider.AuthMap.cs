using System.Net.Http.Headers;
using System.Text.Json;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Spotify;

public sealed partial class SpotifyProvider
{
    public async Task AddTracksAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        // Strict: non-expired user OAuth only — never fall back to client_credentials
        var accessToken = RequireUserAccessToken();

        ArgumentNullException.ThrowIfNull(tracks);
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("A destination playlist ID is required.", nameof(playlistId));
        if (tracks.Count == 0)
            return;

        // Validate the entire batch before the first write. Silently filtering a track
        // without a Spotify ID would make the UI report a successful but incomplete transfer.
        var uris = new List<string>(tracks.Count);
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index]
                ?? throw new InvalidOperationException($"Track {index + 1} is null; transfer was not started.");

            if (track.ProviderIds is null || !track.ProviderIds.TryGetValue("spotify", out var id) || string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(
                    $"Track {index + 1} ('{track.Title}') has no Spotify track ID. Transfer was not started.");

            uris.Add($"spotify:track:{id}");
        }

        var confirmedTracksAdded = 0;
        for (var i = 0; i < uris.Count; i += 100)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (confirmedTracksAdded > 0)
                    throw new SpotifyPartialTransferException(
                        confirmedTracksAdded,
                        $"Transfer cancelled after {confirmedTracksAdded} tracks were confirmed in Spotify.");
                cancellationToken.ThrowIfCancellationRequested();
            }

            var chunk = uris.Skip(i).Take(100).ToArray();
            var body = JsonSerializer.Serialize(new { uris = chunk });
            HttpResponseMessage response;
            try
            {
                response = await SendWithRetryAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/playlists/{playlistId}/items")
                    {
                        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    return request;
                }, cancellationToken, retryServerErrors: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (confirmedTracksAdded > 0)
            {
                throw new SpotifyPartialTransferException(
                    confirmedTracksAdded,
                    $"Transfer was interrupted after {confirmedTracksAdded} confirmed tracks. The current batch outcome may be unknown; check the Spotify playlist before retrying.",
                    ex,
                    currentBatchOutcomeUnknown: true);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (confirmedTracksAdded > 0)
                        throw new SpotifyPartialTransferException(
                            confirmedTracksAdded,
                            $"Spotify accepted {confirmedTracksAdded} tracks, then rejected the next batch ({(int)response.StatusCode}): {err}");
                    throw new InvalidOperationException($"Add tracks failed ({(int)response.StatusCode}): {err}");
                }
            }

            confirmedTracksAdded += chunk.Length;
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
