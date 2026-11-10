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
        cancellationToken.ThrowIfCancellationRequested();

        var path = PathFor(key);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                plaintextBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, options: FileOptions.WriteThrough))
                {
                    stream.Write(protectedBytes);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(key);
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        byte[]? plaintextBytes = null;
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            try
            {
                plaintextBytes = ProtectedData.Unprotect(
                    protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plaintextBytes));
        }
        catch (CryptographicException)
        {
            // Corrupted or different user — treat as missing without deleting the evidence.
            return Task.FromResult<string?>(null);
        }
        finally
        {
            if (plaintextBytes is not null)
                CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
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
