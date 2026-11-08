using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TuneBridge.Infrastructure.Spotify;

/// <summary>
/// Authorization Code + PKCE helpers for Spotify.
/// Client secret is not required for public native clients.
/// </summary>
public static class SpotifyPkce
{
    public const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
    public const string TokenEndpoint = "https://accounts.spotify.com/api/token";

    public static string GenerateCodeVerifier(int byteLength = 64)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Base64Url(bytes);
    }

    public static string ChallengeS256(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    public static string GenerateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string BuildAuthorizeUrl(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string state,
        IEnumerable<string> scopes)
    {
        var scope = Uri.EscapeDataString(string.Join(' ', scopes));
        return
            $"{AuthorizeEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&code_challenge_method=S256" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&scope={scope}";
    }

    public static async Task<SpotifyTokenResponse> ExchangeCodeAsync(
        HttpClient http,
        string clientId,
        string code,
        string codeVerifier,
        string redirectUri,
        string? clientSecret = null,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier
        };
        if (!string.IsNullOrEmpty(clientSecret))
            form["client_secret"] = clientSecret;

        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spotify token exchange failed ({(int)response.StatusCode}): {body}");

        return JsonSerializer.Deserialize<SpotifyTokenResponse>(body, JsonOptions)
               ?? throw new InvalidOperationException("Empty token response");
    }

    public static async Task<SpotifyTokenResponse> ClientCredentialsAsync(
        HttpClient http,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);

        using var response = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spotify client_credentials failed ({(int)response.StatusCode}): {body}");

        return JsonSerializer.Deserialize<SpotifyTokenResponse>(body, JsonOptions)
               ?? throw new InvalidOperationException("Empty token response");
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed class SpotifyTokenResponse
{
    public string access_token { get; set; } = "";
    public string token_type { get; set; } = "Bearer";
    public int expires_in { get; set; }
    public string? refresh_token { get; set; }
    public string? scope { get; set; }
}
