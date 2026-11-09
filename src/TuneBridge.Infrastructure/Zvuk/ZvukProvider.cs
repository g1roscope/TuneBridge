using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TuneBridge.Core.Abstractions;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Zvuk;

/// <summary>
/// Experimental read/search integration for Zvuk's currently undocumented GraphQL API.
/// Uses an anonymous token by default. Playlist creation and track insertion are not supported.
/// </summary>
public sealed class ZvukProvider : IMusicProvider
{
    private const string ProfileUrl = "https://zvuk.com/api/tiny/profile";
    private const string GraphQlUrl = "https://zvuk.com/api/v1/graphql";
    private const string SearchQuery = """
        query getSearch($query: String, $limit: Int, $searchSessionId: String) {
          quickSearch(query: $query, limit: $limit, searchSessionId: $searchSessionId) {
            content {
              __typename
              ... on Track {
                id
                title
                duration
                artistNames
                artistTemplate
                release { title }
              }
            }
          }
        }
        """;
    private const string PlaylistQuery = """
        query getPlaylists($ids: [ID!]!) {
          playlists(ids: $ids) {
            id
            title
            description
            isPublic
            tracks {
              id
              title
              duration
              position
              artistTemplate
              artists { id title }
              release { id title }
            }
          }
        }
        """;

    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _anonymousToken;

    public ZvukProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public string ProviderId => "zvuk";
    public string DisplayName => "Звук";
    public bool IsExperimental => true;

    public ProviderCapabilities GetCapabilities() => new()
    {
        CanParseUrl = true,
        CanReadPublic = true,
        CanReadPrivate = false,
        CanSearch = true,
        CanCreatePlaylist = false,
        CanAddTracks = false,
        SupportsIsrc = false,
        RequiresOAuth = false,
        IsExperimental = true,
        Notes =
            "Experimental read/search only via Zvuk's undocumented GraphQL API. " +
            "Uses an anonymous token; playlist creation and adding tracks are not implemented. " +
            "The service may change this API without notice."
    };

    public ParsedPlaylistRef? ParsePlaylistUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;

