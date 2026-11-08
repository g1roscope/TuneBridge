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
