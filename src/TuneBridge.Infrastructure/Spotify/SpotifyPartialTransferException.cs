namespace TuneBridge.Infrastructure.Spotify;

/// <summary>
/// Reports a Spotify write that stopped after one or more confirmed batches.
/// The current batch may have an unknown outcome when cancellation interrupted its request.
/// </summary>
public sealed class SpotifyPartialTransferException : Exception
{
    public SpotifyPartialTransferException(
        int confirmedTracksAdded,
        string message,
        Exception? innerException = null,
        bool currentBatchOutcomeUnknown = false)
        : base(message, innerException)
    {
        ConfirmedTracksAdded = confirmedTracksAdded;
        CurrentBatchOutcomeUnknown = currentBatchOutcomeUnknown;
    }

    public int ConfirmedTracksAdded { get; }
    public bool CurrentBatchOutcomeUnknown { get; }
}