        var host = uri.IdnHost;
        if (!host.Equals("zvuk.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("www.zvuk.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("sber-zvuk.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("www.sber-zvuk.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var playlistIndex = Array.FindIndex(segments, s =>
            s.Equals("playlist", StringComparison.OrdinalIgnoreCase));
        if (playlistIndex < 0 || playlistIndex == segments.Length - 1)
            return null;

        var id = Uri.UnescapeDataString(segments[^1]);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            return null;

        return new ParsedPlaylistRef
        {
            ProviderId = ProviderId,
            PlaylistId = id,
            Url = uri.GetLeftPart(UriPartial.Path),
            IsPublic = true
        };
    }

    public async Task<Playlist> GetPublicPlaylistAsync(
        ParsedPlaylistRef reference,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(reference.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Playlist reference does not belong to Zvuk.", nameof(reference));

        using var document = await ExecuteGraphQlAsync(
            "getPlaylists",
            new { ids = new[] { reference.PlaylistId } },
            PlaylistQuery,
            cancellationToken).ConfigureAwait(false);

        var root = document.RootElement.GetProperty("data");
        var playlists = root.GetProperty("playlists");
        if (playlists.ValueKind != JsonValueKind.Array || playlists.GetArrayLength() == 0)
            throw new InvalidOperationException("Zvuk did not return the requested playlist.");

        var source = playlists[0];
        var tracks = new List<Track>();
        if (source.TryGetProperty("tracks", out var trackItems) &&
            trackItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in trackItems.EnumerateArray())
            {
                var track = MapTrack(item, tracks.Count);
                if (!string.IsNullOrWhiteSpace(track.Title))
                    tracks.Add(track);
            }
        }

        return new Playlist
        {
            Name = ReadString(source, "title") ?? "Плейлист Звука",
            Description = ReadString(source, "description") ?? string.Empty,
            Tracks = tracks,
            SourceService = ProviderId,
            SourceUrl = reference.Url,
            ExternalId = reference.PlaylistId
        };
    }

    public async Task<IReadOnlyList<Track>> SearchTrackAsync(
        Track track,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var query = string.Join(' ', new[] { track.Artist, track.Title }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<Track>();

        using var document = await ExecuteGraphQlAsync(
            "getSearch",
            new { query, limit = Math.Clamp(limit, 1, 30), searchSessionId = (string?)null },
            SearchQuery,
            cancellationToken).ConfigureAwait(false);

        var content = document.RootElement
            .GetProperty("data")
            .GetProperty("quickSearch")
            .GetProperty("content");

        if (content.ValueKind != JsonValueKind.Array)
            return Array.Empty<Track>();

        var results = new List<Track>();
        foreach (var item in content.EnumerateArray())
        {
            if (!string.Equals(ReadString(item, "__typename"), "Track", StringComparison.Ordinal))
                continue;

            var mapped = MapTrack(item, results.Count);
            if (!string.IsNullOrWhiteSpace(mapped.Title))
                results.Add(mapped);

            if (results.Count >= Math.Clamp(limit, 1, 30))
                break;
        }

        return results;
    }

    public Task<string> CreatePlaylistAsync(
        string name,
        string description = "",
        bool isPublic = false,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Создание плейлистов в Звуке пока не реализовано.");

    public Task AddTracksAsync(
        string playlistId,
        IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Добавление треков в плейлисты Звука пока не реализовано.");

    public string GetPlaylistUrl(string playlistId) => $"https://zvuk.com/playlist/{Uri.EscapeDataString(playlistId)}";

    private async Task<JsonDocument> ExecuteGraphQlAsync(
        string operationName,
        object variables,
        string query,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { operationName, variables, query });

        // Anonymous tokens can expire while the app is open. Refresh once after an
        // authentication failure, but never loop indefinitely on rejected credentials.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetAnonymousTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Auth-Token", token);

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                await InvalidateAnonymousTokenAsync(token, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Zvuk API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

            var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var message = ReadString(errors[0], "message") ?? "Unknown GraphQL error";
                document.Dispose();
                throw new InvalidOperationException($"Zvuk API error: {message}");
            }

            if (!document.RootElement.TryGetProperty("data", out _))
            {
                document.Dispose();
                throw new InvalidOperationException("Zvuk API returned an unexpected response.");
            }

            return document;
        }

        throw new UnauthorizedAccessException("Zvuk rejected the refreshed anonymous session.");
    }

    private async Task InvalidateAnonymousTokenAsync(string rejectedToken, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Do not clear a newer token that another concurrent request may have fetched.
            if (string.Equals(_anonymousToken, rejectedToken, StringComparison.Ordinal))
                _anonymousToken = null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string> GetAnonymousTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_anonymousToken))
            return _anonymousToken;

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_anonymousToken))
                return _anonymousToken;

            using var response = await _httpClient.GetAsync(ProfileUrl, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Zvuk token endpoint returned HTTP {(int)response.StatusCode}.");

            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
                throw new InvalidOperationException("Zvuk did not return an anonymous access token.");

            _anonymousToken = tokenElement.GetString();
            return _anonymousToken!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static Track MapTrack(JsonElement item, int position)
    {
        var id = ReadString(item, "id");
        var artist = ReadArtist(item);
        var durationSeconds = ReadInt(item, "duration");
        var track = new Track
        {
            Title = ReadString(item, "title") ?? string.Empty,
            Artist = artist,
            Album = item.TryGetProperty("release", out var release)
                ? ReadString(release, "title") ?? string.Empty
                : string.Empty,
            DurationMs = durationSeconds is > 0
                ? (int)Math.Min(int.MaxValue, (long)durationSeconds.Value * 1000)
                : null,
            Position = position,
            SourceUrl = string.IsNullOrWhiteSpace(id) ? null : $"https://zvuk.com/track/{Uri.EscapeDataString(id)}"
        };

        if (!string.IsNullOrWhiteSpace(id))
            track.ProviderIds["zvuk"] = id;

        return track;
    }

    private static string ReadArtist(JsonElement item)
    {
        if (item.TryGetProperty("artistNames", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            var values = names.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x));
            var joined = string.Join(", ", values);
            if (!string.IsNullOrWhiteSpace(joined))
                return joined;
        }

        if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var values = artists.EnumerateArray()
                .Select(x => ReadString(x, "title"))
                .Where(x => !string.IsNullOrWhiteSpace(x));
            var joined = string.Join(", ", values);
            if (!string.IsNullOrWhiteSpace(joined))
                return joined;
        }

        return ReadString(item, "artistTemplate") ?? string.Empty;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out number))
            return number;

        return null;
    }
}
