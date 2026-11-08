using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Storage;

public sealed class AppSettingsStore
{
    private const string FileName = "app_settings.json";
    private AppSettings _settings = new();
    private bool _loaded;

    public async Task<AppSettings> GetAsync(CancellationToken ct = default)
    {
        if (!_loaded)
        {
            var loaded = await JsonFileStore.LoadAsync<AppSettings>(FileName, ct).ConfigureAwait(false);
            _settings = loaded ?? new AppSettings();
            _loaded = true;
        }
        return _settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        _settings = settings;
        _loaded = true;
        await JsonFileStore.SaveAsync(FileName, settings, ct).ConfigureAwait(false);
    }
}
