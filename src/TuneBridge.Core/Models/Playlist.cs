namespace TuneBridge.Core.Models;

public sealed class Playlist
{
    public string Name { get; set; } = "Imported playlist";
    public string Description { get; set; } = string.Empty;
    public List<Track> Tracks { get; set; } = new();
    public string SourceService { get; set; } = "unknown";
    public string? SourceUrl { get; set; }
    public string? ExternalId { get; set; }
}
