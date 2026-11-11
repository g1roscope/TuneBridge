namespace TuneBridge.Core.Matching;

using TuneBridge.Core.Models;

/// <summary>
/// Builds cache keys that mirror the query strategy used by each destination provider.
/// A provider must never reuse results for a different effective search query.
/// </summary>
public static class TrackSearchCacheKey
{
    public static string Create(string providerId, Track track)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(track);

        var provider = providerId.Trim().ToLowerInvariant();
        if (provider == "spotify" && !string.IsNullOrWhiteSpace(track.Isrc))
            return $"{provider}:isrc:{track.Isrc.Trim().ToUpperInvariant()}";

        // Yandex and other text-search providers currently query by artist + title;
        // an ISRC alone is not a valid cache key for those providers.
        return $"{provider}:text:{track.Artist.Trim()}\u001F{track.Title.Trim()}";
    }
}
