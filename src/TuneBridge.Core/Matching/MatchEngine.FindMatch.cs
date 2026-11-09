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
                var tags = c.VersionTags.Count > 0 ? c.VersionTags : ExtractVersionTags(c.Title, c.Album);
                return (Track: c, Score: score, Reasons: reasons, Tags: tags);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var sourceHasVersion = source.VersionTags.Count > 0;
        var compatible = ranked
            .Where(x => sourceHasVersion
                ? new HashSet<string>(x.Tags, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(source.VersionTags)
                : x.Tags.Count == 0)
            .ToList();

        // A tagged remix/live/acoustic result is not an ordinary match for an untagged
        // studio source. Keep it visible as a separate review-only outcome.
        var hasOrdinaryCandidate = compatible.Any(x => x.Score >= AmbiguousThreshold);
        var alternateOnly = !sourceHasVersion && !hasOrdinaryCandidate &&
            ranked.Any(x => x.Tags.Count > 0 && x.Score >= AmbiguousThreshold);

        var pool = alternateOnly
            ? ranked.Where(x => x.Tags.Count > 0).ToList()
            : compatible.Count > 0 ? compatible : ranked;
        var best = pool[0];
        var secondScore = pool.Count > 1 ? pool[1].Score : 0.0;

        MatchStatus status;
        if (alternateOnly)
            status = MatchStatus.AlternateVersion;
        else if (best.Score >= MatchedThreshold && (best.Score - secondScore) >= ScoreGapForMatched)
            status = MatchStatus.Matched;
        else if (best.Score >= AmbiguousThreshold)
            status = MatchStatus.Ambiguous;
        else
            status = MatchStatus.Missing;

        var reasons = new List<string>(best.Reasons);
        if (status == MatchStatus.Ambiguous)
            reasons.Add("Review required: similar or uncertain result");
        if (status == MatchStatus.AlternateVersion)
            reasons.Add("Only alternate recording versions were found; review before transferring");

        var alternates = ranked
            .Where(x => x.Track != best.Track)
            .Take(4)
            .Where(x => x.Score >= AmbiguousThreshold * 0.85)
            .Select(x => (x.Track, x.Score))
            .ToList();

        return new MatchResult
        {
            Source = source,
            Candidate = status == MatchStatus.Matched || status == MatchStatus.Ambiguous ||
                        status == MatchStatus.AlternateVersion ? best.Track : null,
            Status = status,
            Score = best.Score,
            Reasons = reasons,
            Alternates = alternates
        };
    }
}
