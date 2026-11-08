using TuneBridge.Infrastructure.Zvuk;

namespace TuneBridge.Tests;

public class ZvukProviderTests
{
    [Fact]
    public void Is_experimental_and_network_disabled()
    {
        var z = new ZvukProvider();
        Assert.True(z.IsExperimental);
        var caps = z.GetCapabilities();
        Assert.True(caps.IsExperimental);
        Assert.False(caps.CanReadPublic);
        Assert.False(caps.CanSearch);
    }

    [Fact]
    public async Task GetPublic_throws_not_supported()
    {
        var z = new ZvukProvider();
        var reference = z.ParsePlaylistUrl("https://zvuk.com/playlist/123");
        Assert.NotNull(reference);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            z.GetPublicPlaylistAsync(reference!));
    }

    [Fact]
    public void Parses_zvuk_host()
    {
        var z = new ZvukProvider();
        Assert.NotNull(z.ParsePlaylistUrl("https://zvuk.com/playlist/abc"));
        Assert.Null(z.ParsePlaylistUrl("https://open.spotify.com/playlist/x"));
    }
}
