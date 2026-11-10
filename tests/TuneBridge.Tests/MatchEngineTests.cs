using TuneBridge.Core.Matching;
using TuneBridge.Core.Models;
using Xunit;

namespace TuneBridge.Tests;

public class MatchEngineTests
{
    [Fact]
    public void Exact_ISRC_wins_even_if_titles_differ()
    {
        var a = new Track { Title = "Different", Artist = "A", Isrc = "USABC1234567" };
        var b = new Track { Title = "Other Title", Artist = "B", Isrc = "USABC1234567" };
        var result = MatchEngine.FindMatch(a, [b]);
        Assert.Equal(MatchStatus.Matched, result.Status);
        Assert.Equal(1.0, result.Score);
        Assert.Contains(result.Reasons, r => r.Contains("ISRC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Isrc_case_insensitive()
    {
        var a = new Track { Title = "X", Artist = "Y", Isrc = "usabc1234567" };
        var b = new Track { Title = "X", Artist = "Y", Isrc = "USABC1234567" };
        Assert.Equal(MatchStatus.Matched, MatchEngine.FindMatch(a, [b]).Status);
    }

    [Fact]
    public void Exact_title_artist_matches()
    {
        var a = new Track
        {
            Title = "Midnight City",
            Artist = "M83",
            Album = "Hurry Up, We're Dreaming",
            DurationMs = 240000
        };
        var b = new Track
        {
            Title = "Midnight City",
            Artist = "M83",
            Album = "Hurry Up We're Dreaming",
            DurationMs = 240300
        };
        var result = MatchEngine.FindMatch(a, [b]);
        Assert.Equal(MatchStatus.Matched, result.Status);
        Assert.True(result.Score >= MatchEngine.MatchedThreshold);
    }

    [Fact]
    public void Empty_candidates_is_missing()
    {
        var a = new Track { Title = "Song", Artist = "Artist" };
        var result = MatchEngine.FindMatch(a, Array.Empty<Track>());
        Assert.Equal(MatchStatus.Missing, result.Status);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void Missing_metadata_still_scores_title_artist()
    {
        var a = new Track { Title = "Only Title", Artist = "Only Artist" };
        var b = new Track { Title = "Only Title", Artist = "Only Artist" };
        var result = MatchEngine.FindMatch(a, [b]);
        Assert.True(result.Score >= MatchEngine.MatchedThreshold);
        Assert.Equal(MatchStatus.Matched, result.Status);
    }

    [Fact]
    public void Ambiguous_when_two_close_candidates()
    {
        var source = new Track { Title = "Song", Artist = "Band" };
        var c1 = new Track { Title = "Song", Artist = "Band" };
        var c2 = new Track { Title = "Song", Artist = "Band" };
        var result = MatchEngine.FindMatch(source, [c1, c2]);
        Assert.Equal(MatchStatus.Ambiguous, result.Status);
    }

    [Fact]
    public void Low_score_is_missing()
    {
        var source = new Track { Title = "Completely Different Song", Artist = "Unknown Act" };
        var cand = new Track { Title = "XYZ", Artist = "ABC" };
        var result = MatchEngine.FindMatch(source, [cand]);
        Assert.Equal(MatchStatus.Missing, result.Status);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void Normalize_strips_official_video_noise()
    {
        var a = MatchEngine.Normalize("Song Title (Official Video)");
        var b = MatchEngine.Normalize("Song Title");
        Assert.Equal(b, a);
    }

    [Fact]
    public void Normalize_strips_bracket_noise()
    {
        var a = MatchEngine.Normalize("Song [Official Audio]");
        var b = MatchEngine.Normalize("Song");
        Assert.Equal(b, a);
    }

    [Fact]
    public void Normalize_preserves_cyrillic()
    {
        Assert.Equal("моя любовь", MatchEngine.Normalize("Моя любовь"));
    }

    [Fact]
    public void Normalize_preserves_mixed_cyrillic_and_latin()
    {
        Assert.Equal("ночной city 2", MatchEngine.Normalize("Ночной City 2"));
    }

    [Fact]
    public void Normalize_removes_latin_diacritics_without_losing_cyrillic()
    {
        Assert.Equal("cafe любовь", MatchEngine.Normalize("Café Любовь"));
    }

    [Fact]
    public void Similarity_matches_russian_titles()
    {
        Assert.Equal(1.0, MatchEngine.Similarity("Моя любовь", "моя любовь"));
        Assert.True(MatchEngine.Similarity("Моя любовь", "Моя любофф") > 0.75);
    }

    [Fact]
    public void ExtractVersionTags_detects_live()
    {
        var tags = MatchEngine.ExtractVersionTags("Song Title (Live at Wembley)");
        Assert.Contains("live", tags);
    }

    [Fact]
    public void ExtractVersionTags_detects_russian_remix_and_live()
    {
        var tags = MatchEngine.ExtractVersionTags("Песня (Ремикс)", "Концертная версия");
        Assert.Contains("remix", tags);
        Assert.Contains("live", tags);
    }

    [Fact]
    public void ExtractVersionTags_detects_russian_acoustic_and_instrumental()
    {
        Assert.Contains("acoustic", MatchEngine.ExtractVersionTags("Акустическая версия"));
        Assert.Contains("instrumental", MatchEngine.ExtractVersionTags("Инструментальная версия"));
    }

    [Fact]
    public void Live_vs_studio_penalty()
    {
        var src = new Track { Title = "Song", Artist = "Artist", VersionTags = ["live"] };
        var studio = new Track { Title = "Song", Artist = "Artist", VersionTags = [] };
        var live = new Track { Title = "Song", Artist = "Artist", VersionTags = ["live"] };
        var (scoreStudio, _) = MatchEngine.ScoreTrack(src, studio);
        var (scoreLive, _) = MatchEngine.ScoreTrack(src, live);
        Assert.True(scoreLive > scoreStudio, $"live={scoreLive} studio={scoreStudio}");
    }

    [Fact]
    public void Only_remix_candidate_is_classified_as_alternate_version()
    {
        var source = new Track { Title = "Моя любовь", Artist = "Исполнитель" };
        var remix = new Track { Title = "Моя любовь (Ремикс)", Artist = "Исполнитель" };

        var result = MatchEngine.FindMatch(source, [remix]);

        Assert.Equal(MatchStatus.AlternateVersion, result.Status);
        Assert.Equal(remix, result.Candidate);
        Assert.Contains(result.Reasons, r => r.Contains("alternate recording versions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ordinary_candidate_is_preferred_over_remix()
    {
        var source = new Track { Title = "Моя любовь", Artist = "Исполнитель" };
        var remix = new Track { Title = "Моя любовь (Ремикс)", Artist = "Исполнитель" };
        var original = new Track { Title = "Моя любовь", Artist = "Исполнитель" };

        var result = MatchEngine.FindMatch(source, [remix, original]);

        Assert.Equal(MatchStatus.Matched, result.Status);
        Assert.Equal(original, result.Candidate);
    }

    [Theory]
    [InlineData("live", "studio")]
    [InlineData("remix", "live")]
    [InlineData("acoustic", "instrumental")]
    public void Version_mismatch_is_never_auto_selected(string requestedVersion, string availableVersion)
    {
        var source = new Track
        {
            Title = "Одна и та же песня",
            Artist = "Исполнитель",
            VersionTags = [requestedVersion]
        };
        var candidate = new Track
        {
            Title = "Одна и та же песня",
            Artist = "Исполнитель",
            VersionTags = availableVersion == "studio" ? [] : [availableVersion]
        };

        var result = MatchEngine.FindMatch(source, [candidate]);

        Assert.Equal(MatchStatus.AlternateVersion, result.Status);
        Assert.Equal(candidate, result.Candidate);
        Assert.Contains(result.Reasons, reason =>
            reason.Contains("matching recording-version metadata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Exact_requested_version_is_still_auto_matched()
    {
        var source = new Track
        {
            Title = "Песня",
            Artist = "Исполнитель",
            VersionTags = ["live"]
        };
        var candidate = new Track
        {
            Title = "Песня",
            Artist = "Исполнитель",
            VersionTags = ["live"]
        };

        var result = MatchEngine.FindMatch(source, [candidate]);

        Assert.Equal(MatchStatus.Matched, result.Status);
        Assert.Equal(candidate, result.Candidate);
    }

    [Fact]
    public void Duration_far_apart_reduces_score()
    {
        var a = new Track { Title = "Same", Artist = "Same", DurationMs = 180_000 };
        var close = new Track { Title = "Same", Artist = "Same", DurationMs = 182_000 };
        var far = new Track { Title = "Same", Artist = "Same", DurationMs = 300_000 };
        var (sClose, _) = MatchEngine.ScoreTrack(a, close);
        var (sFar, _) = MatchEngine.ScoreTrack(a, far);
        Assert.True(sClose > sFar);
    }
}
