using TuneBridge.Core.Models;

namespace TuneBridge.Core.Matching;

public static partial class MatchEngine
{
    /// <summary>Score one candidate against source. ISRC exact match returns 1.0.</summary>
    public static (double Score, List<string> Reasons) ScoreTrack(Track source, Track candidate)
    {
        var reasons = new List<string>();

        if (!string.IsNullOrWhiteSpace(source.Isrc) &&
            !string.IsNullOrWhiteSpace(candidate.Isrc) &&
            string.Equals(source.Isrc.Trim(), candidate.Isrc.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("ISRC exact match");
            return (1.0, reasons);
        }

        var titleSim = Similarity(source.Title, candidate.Title);
        var artistSim = Similarity(source.Artist, candidate.Artist);
        var albumSim = Similarity(source.Album, candidate.Album);

        var hasAlbum = !string.IsNullOrWhiteSpace(source.Album) && !string.IsNullOrWhiteSpace(candidate.Album);
        double score;
        if (hasAlbum)
        {
            score = titleSim * 0.45 + artistSim * 0.35 + albumSim * 0.15;
            reasons.Add($"title={titleSim:F2} artist={artistSim:F2} album={albumSim:F2}");
        }
        else
        {
            score = titleSim * 0.55 + artistSim * 0.45;
            reasons.Add($"title={titleSim:F2} artist={artistSim:F2} (no album)");
        }

        if (source.DurationMs is int sd && candidate.DurationMs is int cd && sd > 0 && cd > 0)
        {
            var diffSec = Math.Abs(sd - cd) / 1000.0;
            if (diffSec <= 5)
            {
                score = Math.Min(1.0, score + 0.05);
                reasons.Add($"duration within 5s ({diffSec:F1}s)");
            }
            else if (diffSec <= 15)
            {
                reasons.Add($"duration within 15s ({diffSec:F1}s)");
            }
            else
            {
                score = Math.Max(0, score - 0.08);
                reasons.Add($"duration differs {diffSec:F1}s");
            }
        }

        var srcTags = source.VersionTags.Count > 0
            ? source.VersionTags
            : ExtractVersionTags(source.Title, source.Album);
        var candTags = candidate.VersionTags.Count > 0
            ? candidate.VersionTags
            : ExtractVersionTags(candidate.Title, candidate.Album);

        if (srcTags.Count > 0 || candTags.Count > 0)
        {
            var srcSet = new HashSet<string>(srcTags, StringComparer.OrdinalIgnoreCase);
            var candSet = new HashSet<string>(candTags, StringComparer.OrdinalIgnoreCase);
            if (srcSet.SetEquals(candSet))
            {
                score = Math.Min(1.0, score + 0.04);
                reasons.Add("version tags match");
            }
            else if (srcSet.Overlaps(candSet))
            {
                reasons.Add("version tags partial");
            }
            else if (srcSet.Count == 0 && candSet.Count > 0)
            {
                score = Math.Max(0, score - 0.12);
                reasons.Add("candidate is an alternate recording version");
            }
            else if (srcSet.Count > 0 && candSet.Count > 0)
            {
                score = Math.Max(0, score - 0.12);
                reasons.Add("version tags conflict");
            }
            else if (srcSet.Contains("live") && candSet.Count == 0)
            {
                score = Math.Max(0, score - 0.10);
                reasons.Add("source is live, candidate looks studio");
            }
        }

        return (Math.Clamp(score, 0, 1), reasons);
    }
}
