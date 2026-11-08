using System.Net;
using System.Text;

namespace TuneBridge.Infrastructure.Spotify;

/// <summary>
/// Listens on 127.0.0.1 for a single OAuth redirect. Validates state and returns the authorization code.
/// RedirectUri is the exact URI registered with Spotify and used in token exchange (byte-for-byte match).
/// </summary>
public sealed class LoopbackHttpListener : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly string _expectedState;

    /// <summary>Exact redirect URI for authorize + token exchange (no forced trailing slash).</summary>
    public string RedirectUri { get; }

    /// <summary>
    /// Create listener for a specific redirect URI (e.g. http://127.0.0.1:43821/callback).
    /// HttpListener prefix always ends with '/' as required by the API; RedirectUri stays exact.
    /// </summary>
    public LoopbackHttpListener(string redirectUri, string expectedState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            (uri.Host != "127.0.0.1" && uri.Host != "localhost" && uri.Host != "[::1]"))
        {
            throw new ArgumentException(
                "Redirect URI must be an absolute loopback URL (http://127.0.0.1:... or http://[::1]:...).",
                nameof(redirectUri));
        }

        _expectedState = expectedState;
        RedirectUri = redirectUri.TrimEnd('/');

        var path = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
            ? "/callback/"
            : uri.AbsolutePath.TrimEnd('/') + "/";
        var prefix = $"{uri.Scheme}://{uri.Host}:{uri.Port}{path}";

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
    }

    public LoopbackHttpListener(int port, string expectedState, string path = "/callback")
        : this($"http://127.0.0.1:{port}{NormalizePath(path)}", expectedState)
    {
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/callback";
        if (!path.StartsWith('/'))
            path = "/" + path;
        return path.TrimEnd('/');
    }

    public void Start() => _listener.Start();

    public string ListeningPrefix => _listener.Prefixes.First();

    public async Task<string> WaitForCodeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_listener.IsListening)
            _listener.Start();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync().WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"OAuth callback timed out after {timeout.TotalSeconds:0}s. Complete login in the browser or cancel.");
        }

        var request = context.Request;
        var response = context.Response;

        try
        {
            if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHtmlAsync(response, 405, "Method not allowed", "Use the browser login flow.").ConfigureAwait(false);
                throw new InvalidOperationException("OAuth callback received a non-GET request.");
            }

            var query = ParseQuery(request.Url?.Query);
            if (query.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
            {
                query.TryGetValue("error_description", out var desc);
                desc = string.IsNullOrEmpty(desc) ? error : desc;
                await WriteHtmlAsync(response, 200, "Login cancelled",
                    $"Spotify returned: {WebUtility.HtmlEncode(desc)}. You can close this window.").ConfigureAwait(false);
                throw new OperationCanceledException($"User denied or error from Spotify: {desc}");
            }

            query.TryGetValue("state", out var state);
            if (!string.Equals(state, _expectedState, StringComparison.Ordinal))
            {
                await WriteHtmlAsync(response, 400, "Invalid state",
                    "Security check failed (state mismatch). Close this window and try again.").ConfigureAwait(false);
                throw new InvalidOperationException("OAuth state mismatch — possible CSRF. Aborting.");
            }

            query.TryGetValue("code", out var code);
            if (string.IsNullOrEmpty(code))
            {
                await WriteHtmlAsync(response, 400, "Missing code",
                    "Authorization code was not returned. Close this window and try again.").ConfigureAwait(false);
                throw new InvalidOperationException("OAuth callback missing authorization code.");
            }

            await WriteHtmlAsync(response, 200, "TuneBridge connected",
                "You can close this window and return to the application.").ConfigureAwait(false);
            return code;
        }
        finally
        {
            response.Close();
        }
    }

    public static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
            return result;
        if (query.StartsWith('?'))
            query = query[1..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                result[Uri.UnescapeDataString(part)] = "";
                continue;
            }
            var key = Uri.UnescapeDataString(part[..eq]);
            var value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    public static string CanonicalRedirectUri(string uri)
    {
        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var u))
            throw new ArgumentException("Invalid redirect URI", nameof(uri));
        var path = u.AbsolutePath.TrimEnd('/');
        if (string.IsNullOrEmpty(path))
            path = "";
        return $"{u.Scheme}://{u.Host.ToLowerInvariant()}:{u.Port}{path}";
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, int status, string title, string body)
    {
        var html = $"""
            <!DOCTYPE html>
            <html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title></head>
            <body style=\"font-family:system-ui;background:#121212;color:#fff;padding:2rem\">
            <h1 style=\"color:#1DB954\">{WebUtility.HtmlEncode(title)}</h1>
            <p>{body}</p>
            </body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_listener.IsListening)
                _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }
        return ValueTask.CompletedTask;
    }
}
