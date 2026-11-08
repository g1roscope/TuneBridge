using TuneBridge.Core.Models;

namespace TuneBridge.Infrastructure.Storage;

public sealed class TransferHistoryStore
{
    private const string FileName = "transfer_history.json";
    private readonly int _maxEntries;
    private List<TransferHistoryEntry> _entries = new();
    private bool _loaded;

    public TransferHistoryStore(int maxEntries = 100)
    {
        _maxEntries = Math.Clamp(maxEntries, 10, 500);
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        var list = await JsonFileStore.LoadAsync<List<TransferHistoryEntry>>(FileName, ct).ConfigureAwait(false);
        _entries = list ?? new List<TransferHistoryEntry>();
        _loaded = true;
    }

    public async Task<IReadOnlyList<TransferHistoryEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _entries.OrderByDescending(e => e.Timestamp).ToList();
    }

    public async Task AddAsync(TransferHistoryEntry entry, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        _entries.Insert(0, entry);
        while (_entries.Count > _maxEntries)
            _entries.RemoveAt(_entries.Count - 1);
        await JsonFileStore.SaveAsync(FileName, _entries, ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        _entries.Clear();
        _loaded = true;
        await JsonFileStore.SaveAsync(FileName, _entries, ct).ConfigureAwait(false);
    }
}
