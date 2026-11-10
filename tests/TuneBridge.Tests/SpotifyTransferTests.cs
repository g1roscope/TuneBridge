using System.Net;
using System.Text.Json;
using TuneBridge.Core.Models;
using TuneBridge.Infrastructure.Spotify;
using Xunit;

namespace TuneBridge.Tests;

public sealed class SpotifyTransferTests
{
    [Fact]
    public async Task AddTracks_rejects_missing_destination_ids_before_any_write()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "client" });
        provider.SetUserTokens("user-token", DateTimeOffset.UtcNow.AddHours(1));

        var tracks = new[]
        {
            new Track { Title = "Valid", ProviderIds = { ["spotify"] = "track-1" } },
            new Track { Title = "Missing ID" }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.AddTracksAsync("playlist-1", tracks));

        Assert.Contains("Track 2", exception.Message);
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task AddTracks_preserves_order_and_batches_at_one_hundred()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "client" });
        provider.SetUserTokens("user-token", DateTimeOffset.UtcNow.AddHours(1));

        var tracks = Enumerable.Range(0, 101)
            .Select(index => new Track
            {
                Title = $"Track {index}",
                ProviderIds = { ["spotify"] = $"id-{index}" }
            })
            .ToList();

        await provider.AddTracksAsync("playlist-1", tracks);

        Assert.Equal(2, handler.RequestBodies.Count);
        using var first = JsonDocument.Parse(handler.RequestBodies[0]);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        var firstUris = first.RootElement.GetProperty("uris");
        var secondUris = second.RootElement.GetProperty("uris");

        Assert.Equal(100, firstUris.GetArrayLength());
        Assert.Equal(1, secondUris.GetArrayLength());
        Assert.Equal("spotify:track:id-0", firstUris[0].GetString());
        Assert.Equal("spotify:track:id-99", firstUris[99].GetString());
        Assert.Equal("spotify:track:id-100", secondUris[0].GetString());
    }

    [Fact]
    public async Task AddTracks_reports_confirmed_count_when_later_batch_fails()
    {
        var handler = new FailSecondBatchHandler();
        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "client" });
        provider.SetUserTokens("user-token", DateTimeOffset.UtcNow.AddHours(1));

        var tracks = Enumerable.Range(0, 101)
            .Select(index => new Track
            {
                Title = $"Track {index}",
                ProviderIds = { ["spotify"] = $"id-{index}" }
            })
            .ToList();

        var exception = await Assert.ThrowsAsync<SpotifyPartialTransferException>(() =>
            provider.AddTracksAsync("playlist-1", tracks));

        Assert.Equal(100, exception.ConfirmedTracksAdded);
        Assert.True(exception.CurrentBatchOutcomeUnknown);
    }

    [Fact]
    public async Task AddTracks_marks_first_batch_as_unknown_when_server_returns_ambiguous_error()
    {
        var handler = new AmbiguousFailureHandler();
        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "client" });
        provider.SetUserTokens("user-token", DateTimeOffset.UtcNow.AddHours(1));

        var tracks = new[]
        {
            new Track { Title = "Track 1", ProviderIds = { ["spotify"] = "id-1" } }
        };

        var exception = await Assert.ThrowsAsync<SpotifyPartialTransferException>(() =>
            provider.AddTracksAsync("playlist-1", tracks));

        Assert.Equal(0, exception.ConfirmedTracksAdded);
        Assert.True(exception.CurrentBatchOutcomeUnknown);
        Assert.Contains("may have been applied", exception.Message);
    }

    [Fact]
    public async Task AddTracks_marks_first_batch_as_unknown_when_request_is_cancelled_in_flight()
    {
        var handler = new CancelInFlightHandler();
        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "client" });
        provider.SetUserTokens("user-token", DateTimeOffset.UtcNow.AddHours(1));

        var tracks = new[]
        {
            new Track { Title = "Track 1", ProviderIds = { ["spotify"] = "id-1" } }
        };

        var exception = await Assert.ThrowsAsync<SpotifyPartialTransferException>(() =>
            provider.AddTracksAsync("playlist-1", tracks));

        Assert.Equal(0, exception.ConfirmedTracksAdded);
        Assert.True(exception.CurrentBatchOutcomeUnknown);
    }

    [Fact]
    public async Task AddTracks_reports_confirmed_count_when_cancelled_between_batches()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new CancelAfterFirstBatchHandler(cancellation);
        using var http = new HttpClient(handler);
        var provider = new SpotifyProvider(http, new SpotifyOptions { ClientId = "client" });
        provider.SetUserTokens("user-token", DateTimeOffset.UtcNow.AddHours(1));

        var tracks = Enumerable.Range(0, 101)
            .Select(index => new Track
            {
                Title = $"Track {index}",
                ProviderIds = { ["spotify"] = $"id-{index}" }
            })
            .ToList();

        var exception = await Assert.ThrowsAsync<SpotifyPartialTransferException>(() =>
            provider.AddTracksAsync("playlist-1", tracks, cancellation.Token));

        Assert.Equal(100, exception.ConfirmedTracksAdded);
        Assert.False(exception.CurrentBatchOutcomeUnknown);
    }

    private sealed class AmbiguousFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("temporary failure")
            });
    }

    private sealed class CancelInFlightHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new OperationCanceledException("Connection interrupted after dispatch."));
    }

    private sealed class CancelAfterFirstBatchHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            cancellation.Cancel();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"snapshot_id":"snapshot"}""")
            };
        }
    }

    private sealed class FailSecondBatchHandler : HttpMessageHandler
    {
        private int _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests++;
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            return _requests == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"snapshot_id":"snapshot"}""") }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    { Content = new StringContent("temporary failure") };
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("user-token", request.Headers.Authorization?.Parameter);
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"snapshot_id":"snapshot"}""")
            };
        }
    }
}
