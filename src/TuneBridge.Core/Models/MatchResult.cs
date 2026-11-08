namespace TuneBridge.Core.Models;

public sealed class MatchResult
{
    public required Track Source { get; init; }
    public Track? Candidate { get; init; }
    public double Score { get; init; }
    public MatchStatus Status { get; init; } = MatchStatus.Missing;
    public List<string> Reasons { get; init; } = new();
    public List<(Track Track, double Score)> Alternates { get; init; } = new();
}
