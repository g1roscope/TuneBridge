using System.Text.Json;

namespace TuneBridge.Infrastructure.Storage;

public static class JsonFileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetAppDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Path.GetTempPath(), "TuneBridge");
        var dir = Path.Combine(root, "TuneBridge");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task<T?> LoadAsync<T>(string fileName, CancellationToken ct = default) where T : class
    {
        var path = GetFilePath(fileName);
        if (!File.Exists(path))
            return null;

        try
        {
            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(fs, Options, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // Preserve damaged user data rather than silently treating it as an empty file.
            var quarantinePath = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            try
            {
                File.Move(path, quarantinePath);
            }
            catch (Exception quarantineException) when (
                quarantineException is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    $"The local data file '{fileName}' is invalid and could not be preserved. " +
                    "The original file has been left untouched.",
                    new AggregateException(ex, quarantineException));
            }

            return null;
        }
    }

    public static async Task SaveAsync<T>(string fileName, T value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = GetFilePath(fileName);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var fs = new FileStream(
                tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(fs, value, Options, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // A cancelled or failed write must not leave a stale temp file behind.
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch (IOException)
            {
                // The primary save exception is more useful than a best-effort cleanup failure.
            }
            catch (UnauthorizedAccessException)
            {
                // The primary save exception is more useful than a best-effort cleanup failure.
            }
        }
    }

    public static void Delete(string fileName)
    {
        var path = GetFilePath(fileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string GetFilePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName) ||
            fileName is "." or ".." ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("A simple file name inside the TuneBridge data directory is required.", nameof(fileName));
        }

        return Path.Combine(GetAppDataDirectory(), fileName);
    }
}
