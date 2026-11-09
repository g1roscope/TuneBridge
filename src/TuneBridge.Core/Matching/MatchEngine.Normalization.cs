using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TuneBridge.Core.Models;

namespace TuneBridge.Core.Matching;

public static partial class MatchEngine
{
    // Thresholds: high confidence vs needs review
    public const double MatchedThreshold = 0.88;
    public const double AmbiguousThreshold = 0.68;
    public const double ScoreGapForMatched = 0.035;

    private static readonly (Regex Pattern, string Tag)[] VersionPatterns =
    [
        // English tags
        (new Regex(@"\blive\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "live"),
        (new Regex(@"\bremix\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "remix"),
        (new Regex(@"\bremaster(?:ed)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "remastered"),
        (new Regex(@"\bacoustic\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "acoustic"),
        (new Regex(@"\binstrumental\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "instrumental"),
        (new Regex(@"\bradio\s*edit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "radio_edit"),
        (new Regex(@"\bexplicit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "explicit"),
        (new Regex(@"\bclean\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "clean"),
        (new Regex(@"\bdeluxe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "deluxe"),
        (new Regex(@"\bextended\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "extended"),
        (new Regex(@"\bslowed(?:\s*\+?\s*reverb)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "slowed"),
        (new Regex(@"\bsped\s*up\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "sped_up"),
        (new Regex(@"\bnightcore\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "nightcore"),

        // Russian tags and common mixed-language spellings
        (new Regex(@"\bлайв\b|\bконцертн(?:ая|ый|ое)\s+верси[яию]\b|\bвживую\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "live"),
        (new Regex(@"\bремикс\b|\bремикс(?:овая|овый)\s+верси[яию]\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "remix"),
        (new Regex(@"\bремастер(?:инг)?\b|\bремастированн(?:ая|ый|ое)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "remastered"),
        (new Regex(@"\bакустика\b|\bакустическ(?:ая|ий|ое)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "acoustic"),
        (new Regex(@"\bинструментал(?:ьная|ьный)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "instrumental"),
        (new Regex(@"\bрадио\s*верси[яию]\b|\bрадио\s*эдит\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "radio_edit"),
        (new Regex(@"\bделюкс\b|\bрасширенн(?:ая|ый)\s+верси[яию]\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "deluxe"),
        (new Regex(@"\bзамедленн(?:ая|ый)\b|\bслоуд\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "slowed"),
        (new Regex(@"\bускоренн(?:ая|ый)\b|\bспед\s*ап\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "sped_up"),
    ];

    /// <summary>
    /// Normalize text for comparison. NFKD removes diacritics while Unicode letter/number
    /// categories preserve Cyrillic and other non-Latin scripts.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        const char preservedCyrillicShortI = '\uE000';
        // NFKD decomposes Cyrillic й into и + breve. Keep й intact while still
        // removing Latin diacritics such as the accent in café.
        var form = value.Replace('й', preservedCyrillicShortI).Replace('Й', preservedCyrillicShortI)
            .Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(form.Length);
        foreach (var ch in form)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark ||
                category == UnicodeCategory.SpacingCombiningMark ||
                category == UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        var normalized = sb.ToString().Replace(preservedCyrillicShortI, 'й');
        normalized = Regex.Replace(
            normalized,
            @"\([^)]*(official|lyrics?|audio|video|visuali[sz]er|hd|4k|hq)[^)]*\)",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\[[^\]]*(official|lyrics?|audio|video|visuali[sz]er|hd|4k|hq)[^\]]*\]",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Keep letters and digits from every Unicode script (not only a-z).
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ");
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static List<string> ExtractVersionTags(string? title, string? album = null)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in new[] { title, album })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            foreach (var (pattern, tag) in VersionPatterns)
            {
                if (pattern.IsMatch(text))
                    tags.Add(tag);
            }
        }

        return tags.OrderBy(tag => tag, StringComparer.Ordinal).ToList();
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
