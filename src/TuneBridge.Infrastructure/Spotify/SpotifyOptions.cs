namespace TuneBridge.Infrastructure.Spotify;

/// <summary>
/// Runtime configuration. Populate from environment or user settings — never hard-code secrets.
/// For desktop PKCE public clients, ClientSecret may be null.
/// </summary>
public sealed class SpotifyOptions
{
    public string? ClientId { get; set; }
    /// <summary>Only for confidential flows; prefer PKCE public client for Desktop.</summary>
    public string? ClientSecret { get; set; }
    public string RedirectUri { get; set; } = "http://127.0.0.1:43821/callback";
    public string[] DefaultScopes { get; set; } =
    [
        "playlist-read-private",
        "playlist-read-collaborative",
        "playlist-modify-public",
        "playlist-modify-private"
    ];
}
