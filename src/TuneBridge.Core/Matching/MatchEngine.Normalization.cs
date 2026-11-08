using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TuneBridge.Core.Models;

namespace TuneBridge.Core.Matching;

public static partial class MatchEngine
{
    public const double MatchedThreshold = 0.88;
    public const double AmbiguousThreshold = 0.68;
    public const double ScoreGapForMatched = 0.035;

    private static readonly (Regex Pattern, string Tag)[] VersionPatterns =
    [
        (new Regex(@"\blive\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "live"),
        (new Regex(@"\bremix\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "remix"),
        (new Regex(@"\bremaster(?:ed)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "remastered"),
        (new Regex(@"\bacoustic\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "acoustic"),
        (new Regex(@"\binstrumental\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "instrumental"),
        (new Regex(@"\bradio\s*edit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "radio_edit"),
        (new Regex(@"\bexplicit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "explicit"),
        (new Regex(@"\bclean\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "clean"),
        (new Regex(@"\bdeluxe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "deluxe"),
        (new Regex(@"\bextended\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "extended"),
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var form = value.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(form.Length);
        foreach (var ch in form)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.ToLowerInvariant(ch));
        }

        var s = sb.ToString();
        s = Regex.Replace(s, @"\([^)]*(official|lyrics?|audio|video|visuali[sz]er|hd|4k|hq)[^)]*\)", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\[[^\]]*(official|lyrics?|audio|video|visuali[sz]er|hd|4k|hq)[^\]]*\]", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[^a-z0-9]+", " ");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static List<string> ExtractVersionTags(string? title, string? album = null)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in new[] { title, album })
        {
            if (string.IsNullOrEmpty(text))
                continue;
            foreach (var (pattern, tag) in VersionPatterns)
            {
                if (pattern.IsMatch(text))
                    tags.Add(tag);
            }
        }
        return tags.ToList();
    }

    public static double Similarity(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0)
            return 0.0;
        if (na == nb)
            return 1.0;

        var distance = Levenshtein(na, nb);
        var maxLen = Math.Max(na.Length, nb.Length);
        return 1.0 - (double)distance / maxLen;
    }

    private static int Levenshtein(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var j = 0; j <= m; j++)
            prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }
}
