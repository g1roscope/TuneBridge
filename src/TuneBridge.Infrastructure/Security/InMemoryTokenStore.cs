using System.Collections.Concurrent;

namespace TuneBridge.Infrastructure.Security;

/// <summary>In-memory store for unit tests and non-Windows CI.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);

    public Task SaveAsync(string key, string plaintext, CancellationToken cancellationToken = default)
    {
        _map[key] = plaintext;
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_map.TryGetValue(key, out var v) ? v : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _map.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
