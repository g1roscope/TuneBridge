using Xunit;
using TuneBridge.Core.Models;
using TuneBridge.Infrastructure.Storage;

namespace TuneBridge.Tests;

public class HistoryAndSettingsTests
{
    [Fact]
    public async Task History_add_and_load_roundtrip()
    {
        var store = new TransferHistoryStore(maxEntries: 20);
        await store.ClearAsync();
        var entry = new TransferHistoryEntry
        {
            PlaylistName = "Test PL",
            SourceProvider = "spotify",
            DestinationProvider = "spotify",
            TotalTracks = 10,
            TransferredCount = 8,
            SkippedCount = 2,
            Status = "Completed"
        };
        await store.AddAsync(entry);
        var all = await store.GetAllAsync();
        Assert.NotEmpty(all);
        Assert.Equal("Test PL", all[0].PlaylistName);
        Assert.Equal(8, all[0].TransferredCount);
    }

    [Fact]
    public async Task Settings_roundtrip()
    {
        var store = new AppSettingsStore();
        var s = await store.GetAsync();
        s.AutoSelectMatched = false;
        s.ConfirmBeforeTransfer = false;
        await store.SaveAsync(s);
        var store2 = new AppSettingsStore();
        var loaded = await store2.GetAsync();
        Assert.False(loaded.AutoSelectMatched);
        Assert.False(loaded.ConfirmBeforeTransfer);
        loaded.AutoSelectMatched = true;
        loaded.ConfirmBeforeTransfer = true;
        await store2.SaveAsync(loaded);
    }

    [Fact]
    public async Task Corrupted_json_is_quarantined_instead_of_silently_deleted()
    {
        var fileName = $"test-corrupt-{Guid.NewGuid():N}.json";
        var directory = JsonFileStore.GetAppDataDirectory();
        var originalPath = Path.Combine(directory, fileName);

        try
        {
            await File.WriteAllTextAsync(originalPath, "{ invalid json");

            var loaded = await JsonFileStore.LoadAsync<AppSettings>(fileName);

            Assert.Null(loaded);
            Assert.False(File.Exists(originalPath));
            var backups = Directory.GetFiles(directory, fileName + ".corrupt-*");
            var backup = Assert.Single(backups);
            Assert.Equal("{ invalid json", await File.ReadAllTextAsync(backup));
        }
        finally
        {
            if (File.Exists(originalPath))
                File.Delete(originalPath);
            foreach (var backup in Directory.GetFiles(directory, fileName + ".corrupt-*"))
                File.Delete(backup);
        }
    }

    [Fact]
    public async Task Json_store_rejects_paths_outside_app_data_directory()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => JsonFileStore.LoadAsync<AppSettings>("../outside.json"));
    }

    [Fact]
    public void MatchResult_candidate_property_used()
    {
        var src = new Track { Title = "A", Artist = "B", Isrc = "USABC1234567" };
        var cand = new Track { Title = "A", Artist = "B", Isrc = "USABC1234567", ProviderIds = { ["spotify"] = "x" } };
        var r = TuneBridge.Core.Matching.MatchEngine.FindMatch(src, [cand]);
        Assert.Equal(MatchStatus.Matched, r.Status);
        Assert.NotNull(r.Candidate);
        Assert.Equal("x", r.Candidate!.ProviderIds["spotify"]);
    }
}
