namespace TuneBridge.Core.Models;

/// <summary>
/// Declares what a provider can do *today*. Do not claim capabilities that are not implemented.
/// </summary>
public sealed class ProviderCapabilities
{
    public bool CanParseUrl { get; init; }
    public bool CanReadPublic { get; init; }
    public bool CanReadPrivate { get; init; }
    public bool CanSearch { get; init; }
    public bool CanCreatePlaylist { get; init; }
    public bool CanAddTracks { get; init; }
    public bool SupportsIsrc { get; init; }
    public bool RequiresOAuth { get; init; }
    public bool IsExperimental { get; init; }
    public string Notes { get; init; } = string.Empty;
}
