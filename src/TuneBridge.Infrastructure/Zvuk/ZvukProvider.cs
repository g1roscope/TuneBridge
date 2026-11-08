using TuneBridge.Core.Abstractions;
using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Zvuk;

/// <summary>
/// Звук (ex-SberZvuk) provider — EXPERIMENTAL.
/// Official third-party playlist API is not integrated. Network methods throw NotSupportedException.
/// Do not replace these throws with fabricated playlists.
/// </summary>
public sealed class ZvukProvider : IMusicProvider
{
    public string ProviderId => "zvuk";
    public string DisplayName => "Звук";
    public bool IsExperimental => true;

    public ProviderCapabilities GetCapabilities() => new()
    {
        CanParseUrl = true,
        CanReadPublic = false,
        CanReadPrivate = false,
        CanSearch = false,
        CanCreatePlaylist = false,
        CanAddTracks = false,
        SupportsIsrc = false,
        RequiresOAuth = true,
        IsExperimental = true,
        Notes =
            "Official third-party playlist API is not integrated. " +
            "Network methods throw NotSupportedException until a supported access path exists. " +
            "Marked Experimental by product policy."
    };

    public ParsedPlaylistRef? ParsePlaylistUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (url.Contains("zvuk.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("sber-zvuk.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var id = segments.LastOrDefault() ?? "unknown";
            return new ParsedPlaylistRef
            {
                ProviderId = ProviderId,
                PlaylistId = id,
                Url = url,
                IsPublic = true
            };
        }
        return null;
    }

    public Task<Playlist> GetPublicPlaylistAsync(ParsedPlaylistRef reference, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Zvuk public playlist fetch is not available: no supported official API is integrated in this build.");

    public Task<IReadOnlyList<Track>> SearchTrackAsync(Track track, int limit = 10, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Zvuk search is not available in this build (no supported API).");

    public Task<string> CreatePlaylistAsync(string name, string description = "", bool isPublic = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Zvuk playlist creation is not available in this build (no supported API).");

    public Task AddTracksAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Zvuk add tracks is not available in this build (no supported API).");

    public string GetPlaylistUrl(string playlistId) => $"https://zvuk.com/playlist/{playlistId}";
}
