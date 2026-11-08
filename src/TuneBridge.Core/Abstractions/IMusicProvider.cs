using TuneBridge.Core.Models;

namespace TuneBridge.Core.Abstractions;

/// <summary>
/// Contract for a music service integration.
/// Implementations must not pretend to succeed when the underlying API was not called.
/// </summary>
public interface IMusicProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool IsExperimental { get; }

    ProviderCapabilities GetCapabilities();

    /// <summary>Parse a playlist URL. Returns null if this provider does not own the URL.</summary>
    ParsedPlaylistRef? ParsePlaylistUrl(string url);

    /// <summary>
    /// Fetch a publicly readable playlist.
    /// Throws if credentials are missing or the API call fails — never returns fabricated tracks.
    /// </summary>
    Task<Playlist> GetPublicPlaylistAsync(ParsedPlaylistRef reference, CancellationToken cancellationToken = default);

    /// <summary>Search for candidates matching the given track metadata.</summary>
    Task<IReadOnlyList<Track>> SearchTrackAsync(Track track, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>Create a playlist in the user's account. Requires prior user authorization.</summary>
    Task<string> CreatePlaylistAsync(string name, string description = "", bool isPublic = false, CancellationToken cancellationToken = default);

    /// <summary>Add tracks (that already have this provider's IDs) to an existing playlist.</summary>
    Task AddTracksAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default);

    string GetPlaylistUrl(string playlistId);
}
