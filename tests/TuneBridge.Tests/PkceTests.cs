using TuneBridge.Infrastructure.Spotify;

namespace TuneBridge.Tests;

public class PkceTests
{
    [Fact]
    public void Verifier_and_challenge_are_base64url()
    {
        var verifier = SpotifyPkce.GenerateCodeVerifier();
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);
        var challenge = SpotifyPkce.ChallengeS256(verifier);
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
    }

    [Fact]
    public void Authorize_url_contains_pkce_params()
    {
        var url = SpotifyPkce.BuildAuthorizeUrl(
            "client",
            "http://127.0.0.1:43821/callback",
            "challenge",
            "state123",
            ["playlist-read-private"]);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("code_challenge=challenge", url);
        Assert.Contains("state=state123", url);
        Assert.Contains("client_id=client", url);
    }
}
