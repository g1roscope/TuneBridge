using System.Text.Json;
using TuneBridge.Core.Abstractions;
using TuneBridge.Core.Models;
using TuneBridge.Infrastructure.Security;
using YandexMusic;
using YandexMusic.Authentication;
using YandexMusic.Endpoints;
using YandexMusic.Models.Playlists;
using YandexMusic.Models.Tracks;
using YandexTrack = YandexMusic.Models.Tracks.Track;
using YandexPlaylist = YandexMusic.Models.Playlists.Playlist;

namespace TuneBridge.Infrastructure.Yandex;

/// <summary>
/// Authenticated Yandex Music integration. Authentication uses the OAuth device-code flow;
/// the serialized session is protected by the supplied OS-backed token store.
/// The underlying Yandex API is unofficial and may change without notice.
/// </summary>
public sealed class YandexMusicProvider : IMusicProvider, IDisposable
{
    private const string TokenStoreKey = "yandex-music-session";
    private const string AlbumIdMetadataKey = "yandex.albumId";

    private readonly IYandexMusicClient _client;
    private readonly ITokenStore? _tokenStore;
    private readonly bool _ownsClient;

    public YandexMusicProvider(ITokenStore? tokenStore = null)
        : this(new YandexMusicClient(new YandexMusicClientOptions { Language = "ru" }), tokenStore, ownsClient: true)
    {
    }

    public YandexMusicProvider(IYandexMusicClient client, ITokenStore? tokenStore = null)
        : this(client, tokenStore, ownsClient: false)
    {
    }

    private YandexMusicProvider(IYandexMusicClient client, ITokenStore? tokenStore, bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tokenStore = tokenStore;
        _ownsClient = ownsClient;
    }

    public string ProviderId => "yandex";
    public string DisplayName => "Яндекс Музыка";
    public bool IsExperimental => true;
    public bool IsAuthenticated => _client.Authentication.IsAuthenticated;

    public ProviderCapabilities GetCapabilities()
    {
        var authenticated = IsAuthenticated;
        return new ProviderCapabilities
        {
            CanParseUrl = true,
            CanReadPublic = true,
            CanReadPrivate = authenticated,
            CanSearch = true,
            CanCreatePlaylist = authenticated,
            CanAddTracks = authenticated,
            SupportsIsrc = true,
            RequiresOAuth = true,
            IsExperimental = true,
            Notes = authenticated
                ? "Authenticated through Yandex OAuth device-code flow. Playlist reading and writing use an unofficial API client and may change."
                : "Public playlist reading and catalogue search are available where permitted. Sign in with Yandex to read private playlists and create or edit playlists."
        };
    }

