using System.Globalization;
using System.Text;
using System.Text.Json;
using TuneBridge.Core.Models;

namespace TuneBridge.Core.ImportExport;

/// <summary>
/// Imports and exports playlist files without relying on any music-service API.
/// Supported formats: JSON, CSV and M3U/M3U8.
/// </summary>
public static class PlaylistFileFormats
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static Playlist Import(string content, string fileExtension)
    {
        ArgumentNullException.ThrowIfNull(content);

        var extension = NormalizeExtension(fileExtension);
        content = content.TrimStart('\uFEFF');

        return extension switch
        {
            ".json" => ImportJson(content),
            ".csv" => ImportCsv(content),
            ".m3u" or ".m3u8" => ImportM3u(content),
            _ => throw new NotSupportedException($"Playlist format '{extension}' is not supported.")
        };
    }

    public static string Export(Playlist playlist, string fileExtension)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        var extension = NormalizeExtension(fileExtension);
        return extension switch
        {
            ".json" => JsonSerializer.Serialize(playlist, JsonOptions),
            ".csv" => ExportCsv(playlist),
            ".m3u" or ".m3u8" => ExportM3u(playlist),
            _ => throw new NotSupportedException($"Playlist format '{extension}' is not supported.")
        };
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            throw new ArgumentException("A file extension is required.", nameof(extension));

        var value = extension.Trim();
        if (!value.StartsWith('.'))
            value = "." + value;

        return value.ToLowerInvariant();
    }

    private static Playlist ImportJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var tracks = JsonSerializer.Deserialize<List<Track>>(content, JsonOptions) ?? [];
                SetPositions(tracks);
                return new Playlist { Tracks = tracks, SourceService = "file" };
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("JSON playlist must be an object or an array of tracks.");

            var playlist = JsonSerializer.Deserialize<Playlist>(content, JsonOptions)
                ?? throw new FormatException("The JSON playlist is empty.");

            playlist.Tracks ??= [];
            SetPositions(playlist.Tracks);
            playlist.SourceService = string.IsNullOrWhiteSpace(playlist.SourceService)
                ? "file"
                : playlist.SourceService;
            return playlist;
        }
        catch (JsonException exception)
        {
            throw new FormatException("The JSON file is not a valid TuneBridge playlist.", exception);
        }
    }

    private static Playlist ImportCsv(string content)
    {
        var rows = ParseCsvRows(content);
        if (rows.Count == 0)
            return new Playlist { SourceService = "file" };

        var delimiter = DetectDelimiter(rows[0]);
        var header = ParseCsvRow(rows[0], delimiter)
            .Select((name, index) => (Name: NormalizeHeader(name), Index: index))
            .Where(x => x.Name.Length > 0)
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var hasHeader = header.ContainsKey("title") || header.ContainsKey("track") || header.ContainsKey("name") || header.ContainsKey("название") || header.ContainsKey("наименование") || header.ContainsKey("trackname");
        var start = hasHeader ? 1 : 0;
        if (!hasHeader)
        {
            header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = 0,
                ["artist"] = 1,
                ["album"] = 2,
                ["duration_ms"] = 3,
                ["isrc"] = 4,
                ["url"] = 5
            };
        }

        var tracks = new List<Track>();
        for (var rowIndex = start; rowIndex < rows.Count; rowIndex++)
        {
            var cells = ParseCsvRow(rows[rowIndex], delimiter);
            if (cells.All(string.IsNullOrWhiteSpace))
                continue;

            var title = GetCell(cells, header, "title", "track", "name", "track name", "название", "наименование");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var durationMs = ParseDurationMs(GetCell(cells, header, "duration_ms", "durationms", "duration (ms)", "длительность мс", "длительность (мс)"));
            if (durationMs is null)
            {
                var durationValue = GetCell(cells, header, "duration", "duration_seconds", "длительность", "время");
                if (durationValue.Contains(':'))
                    durationMs = ParseDurationMs(durationValue);
                if (durationMs is null)
                {
                    var seconds = ParseNumber(durationValue);
                    if (seconds is > 0 && seconds.Value <= int.MaxValue / 1000d)
                        durationMs = (int)Math.Round(seconds.Value * 1000, MidpointRounding.AwayFromZero);
                }
            }

            tracks.Add(new Track
            {
                Title = title.Trim(),
                Artist = GetCell(cells, header, "artist", "artists", "artist name(s)", "исполнитель", "исполнители", "артист").Trim(),
                Album = GetCell(cells, header, "album", "album name", "альбом").Trim(),
                DurationMs = durationMs,
                Isrc = NullIfWhiteSpace(GetCell(cells, header, "isrc")),
                SourceUrl = NullIfWhiteSpace(GetCell(cells, header, "url", "source_url", "spotify_url", "track_url", "track uri", "ссылка", "ссылка на трек", "uri")),
                Position = tracks.Count
            });
        }

        return new Playlist { Tracks = tracks, SourceService = "file" };
    }

    private static Playlist ImportM3u(string content)
    {
        var playlist = new Playlist { SourceService = "file" };
        string? pendingTitle = null;
        int? pendingDurationMs = null;

        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#PLAYLIST:", StringComparison.OrdinalIgnoreCase))
            {
                var name = line["#PLAYLIST:".Length..].Trim();
                if (name.Length > 0)
                    playlist.Name = name;
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                if (comma < 0)
                {
                    pendingTitle = null;
                    pendingDurationMs = null;
                    continue;
                }

                var metadata = line["#EXTINF:".Length..comma].Trim();
                var secondsText = metadata.Split(',', StringSplitOptions.TrimEntries)[0];
                if (double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && seconds >= 0 && seconds <= int.MaxValue / 1000d)
                    pendingDurationMs = (int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
                else
                    pendingDurationMs = null;

                pendingTitle = line[(comma + 1)..].Trim();
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            var (artist, title) = SplitArtistTitle(pendingTitle ?? string.Empty);
            playlist.Tracks.Add(new Track
            {
                Artist = artist,
                Title = title.Length > 0 ? title : line,
                DurationMs = pendingDurationMs,
                SourceUrl = line,
                Position = playlist.Tracks.Count
            });
            pendingTitle = null;
            pendingDurationMs = null;
        }

        return playlist;
    }

    private static string ExportCsv(Playlist playlist)
    {
        var builder = new StringBuilder();
        builder.AppendLine("position,title,artist,album,duration_ms,isrc,url");

        foreach (var track in playlist.Tracks)
        {
            builder.Append(track.Position.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(track.Title)).Append(',')
                .Append(Csv(track.Artist)).Append(',')
                .Append(Csv(track.Album)).Append(',')
                .Append(track.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(Csv(track.Isrc ?? string.Empty)).Append(',')
                .Append(Csv(track.SourceUrl ?? string.Empty)).AppendLine();
        }

        return builder.ToString();
    }

    private static string ExportM3u(Playlist playlist)
    {
        if (playlist.Tracks.Any(track => string.IsNullOrWhiteSpace(track.SourceUrl)))
            throw new InvalidOperationException("M3U export requires a URL or file path for every track. Use JSON or CSV to preserve tracks without a source location.");

        var builder = new StringBuilder("#EXTM3U\n");
        if (!string.IsNullOrWhiteSpace(playlist.Name))
            builder.Append("#PLAYLIST:").Append(playlist.Name.Replace("\r", " ").Replace("\n", " ")).Append('\n');

        foreach (var track in playlist.Tracks)
        {
            if (track.DurationMs is > 0)
            {
                var seconds = track.DurationMs.Value / 1000d;
                builder.Append("#EXTINF:")
                    .Append(seconds.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(FormatArtistTitle(track))
                    .Append('\n');
            }
            else
            {
                builder.Append("#EXTINF:-1,").Append(FormatArtistTitle(track)).Append('\n');
            }

            builder.Append(track.SourceUrl ?? string.Empty).Append('\n');
        }

        return builder.ToString();
    }

    private static List<string> ParseCsvRows(string content)
    {
        // Keep physical lines together when a quoted field contains a newline.
        var rows = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            current.Append(ch);

            if (ch == '"')
            {
                if (inQuotes && i + 1 < content.Length && content[i + 1] == '"')
                {
                    current.Append(content[++i]);
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if ((ch == '\r' || ch == '\n') && !inQuotes)
            {
                current.Length--;
                if (current.Length > 0)
                    rows.Add(current.ToString());
                current.Clear();
                if (ch == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                    i++;
            }
        }

        if (current.Length > 0)
            rows.Add(current.ToString());

        if (inQuotes)
            throw new FormatException("CSV contains an unterminated quoted field.");

        return rows;
    }

    private static char DetectDelimiter(string header)
    {
        var candidates = new[] { ',', ';', '\t' };
        return candidates
            .Select(delimiter => (Delimiter: delimiter, Count: ParseCsvRow(header, delimiter).Count))
            .OrderByDescending(x => x.Count)
            .First().Delimiter;
    }

    private static List<string> ParseCsvRow(string row, char delimiter)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < row.Length; i++)
        {
            var ch = row[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < row.Length && row[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == delimiter && !inQuotes)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(ch);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }

    private static string NormalizeHeader(string value) =>
        new(value.Trim().TrimStart('\uFEFF').ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string GetCell(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> header, params string[] names)
    {
        foreach (var name in names)
        {
            if (header.TryGetValue(NormalizeHeader(name), out var index) && index >= 0 && index < cells.Count)
                return cells[index];
        }

        return string.Empty;
    }

    private static int? ParseDurationMs(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0)
            return milliseconds;

        var parts = value.Split(':');
        if (parts.Length is 2 or 3)
        {
            var numbers = parts.Select(part => ParseNumber(part)).ToArray();
            if (numbers.All(number => number.HasValue) &&
                numbers[^1] is >= 0 and < 60 &&
                (parts.Length == 2 || numbers[1] is >= 0 and < 60))
            {
                var totalSeconds = parts.Length == 2
                    ? numbers[0]!.Value * 60 + numbers[1]!.Value
                    : numbers[0]!.Value * 3600 + numbers[1]!.Value * 60 + numbers[2]!.Value;
                if (totalSeconds > 0 && totalSeconds <= int.MaxValue / 1000d)
                    return (int)Math.Round(totalSeconds * 1000, MidpointRounding.AwayFromZero);
            }
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration) &&
            duration > TimeSpan.Zero && duration.TotalMilliseconds <= int.MaxValue)
            return (int)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero);

        return null;
    }

    private static double? ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string Csv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static (string Artist, string Title) SplitArtistTitle(string value)
    {
        var separator = value.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0
            ? (value[..separator].Trim(), value[(separator + 3)..].Trim())
            : (string.Empty, value.Trim());
    }

    private static string FormatArtistTitle(Track track) =>
        string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Artist} - {track.Title}";

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SetPositions(IReadOnlyList<Track> tracks)
    {
        for (var i = 0; i < tracks.Count; i++)
            tracks[i].Position = i;
    }
}
