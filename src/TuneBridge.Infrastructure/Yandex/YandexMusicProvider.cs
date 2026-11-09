using System.Net;
using YandexMusicResolver;
using YandexMusicResolver.AudioItems;
using YandexMusicResolver.Config;
using TuneBridge.Core.Abstractions;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Yandex;

/// <summary>
/// Anonymous, read-only Yandex Music integration built on YandexMusicResolver 7.3.0.
/// Supports track search and public playlist reading. It does not access private playlists,
/// create playlists, add tracks, or retrieve full audio streams.
/// </summary>
public sealed class YandexMusicProvider : IMusicProvider
{
    private readonly IYandexMusicMainResolver _resolver;

    public YandexMusicProvider(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _resolver = YandexMusicMainResolver.Create(
            YandexAnonymousCredentialProvider.Instance,
            httpClient);
    }

    public string ProviderId => "yandex";
    public string DisplayName => "Яндекс Музыка";
    public bool IsExperimental => false;

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
        IsExperimental = false,
        Notes = "Anonymous public playlist reading and track search. Album metadata is not exposed by YandexMusicResolver 7.3.0. Private playlists and playlist writing are not supported."
    };

    public ParsedPlaylistRef? ParsePlaylistUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !IsYandexMusicHost(uri.IdnHost))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length == 4 &&
            segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("playlists", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(segments[1]) &&
            !string.IsNullOrWhiteSpace(segments[3]))
        {
            return new ParsedPlaylistRef
            {
                ProviderId = ProviderId,
                PlaylistId = $"{segments[1]}|{segments[3]}",
                Url = uri.GetLeftPart(UriPartial.Path),
                IsPublic = true
            };
        }

        if (segments.Length == 2 &&
            segments[0].Equals("playlists", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(segments[1]))
        {
            return new ParsedPlaylistRef
            {
                ProviderId = ProviderId,
                PlaylistId = segments[1],
                Url = uri.GetLeftPart(UriPartial.Path),
                IsPublic = true
            };
        }

        return null;
    }

    public async Task<Playlist> GetPublicPlaylistAsync(
        ParsedPlaylistRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Playlist reference does not belong to Yandex Music.", nameof(reference));

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            YandexMusicPlaylist? source;
            var separator = reference.PlaylistId.IndexOf('|');
            if (separator > 0 && separator < reference.PlaylistId.Length - 1)
            {
                var userId = reference.PlaylistId[..separator];
                var kind = reference.PlaylistId[(separator + 1)..];
                source = await _resolver.PlaylistLoader.LoadPlaylist(userId, kind).ConfigureAwait(false);
            }
            else
            {
                source = await _resolver.PlaylistLoader.LoadPlaylist(reference.PlaylistId).ConfigureAwait(false);
            }

            if (source is null)
                throw new InvalidOperationException("Yandex Music did not return the requested public playlist.");

            cancellationToken.ThrowIfCancellationRequested();
            return new Playlist
            {
                Name = string.IsNullOrWhiteSpace(source.Title) ? "Плейлист Яндекс Музыки" : source.Title,
                Tracks = source.Data.Select(MapTrack).ToList(),
                SourceService = ProviderId,
                SourceUrl = reference.Url,
                ExternalId = reference.PlaylistId
            };
        }
        catch (YandexMusicException ex)
        {
            throw TranslateResolverException(ex, "Не удалось загрузить публичный плейлист Яндекс Музыки.");
        }
    }

    public async Task<IReadOnlyList<Track>> SearchTrackAsync(
        Track track,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Search limit must be greater than zero.");

        var query = string.Join(' ', new[] { track.Artist, track.Title }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<Track>();

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // The library inserts query into a URL without escaping it itself.
            var encodedQuery = Uri.EscapeDataString(query);
            var result = await _resolver.SearchResultLoader
                .LoadSearchResult(YandexSearchType.Track, encodedQuery, Math.Min(limit, 50))
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return result?.Tracks?.Select(MapTrack).ToList() ?? (IReadOnlyList<Track>)Array.Empty<Track>();
        }
        catch (YandexMusicException ex)
        {
            throw TranslateResolverException(ex, "Не удалось выполнить поиск в Яндекс Музыке.");
        }
    }

    public Task<string> CreatePlaylistAsync(
        string name,
        string description = "",
        bool isPublic = false,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Яндекс Музыка: создание плейлистов пока не поддерживается.");

    public Task AddTracksAsync(
        string playlistId,
        IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Яндекс Музыка: добавление треков в плейлисты пока не поддерживается.");

    public string GetPlaylistUrl(string playlistId)
    {
        var separator = playlistId.IndexOf('|');
        if (separator > 0 && separator < playlistId.Length - 1)
        {
            var userId = playlistId[..separator];
            var kind = playlistId[(separator + 1)..];
            return $"https://music.yandex.ru/users/{Uri.EscapeDataString(userId)}/playlists/{Uri.EscapeDataString(kind)}";
        }

        return $"https://music.yandex.ru/playlists/{Uri.EscapeDataString(playlistId)}";
    }

    private static Track MapTrack(YandexMusicTrack source)
    {
        var id = source.Id.ToString();
        var track = new Track
        {
            Title = source.Title ?? string.Empty,
            Artist = source.Authors is { Count: > 0 }
                ? string.Join(", ", source.Authors.Where(a => a is not null).Select(a => a.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name)))
                : string.Empty,
            Album = string.Empty,
            DurationMs = source.Length.TotalMilliseconds is >= 0 and <= int.MaxValue
                ? (int)source.Length.TotalMilliseconds
                : null,
            SourceUrl = source.Uri ?? $"https://music.yandex.ru/track/{Uri.EscapeDataString(id)}"
        };

        if (!string.IsNullOrWhiteSpace(id))
            track.ProviderIds[ProviderId] = id;

        return track;
    }

    private static Exception TranslateResolverException(YandexMusicException exception, string message)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is YandexApiResponseException api &&
                api.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new UnauthorizedAccessException($"{message} Сервис отклонил анонимный запрос (HTTP {(int)api.HttpStatusCode}).", exception);

            if (current is HttpRequestException request)
            {
                if (request.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new UnauthorizedAccessException($"{message} Сервис отклонил запрос (HTTP {(int)request.StatusCode}).", exception);

                return new HttpRequestException(message, exception, request.StatusCode);
            }
        }

        return new InvalidOperationException(message, exception);
    }

    private static bool IsYandexMusicHost(string host)
    {
        if (!host.StartsWith("music.yandex.", StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = host["music.yandex.".Length..];
        return suffix.Length is >= 2 and <= 8 && suffix.All(char.IsAsciiLetter);
    }
}