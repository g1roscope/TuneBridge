using TuneBridge.Core.Abstractions;
using TuneBridge.Core.Models;

namespace TuneBridge.Core.Services;

/// <summary>
/// Central registry for installed music-service adapters. A provider is exposed as a
/// destination only when playlist creation and track insertion are both implemented.
/// </summary>
public sealed class MusicProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IMusicProvider> _providers;

    public MusicProviderRegistry(IEnumerable<IMusicProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var map = new Dictionary<string, IMusicProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (string.IsNullOrWhiteSpace(provider.ProviderId))
                throw new ArgumentException("Every provider must have a non-empty provider ID.", nameof(providers));
            if (!map.TryAdd(provider.ProviderId, provider))
                throw new ArgumentException($"Duplicate provider ID: {provider.ProviderId}.", nameof(providers));
        }

        _providers = map;
    }

    public IReadOnlyCollection<IMusicProvider> Providers => _providers.Values.ToArray();

    public IMusicProvider GetRequired(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID is required.", nameof(providerId));

        return _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new KeyNotFoundException($"Music provider '{providerId}' is not registered.");
    }

    public bool TryParsePlaylistUrl(string url, out IMusicProvider? provider, out ParsedPlaylistRef? reference)
    {
        provider = null;
        reference = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        foreach (var candidate in _providers.Values)
        {
            if (!candidate.GetCapabilities().CanParseUrl)
                continue;

            var parsed = candidate.ParsePlaylistUrl(url);
            if (parsed is null)
                continue;

            if (!string.Equals(parsed.ProviderId, candidate.ProviderId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Provider '{candidate.ProviderId}' returned a playlist reference for '{parsed.ProviderId}'.");

            provider = candidate;
            reference = parsed;
            return true;
        }

        return false;
    }

    public IReadOnlyList<IMusicProvider> GetAvailableDestinations()
        => _providers.Values
            .Where(provider =>
            {
                var capabilities = provider.GetCapabilities();
                return capabilities.CanCreatePlaylist && capabilities.CanAddTracks;
            })
            .OrderBy(provider => provider.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
}
