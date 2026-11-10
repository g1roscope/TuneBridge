using System.Text;
using System.Text.RegularExpressions;
using TuneBridge.Infrastructure.Storage;

namespace TuneBridge.Infrastructure.Diagnostics;

/// <summary>
/// Writes local, best-effort diagnostic records. Never records request headers or bodies.
/// </summary>
public static partial class DiagnosticsLogger
{
    private static readonly object Sync = new();
    private static bool _retentionChecked;

    public static string LogDirectory => Path.Combine(JsonFileStore.GetAppDataDirectory(), "logs");

    public static void Write(Exception exception, string stage, string? provider = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var diagnostic = DiagnosticError.From(exception);
        var entry = new StringBuilder()
            .AppendLine($"[{DateTimeOffset.UtcNow:O}] {diagnostic.Code}")
            .AppendLine($"Stage: {Sanitize(stage)}")
            .AppendLine($"Provider: {Sanitize(provider ?? "unknown")}")
            .AppendLine($"Summary: {diagnostic.Summary}")
            .AppendLine($"Action: {diagnostic.SuggestedAction}")
            .AppendLine($"Exception: {Sanitize(exception.ToString())}")
            .AppendLine(new string('-', 72))
            .ToString();

        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (Sync)
            {
                if (!_retentionChecked)
                {
                    CleanupOldLogs();
                    _retentionChecked = true;
                }
            }
            var path = Path.Combine(LogDirectory, $"tunebridge-{DateTime.UtcNow:yyyyMMdd}.log");
            lock (Sync)
                File.AppendAllText(path, entry, Encoding.UTF8);
        }
        catch (Exception)
        {
            // Diagnostics must never replace the original application error.
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "tunebridge-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch (Exception)
        {
            // Log retention is best-effort and must not interrupt the application.
        }
    }

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var result = BearerTokenRegex().Replace(value, "$1[REDACTED]");
        result = SecretAssignmentRegex().Replace(result, "$1[REDACTED]");
        result = QuerySecretRegex().Replace(result, "$1[REDACTED]");
        return CookieHeaderRegex().Replace(result, "$1[REDACTED]");
    }

    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9._~+/-]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)((?:access[_-]?token|refresh[_-]?token|client[_-]?secret|code[_-]?verifier|authorization|cookie|x-auth-token)\s*[:=]\s*)[^\s&,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?i)([?&](?:access_token|refresh_token|token|code|state|code_challenge)=)[^&\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(@"(?i)((?:Set-Cookie|Cookie):\s*)[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex CookieHeaderRegex();
}