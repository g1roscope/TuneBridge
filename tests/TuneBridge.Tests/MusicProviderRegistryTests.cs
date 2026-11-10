using TuneBridge.Core.Abstractions;
using TuneBridge.Core.Models;
using TuneBridge.Core.Services;

namespace TuneBridge.Tests;

public sealed class MusicProviderRegistryTests
{
    [Fact]
    public void TryParsePlaylistUrl_ReturnsOwningProviderAndReference()
    {
        var provider = new FakeProvider("sample", canParse: true);
        var registry = new MusicProviderRegistry([provider]);

        var found = registry.TryParsePlaylistUrl("https://sample.test/playlist/123", out var actualProvider, out var reference);

        Assert.True(found);
        Assert.Same(provider, actualProvider);
        Assert.Equal("sample", reference!.ProviderId);
        Assert.Equal("123", reference.PlaylistId);
    }

    [Fact]
    public void TryParsePlaylistUrl_SkipsProvidersThatCannotParseUrls()
    {
        var provider = new FakeProvider("sample", canParse: false);
        var registry = new MusicProviderRegistry([provider]);

        var found = registry.TryParsePlaylistUrl("https://sample.test/playlist/123", out var actualProvider, out var reference);

        Assert.False(found);
        Assert.Null(actualProvider);
        Assert.Null(reference);
    }

    [Fact]
    public void AvailableDestinations_RequiresBothWriteCapabilities()
    {
        var readOnly = new FakeProvider("read-only", canCreate: false, canAdd: false);
        var createOnly = new FakeProvider("create-only", canCreate: true, canAdd: false);
        var addOnly = new FakeProvider("add-only", canCreate: false, canAdd: true);
        var writable = new FakeProvider("writable", canCreate: true, canAdd: true);
        var registry = new MusicProviderRegistry([readOnly, createOnly, addOnly, writable]);

        var destinations = registry.GetAvailableDestinations();

        Assert.Single(destinations);
        Assert.Same(writable, destinations[0]);
    }

    [Fact]
    public void Constructor_RejectsDuplicateProviderIds()
    {
        var first = new FakeProvider("same");
        var second = new FakeProvider("SAME");

        Assert.Throws<ArgumentException>(() => new MusicProviderRegistry([first, second]));
    }

    private sealed class FakeProvider(
        string providerId,
        bool canParse = true,
        bool canCreate = false,
        bool canAdd = false) : IMusicProvider
    {
        public string ProviderId => providerId;
        public string DisplayName => providerId;
        public bool IsExperimental => false;

        public ProviderCapabilities GetCapabilities() => new()
        {
            CanParseUrl = canParse,
            CanReadPublic = true,
            CanSearch = true,
            CanCreatePlaylist = canCreate,
            CanAddTracks = canAdd
        };

        public ParsedPlaylistRef? ParsePlaylistUrl(string url)
        {
            if (!canParse || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !uri.Host.Equals("sample.test", StringComparison.OrdinalIgnoreCase))
                return null;

            return new ParsedPlaylistRef
            {
                ProviderId = ProviderId,
                PlaylistId = "123",
                Url = url,
                IsPublic = true
            };
        }

        public Task<Playlist> GetPublicPlaylistAsync(ParsedPlaylistRef reference, CancellationToken cancellationToken = default)
            => Task.FromResult(new Playlist { SourceService = ProviderId });

        public Task<IReadOnlyList<Track>> SearchTrackAsync(Track track, int limit = 10, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Track>>([]);

        public Task<string> CreatePlaylistAsync(string name, string description = "", bool isPublic = false, CancellationToken cancellationToken = default)
            => Task.FromResult("new-playlist");

        public Task AddTracksAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetPlaylistUrl(string playlistId) => $"https://sample.test/playlist/{playlistId}";
    }
}
