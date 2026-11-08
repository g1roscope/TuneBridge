namespace TuneBridge.Core.Models;

public sealed class ParsedPlaylistRef
{
    public required string ProviderId { get; init; }
    public required string PlaylistId { get; init; }
    public required string Url { get; init; }
    public bool IsPublic { get; init; } = true;
}
