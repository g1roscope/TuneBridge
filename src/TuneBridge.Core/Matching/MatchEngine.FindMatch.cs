using TuneBridge.Core.Models;

namespace TuneBridge.Core.Matching;

public static partial class MatchEngine
{
    public static MatchResult FindMatch(Track source, IReadOnlyList<Track> candidates)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return new MatchResult
            {
                Source = source,
                Status = MatchStatus.Missing,
                Score = 0,
                Reasons = ["No candidates returned"]
            };
        }

        if (source.VersionTags.Count == 0)
            source.VersionTags = ExtractVersionTags(source.Title, source.Album);

        var ranked = candidates
            .Select(c =>
            {
                var (score, reasons) = ScoreTrack(source, c);
                return (Track: c, Score: score, Reasons: reasons);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var best = ranked[0];
        var secondScore = ranked.Count > 1 ? ranked[1].Score : 0.0;

        MatchStatus status;
        if (best.Score >= MatchedThreshold && (best.Score - secondScore) >= ScoreGapForMatched)
            status = MatchStatus.Matched;
        else if (best.Score >= AmbiguousThreshold)
            status = MatchStatus.Ambiguous;
        else
            status = MatchStatus.Missing;

        var reasons = new List<string>(best.Reasons);
        if (status == MatchStatus.Ambiguous)
            reasons.Add("Review required: similar or uncertain result");

        return new MatchResult
        {
            Source = source,
            Candidate = status == MatchStatus.Missing ? null : best.Track,
            Score = best.Score,
            Status = status,
            Reasons = reasons,
            Alternates = ranked.Skip(1).Take(3).Select(x => (x.Track, x.Score)).ToList()
        };
    }
}
