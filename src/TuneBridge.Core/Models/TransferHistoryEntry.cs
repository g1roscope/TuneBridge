namespace TuneBridge.Core.Models;

public sealed class TransferHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string SourceProvider { get; set; } = "";
    public string DestinationProvider { get; set; } = "";
    public string PlaylistName { get; set; } = "";
    public string? SourceUrl { get; set; }
    public string? DestinationPlaylistId { get; set; }
    public string? DestinationUrl { get; set; }
    public int TotalTracks { get; set; }
    public int TransferredCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    public string Status { get; set; } = "Completed";
    public string? Message { get; set; }
}
