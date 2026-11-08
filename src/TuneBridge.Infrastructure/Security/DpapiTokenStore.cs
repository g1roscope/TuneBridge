using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TuneBridge.Infrastructure.Security;

/// <summary>
/// Windows DPAPI (CurrentUser scope) token store. Data is encrypted for the logged-in Windows user.
/// On non-Windows platforms throws PlatformNotSupportedException — use InMemoryTokenStore for tests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore : ITokenStore
{
    private readonly string _directory;

    public DpapiTokenStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TuneBridge",
            "tokens");
        Directory.CreateDirectory(_directory);
    }

    public Task SaveAsync(string key, string plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(plaintext);

        var path = PathFor(key);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedBytes);
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = PathFor(key);
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        catch (CryptographicException)
        {
            // Corrupted or different user — treat as missing
            return Task.FromResult<string?>(null);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = PathFor(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(_directory, safe + ".bin");
    }
}
