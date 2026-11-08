namespace TuneBridge.Infrastructure.Security;

/// <summary>
/// Secure persistence for OAuth tokens. Implementations must not write plaintext secrets to disk
/// without OS-level protection (DPAPI on Windows).
/// </summary>
public interface ITokenStore
{
    Task SaveAsync(string key, string plaintext, CancellationToken cancellationToken = default);
    Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
