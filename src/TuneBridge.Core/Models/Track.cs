namespace TuneBridge.Core.Models;

public sealed class Track
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public int? DurationMs { get; set; }
    public string? Isrc { get; set; }
    public string? SourceUrl { get; set; }
    public int Position { get; set; }
    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> VersionTags { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
