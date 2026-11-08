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
        var path = Path.Combine(GetAppDataDirectory(), fileName);
        if (!File.Exists(path))
            return null;
        try
        {
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(fs, Options, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public static async Task SaveAsync<T>(string fileName, T value, CancellationToken ct = default)
    {
        var path = Path.Combine(GetAppDataDirectory(), fileName);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, value, Options, ct).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string fileName)
    {
        var path = Path.Combine(GetAppDataDirectory(), fileName);
        if (File.Exists(path))
            File.Delete(path);
    }
}
