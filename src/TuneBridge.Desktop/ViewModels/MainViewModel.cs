using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows.Input;
using TuneBridge.Core.Matching;
using TuneBridge.Core.Models;
using TuneBridge.Infrastructure.Security;
using TuneBridge.Infrastructure.Spotify;
using TuneBridge.Infrastructure.Zvuk;

namespace TuneBridge.Desktop.ViewModels;

/// <summary>
/// Main window VM. Spotify:
///   TUNEBRIDGE_SPOTIFY_CLIENT_ID — required for PKCE (public client, no secret in binary).
///   TUNEBRIDGE_SPOTIFY_CLIENT_SECRET — optional; only for limited client_credentials public metadata.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly HttpClient _http = new();
    private readonly SpotifyOptions _options;
    private readonly SpotifyAuthSession _auth;
    private readonly SpotifyProvider _spotify;

    private string _playlistUrl = "";
    private string _status = "Ready. Connect Spotify (PKCE) or paste a playlist URL.";
    private string _authStatus = "Not connected";
    private string _destination = "spotify";
    private bool _isBusy;
    private bool _isAuthenticated;

    public ObservableCollection<MatchRow> Results { get; } = new();

    public string PlaylistUrl
    {
        get => _playlistUrl;
        set { _playlistUrl = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string AuthStatus
    {
        get => _authStatus;
        set { _authStatus = value; OnPropertyChanged(); }
    }

    public string Destination
    {
        get => _destination;
        set { _destination = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            (ConnectSpotifyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectSpotifyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ImportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            _isAuthenticated = value;
            OnPropertyChanged();
            (ConnectSpotifyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectSpotifyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public ICommand ImportCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand ConnectSpotifyCommand { get; }
    public ICommand DisconnectSpotifyCommand { get; }

    public MainViewModel()
    {
        _options = new SpotifyOptions
        {
            ClientId = Environment.GetEnvironmentVariable("TUNEBRIDGE_SPOTIFY_CLIENT_ID"),
            ClientSecret = Environment.GetEnvironmentVariable("TUNEBRIDGE_SPOTIFY_CLIENT_SECRET"),
            RedirectUri = Environment.GetEnvironmentVariable("TUNEBRIDGE_SPOTIFY_REDIRECT_URI")
                          ?? "http://127.0.0.1:43821/callback"
        };

        ITokenStore store = CreateTokenStore();
        _auth = new SpotifyAuthSession(_http, _options, store);
        _spotify = new SpotifyProvider(_http, _options);

        ImportCommand = new RelayCommand(async () => await ImportAsync(), () => !IsBusy);
        ExportJsonCommand = new RelayCommand(ExportJson, () => Results.Count > 0);
        ConnectSpotifyCommand = new RelayCommand(async () => await ConnectSpotifyAsync(), () => !IsBusy && !IsAuthenticated);
        DisconnectSpotifyCommand = new RelayCommand(async () => await DisconnectSpotifyAsync(), () => !IsBusy && IsAuthenticated);

        _ = RestoreAuthOnStartupAsync();
    }

    private static ITokenStore CreateTokenStore()
    {
        if (OperatingSystem.IsWindows())
            return new DpapiTokenStore();
        return new InMemoryTokenStore();
    }

    private async Task RestoreAuthOnStartupAsync()
    {
        try
        {
            await _auth.RestoreAsync().ConfigureAwait(true);
            ApplyAuthToUi();
            if (_auth.IsAuthenticated)
                _auth.ApplyToProvider(_spotify);
        }
        catch (Exception ex)
        {
            Status = $"Could not restore Spotify session: {ex.Message}";
        }
    }

    private void ApplyAuthToUi()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        if (_auth.IsAuthenticated)
        {
            var name = string.IsNullOrWhiteSpace(_auth.DisplayName) ? "Spotify user" : _auth.DisplayName;
            var exp = _auth.AccessTokenExpiresAt?.ToLocalTime().ToString("g") ?? "?";
            AuthStatus = $"Connected as {name} (token until {exp})";
        }
        else
        {
            AuthStatus = "Not connected — use Connect Spotify (PKCE, no secret required)";
        }
    }

    private async Task ConnectSpotifyAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            Status = "Set TUNEBRIDGE_SPOTIFY_CLIENT_ID (public Client ID from developer.spotify.com). No Client Secret needed for PKCE.";
            return;
        }

        IsBusy = true;
        Status = "Opening browser for Spotify login… Complete login, then return here.";
        try
        {
            await _auth.LoginInteractiveAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            _auth.ApplyToProvider(_spotify);
            ApplyAuthToUi();
            Status = "Spotify connected. You can import playlists you own or collaborate on.";
        }
        catch (OperationCanceledException ex)
        {
            Status = $"Login cancelled or denied: {ex.Message}";
            ApplyAuthToUi();
        }
        catch (TimeoutException ex)
        {
            Status = $"Login timed out: {ex.Message}";
            ApplyAuthToUi();
        }
        catch (HttpRequestException ex)
        {
            Status = $"Network error during Spotify login: {ex.Message}";
            ApplyAuthToUi();
        }
        catch (Exception ex)
        {
            Status = $"Spotify login failed: {ex.Message}";
            ApplyAuthToUi();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectSpotifyAsync()
    {
        IsBusy = true;
        try
        {
            await _auth.LogoutAsync().ConfigureAwait(true);
            // Drop user OAuth on the provider; leave client_credentials app token intact if any
            _spotify.ClearUserTokens();
            ApplyAuthToUi();
            Status = "Disconnected from Spotify. Tokens removed from secure store.";
        }
        catch (Exception ex)
        {
            Status = $"Disconnect error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAsync()
    {
        IsBusy = true;
        Results.Clear();
        try
        {
            var zvuk = new ZvukProvider();
            var url = PlaylistUrl.Trim();
            var parsed = _spotify.ParsePlaylistUrl(url) ?? zvuk.ParsePlaylistUrl(url);
            if (parsed is null)
            {
                Status = "URL not recognized. Supported: Spotify playlist URL, Звук (parse only).";
                return;
            }

            if (parsed.ProviderId == "zvuk")
            {
                Status = "Звук: " + zvuk.GetCapabilities().Notes;
                return;
            }

            // Prefer user token so owned/collaborative playlist items are readable (API Feb 2026).
            if (_auth.IsAuthenticated || !string.IsNullOrEmpty(_auth.RefreshToken))
            {
                try
                {
                    await _auth.GetValidAccessTokenAsync().ConfigureAwait(true);
                    _auth.ApplyToProvider(_spotify);
                    ApplyAuthToUi();
                }
                catch (HttpRequestException ex)
                {
                    // Temporary network — keep session; surface error without logout
                    Status = $"Network error while refreshing Spotify token: {ex.Message}. Try again.";
                    ApplyAuthToUi();
                    return;
                }
                catch (Exception ex)
                {
                    Status = $"Token refresh failed — reconnect Spotify if this persists. {ex.Message}";
                    ApplyAuthToUi();
                    return;
                }
            }

            Status = "Fetching playlist from Spotify… (items only for owned/collaborative playlists since Feb 2026)";
            var playlist = await _spotify.GetPublicPlaylistAsync(parsed).ConfigureAwait(true);
            Status = $"Loaded {playlist.Tracks.Count} tracks — matching (not transferred yet)…";

            var index = 0;
            foreach (var track in playlist.Tracks)
            {
                // Refresh access token periodically during long imports (every 25 tracks)
                if (_auth.IsAuthenticated && index > 0 && index % 25 == 0)
                {
                    try
                    {
                        await _auth.GetValidAccessTokenAsync().ConfigureAwait(true);
                        _auth.ApplyToProvider(_spotify);
                    }
                    catch (HttpRequestException)
                    {
                        // Keep going with current token; surface at end if needed
                    }
                }

                IReadOnlyList<Track> candidates;
                try
                {
                    candidates = Destination == "zvuk"
                        ? Array.Empty<Track>()
                        : await _spotify.SearchTrackAsync(track).ConfigureAwait(true);
                }
                catch (HttpRequestException ex)
                {
                    Status = $"Network error during search (track #{index + 1}): {ex.Message}";
                    candidates = Array.Empty<Track>();
                }
                catch (Exception ex)
                {
                    Status = $"Search error: {ex.Message}";
                    candidates = Array.Empty<Track>();
                }

                var match = MatchEngine.FindMatch(track, candidates);
                Results.Add(new MatchRow(match));
                index++;
            }

            var matched = Results.Count(r => r.Status == MatchStatus.Matched.ToString());
            var ambiguous = Results.Count(r => r.Status == MatchStatus.Ambiguous.ToString());
            var missing = Results.Count(r => r.Status == MatchStatus.Missing.ToString());
            // Matching only — full playlist create/add is not completed by this command yet
            Status = $"Match preview: {Results.Count} tracks — matched={matched}, ambiguous={ambiguous}, missing={missing}. " +
                     "Playlist has NOT been created on the destination yet. Search limit=10 (API 2026).";
        }
        catch (HttpRequestException ex)
        {
            Status = $"Network error: {ex.Message}. Session was not cleared.";
            ApplyAuthToUi();
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ExportJson()
    {
        Status = $"Export: {Results.Count} rows ready (wire SaveFileDialog in view or service).";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
