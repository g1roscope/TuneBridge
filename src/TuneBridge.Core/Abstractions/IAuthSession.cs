namespace TuneBridge.Core.Abstractions;

/// <summary>User authorization session for providers that require OAuth.</summary>
public interface IAuthSession
{
    string ProviderId { get; }
    bool IsAuthenticated { get; }
    DateTimeOffset? AccessTokenExpiresAt { get; }
}

/// <summary>
/// Starts and completes OAuth Authorization Code + PKCE.
/// Client secret must never be embedded in the desktop binary for public clients;
/// use PKCE with a public client id only, or a minimal token-exchange helper the user controls.
/// </summary>
public interface IOauthPkceFlow
{
    string ProviderId { get; }

    /// <summary>Build authorize URL and return (url, codeVerifier, state) for the system browser.</summary>
    (string AuthorizeUrl, string CodeVerifier, string State) BeginAuthorization(string redirectUri, IEnumerable<string> scopes);

    /// <summary>Exchange authorization code for tokens using PKCE verifier.</summary>
    Task CompleteAuthorizationAsync(string authorizationCode, string codeVerifier, string redirectUri, CancellationToken cancellationToken = default);
}
