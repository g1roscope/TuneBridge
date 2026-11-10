using System.Net.Http;
using TuneBridge.Infrastructure.Diagnostics;
using TuneBridge.Infrastructure.Spotify;
using Xunit;

namespace TuneBridge.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void Sanitizer_redacts_authorization_tokens_and_query_secrets()
    {
        var input = "Authorization: Bearer abc.def_123; access_token=secret-value " +
                    "https://example.test/callback?code=oauth-code&state=private-state&code_challenge=pkce-challenge " +
                    "Cookie: session=private-cookie";

        var sanitized = DiagnosticsLogger.Sanitize(input);

        Assert.DoesNotContain("abc.def_123", sanitized);
        Assert.DoesNotContain("secret-value", sanitized);
        Assert.DoesNotContain("oauth-code", sanitized);
        Assert.DoesNotContain("private-state", sanitized);
        Assert.DoesNotContain("pkce-challenge", sanitized);
        Assert.DoesNotContain("private-cookie", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
        Assert.Contains("state=[REDACTED]", sanitized);
    }

    [Fact]
    public void Diagnostic_errors_have_stable_codes_and_recovery_guidance()
    {
        var network = DiagnosticError.From(new HttpRequestException("offline"));
        var access = DiagnosticError.From(new UnauthorizedAccessException("denied"));
        var unknown = DiagnosticError.From(new InvalidOperationException("unexpected"));

        Assert.Equal("TB-503", network.Code);
        Assert.False(string.IsNullOrWhiteSpace(network.SuggestedAction));
        Assert.Equal("TB-401", access.Code);
        Assert.Equal("TB-999", unknown.Code);

        var partial = DiagnosticError.From(new SpotifyPartialTransferException(
            0, "The first batch outcome is unknown.", currentBatchOutcomeUnknown: true));
        Assert.Equal("TB-207", partial.Code);
        Assert.Contains("before retrying", partial.SuggestedAction);
    }
}