    public async Task RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_tokenStore is null)
            return;

        var json = await _tokenStore.LoadAsync(TokenStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var snapshot = JsonSerializer.Deserialize<AuthSnapshot>(json);
            if (snapshot is not null)
                _client.Authentication.Session.Import(snapshot);
        }
        catch (JsonException)
        {
            await _tokenStore.DeleteAsync(TokenStoreKey, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SignInWithDeviceFlowAsync(
        Action<DeviceCode> onCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onCode);
        await _client.Authentication.SignInWithDeviceFlowAsync(
            onCode,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await PersistSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _client.Authentication.SignOut();
        if (_tokenStore is not null)
            await _tokenStore.DeleteAsync(TokenStoreKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistSessionAsync(CancellationToken cancellationToken)
    {
        if (_tokenStore is null)
            return;

        var json = JsonSerializer.Serialize(_client.Authentication.Session.Export());
        await _tokenStore.SaveAsync(TokenStoreKey, json, cancellationToken).ConfigureAwait(false);
    }

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
            IsSafeSegment(segments[1]) &&
            IsSafeSegment(segments[3]))
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
            IsSafeSegment(segments[1]))
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

        YandexPlaylist? source;
        var separator = reference.PlaylistId.IndexOf('|');
        if (separator > 0 && separator < reference.PlaylistId.Length - 1)
        {
            var userId = reference.PlaylistId[..separator];
            var kind = reference.PlaylistId[(separator + 1)..];
            source = await _client.Playlists.GetAsync(userId, kind, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            source = await _client.Playlists.GetByUuidAsync(reference.PlaylistId, cancellationToken).ConfigureAwait(false);
        }

        if (source is null)
            throw new InvalidOperationException("Яндекс Музыка не вернула запрошенный плейлист.");

        var shortTracks = source.Tracks ?? Array.Empty<TrackShort>();
        if (shortTracks.Count == 0 && source.TrackCount > 0)
            throw new InvalidOperationException("Яндекс Музыка вернула плейлист без списка треков. Повторите попытку позже.");

        var missingIds = shortTracks
            .Where(item => item.Track is null && !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        IReadOnlyList<YandexTrack> hydrated = missingIds.Length == 0
            ? Array.Empty<YandexTrack>()
            : await _client.Tracks.GetManyAsync(missingIds, cancellationToken).ConfigureAwait(false);

        var hydratedById = hydrated.ToDictionary(track => track.Id, StringComparer.Ordinal);
        var result = new List<Track>(shortTracks.Count);

        for (var index = 0; index < shortTracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = shortTracks[index];
            var fullTrack = item.Track;
            if (fullTrack is null && !hydratedById.TryGetValue(item.Id, out fullTrack))
                throw new InvalidOperationException($"Яндекс Музыка не вернула данные трека с ID {item.Id}; импорт остановлен, чтобы не потерять композицию.");

            var mapped = MapTrack(fullTrack!, item.AlbumId, index);
            mapped.Position = item.OriginalIndex >= 0 ? item.OriginalIndex : index;
            result.Add(mapped);
        }

        return new Playlist
        {
            Name = string.IsNullOrWhiteSpace(source.Title) ? "Плейлист Яндекс Музыки" : source.Title,
            Description = source.Description ?? string.Empty,
            Tracks = result,
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
        ArgumentNullException.ThrowIfNull(track);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Search limit must be greater than zero.");

        var query = string.Join(' ', new[] { track.Artist, track.Title }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<Track>();

        var response = await _client.Search.SearchAsync(
            query,
            SearchType.Track,
            page: 0,
            correctMisspells: true,
            cancellationToken).ConfigureAwait(false);

        var sourceTracks = response?.Tracks?.Results ?? Array.Empty<YandexTrack>();
        return sourceTracks
            .Take(Math.Clamp(limit, 1, 50))
            .Select((item, index) => MapTrack(item, null, index))
            .ToList();
    }

    public async Task<string> CreatePlaylistAsync(
        string name,
        string description = "",
        bool isPublic = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated)
            throw new UnauthorizedAccessException("Подключите Яндекс Музыку перед созданием плейлиста.");

        var status = await _client.Account.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var userId = status?.Account?.Uid ?? 0;
        if (userId <= 0)
            throw new UnauthorizedAccessException("Не удалось определить пользователя Яндекс Музыки. Переподключите аккаунт.");

        var created = await _client.Playlists.CreateAsync(
            userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(name) ? "TuneBridge playlist" : name,
            isPublic ? PlaylistVisibility.Public : PlaylistVisibility.Private,
            cancellationToken).ConfigureAwait(false);

        if (created is null || created.Kind <= 0)
            throw new InvalidOperationException("Яндекс Музыка не подтвердила создание плейлиста.");

        if (!string.IsNullOrWhiteSpace(description))
        {
            var updated = await _client.Playlists.SetDescriptionAsync(
                created.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                created.Kind.ToString(System.Globalization.CultureInfo.InvariantCulture),
                description,
                cancellationToken).ConfigureAwait(false);

            if (updated is null)
                throw new InvalidOperationException("Плейлист создан, но Яндекс Музыка не подтвердила сохранение описания. Проверьте его в аккаунте.");
        }

        return $"{created.Uid}|{created.Kind}";
    }

    public async Task AddTracksAsync(
        string playlistId,
        IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentNullException.ThrowIfNull(tracks);

        if (!IsAuthenticated)
            throw new UnauthorizedAccessException("Подключите Яндекс Музыку перед добавлением треков.");

        var separator = playlistId.IndexOf('|');
        if (separator <= 0 || separator == playlistId.Length - 1)
            throw new ArgumentException("Invalid Yandex playlist ID.", nameof(playlistId));

        var userId = playlistId[..separator];
        var kind = playlistId[(separator + 1)..];
        var playlist = await _client.Playlists.GetAsync(userId, kind, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Созданный плейлист Яндекс Музыки не найден.");

        // Resolve and validate every target ID/album before making the first write.
        var prepared = new List<(string TrackId, string AlbumId)>(tracks.Count);
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!track.ProviderIds.TryGetValue(ProviderId, out var trackId) || string.IsNullOrWhiteSpace(trackId))
                throw new InvalidOperationException($"Для трека «{track.Title}» отсутствует ID Яндекс Музыки.");

            track.Metadata.TryGetValue(AlbumIdMetadataKey, out var albumId);
            if (string.IsNullOrWhiteSpace(albumId))
            {
                var fullTrack = await _client.Tracks.GetAsync(trackId, cancellationToken).ConfigureAwait(false);
                albumId = fullTrack?.Albums.FirstOrDefault()?.Id;
            }

            if (string.IsNullOrWhiteSpace(albumId))
                throw new InvalidOperationException($"Не удалось определить альбом трека «{track.Title}»; запись плейлиста остановлена.");

            prepared.Add((trackId, albumId));
        }

        var revision = playlist.Revision > 0 ? playlist.Revision : 1;
        var position = Math.Max(0, playlist.TrackCount);
        var confirmed = 0;

        foreach (var item in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var updated = await _client.Playlists.InsertTrackAsync(
                    userId,
                    kind,
                    item.TrackId,
                    item.AlbumId,
                    at: position,
                    revision: revision,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (updated is null)
                    throw new InvalidOperationException("Яндекс Музыка не подтвердила добавление трека.");

                confirmed++;
                position++;
                revision = updated.Revision > 0 ? updated.Revision : revision + 1;
            }
            catch (Exception ex) when (confirmed > 0)
            {
                var outcomeUnknown = ex is HttpRequestException or OperationCanceledException;
                throw new YandexPartialTransferException(confirmed, outcomeUnknown, ex);
            }
        }
    }

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

    private static Track MapTrack(YandexTrack source, string? albumIdOverride, int position)
    {
        var albumId = !string.IsNullOrWhiteSpace(albumIdOverride)
            ? albumIdOverride
            : source.Albums.FirstOrDefault()?.Id;

        var mapped = new Track
        {
            Title = string.IsNullOrWhiteSpace(source.Version) ? source.Title : $"{source.Title} ({source.Version})",
            Artist = source.Artists.Count > 0
                ? string.Join(", ", source.Artists.Select(artist => artist.Name).Where(name => !string.IsNullOrWhiteSpace(name)))
                : source.UgcArtistName ?? string.Empty,
            Album = string.Join(", ", source.Albums.Select(album => album.Title).Where(title => !string.IsNullOrWhiteSpace(title))),
            DurationMs = source.DurationMs is >= 0 and <= int.MaxValue ? (int)source.DurationMs : null,
            Isrc = source.Isrc,
            Position = position,
            SourceUrl = string.IsNullOrWhiteSpace(source.Id) ? null : $"https://music.yandex.ru/track/{Uri.EscapeDataString(source.Id)}"
        };

        if (!string.IsNullOrWhiteSpace(source.Id))
            mapped.ProviderIds[ProviderId] = source.Id;
        if (!string.IsNullOrWhiteSpace(albumId))
            mapped.Metadata[AlbumIdMetadataKey] = albumId;

        return mapped;
    }

    private static bool IsSafeSegment(string value)
        => value.Length is > 0 and <= 128 &&
           value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsYandexMusicHost(string host)
    {
        const string prefix = "music.yandex.";
        if (!host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = host[prefix.Length..];
        return suffix is "ru" or "com" or "by" or "kz" or "uz";
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}
