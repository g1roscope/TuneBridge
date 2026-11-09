using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows.Input;
using System.Windows;
using TuneBridge.Core.ImportExport;
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
    private Playlist? _currentPlaylist;
    private CancellationTokenSource? _operationCts;

    private string _playlistUrl = "";
    private string _status = "Ready. Connect Spotify (PKCE) or paste a playlist URL.";
    private string _authStatus = "Not connected";
    private string _destination = "spotify";
    private bool _isBusy;
    private bool _isAuthenticated;
    private string _currentSection = "Transfer";
    private string _playlistNameDisplay = "";
    private int _matchedCount;
    private int _ambiguousCount;
    private int _missingCount;

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
            OnPropertyChanged(nameof(BusyVisibility));
            OnPropertyChanged(nameof(EmptyStateVisibility));
            (ConnectSpotifyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectSpotifyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ImportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ImportFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TransferCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportJsonCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            _isAuthenticated = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectButtonVisibility));
            OnPropertyChanged(nameof(DisconnectButtonVisibility));
            OnPropertyChanged(nameof(AuthStatusShort));
            OnPropertyChanged(nameof(SourceServiceSubtitle));
            (ConnectSpotifyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectSpotifyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TransferCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string CurrentSection
    {
        get => _currentSection;
        private set
        {
            if (_currentSection == value) return;
            _currentSection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SectionTitle));
            OnPropertyChanged(nameof(TransferSectionVisibility));
            OnPropertyChanged(nameof(HistorySectionVisibility));
            OnPropertyChanged(nameof(SettingsSectionVisibility));
        }
    }

    public string SectionTitle => CurrentSection switch
    {
        "History" => "History",
        "Settings" => "Settings",
        _ => "Transfer"
    };

    public System.Windows.Visibility TransferSectionVisibility =>
        CurrentSection == "Transfer" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility HistorySectionVisibility =>
        CurrentSection == "History" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility SettingsSectionVisibility =>
        CurrentSection == "Settings" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility ConnectButtonVisibility =>
        IsAuthenticated ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility DisconnectButtonVisibility =>
        IsAuthenticated ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility BusyVisibility =>
        IsBusy ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility ResultsVisibility =>
        Results.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility EmptyStateVisibility =>
        Results.Count == 0 && !IsBusy ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public string AuthStatusShort =>
        IsAuthenticated
            ? (string.IsNullOrWhiteSpace(_auth.DisplayName) ? "Spotify connected" : _auth.DisplayName!)
            : "Not connected";

    public string SourceServiceSubtitle =>
        _currentPlaylist?.SourceService switch
        {
            "file" => "Imported from local file",
            "spotify" => "Loaded from Spotify",
            _ => IsAuthenticated ? "Spotify connected" : "Connect Spotify to analyze"
        };

    public string PlaylistNameDisplay
    {
        get => _playlistNameDisplay;
        private set { _playlistNameDisplay = value; OnPropertyChanged(); }
    }

    public string ResultsSummary =>
        Results.Count == 0 ? "" : $"{Results.Count} tracks analyzed";

    public int MatchedCount
    {
        get => _matchedCount;
        private set { _matchedCount = value; OnPropertyChanged(); }
    }

    public int AmbiguousCount
    {
        get => _ambiguousCount;
        private set { _ambiguousCount = value; OnPropertyChanged(); }
    }

    public int MissingCount
    {
        get => _missingCount;
        private set { _missingCount = value; OnPropertyChanged(); }
    }

    public string TransferHint =>
        $"Only {MatchedCount} high-confidence matches will be added. Ambiguous ({AmbiguousCount}) and missing ({MissingCount}) tracks are skipped to avoid wrong songs.";

    public string AppVersion => "TuneBridge 2.0 · .NET 10";

    public string AboutText =>
        "TuneBridge transfers playlists between music services. " +
        "Spotify uses Authorization Code + PKCE (no client secret in the binary). " +
        "Since Spotify API February 2026, playlist items are only available for playlists you own or collaborate on. " +
        "Search limit is 10. Звук integration is experimental (parse only).";

    public ICommand ImportCommand { get; }
    public ICommand ImportFileCommand { get; }
    public ICommand TransferCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ConnectSpotifyCommand { get; }
    public ICommand DisconnectSpotifyCommand { get; }
    public ICommand NavigateTransferCommand { get; }
    public ICommand NavigateHistoryCommand { get; }
    public ICommand NavigateSettingsCommand { get; }

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
        ImportFileCommand = new RelayCommand(async () => await ImportFileAsync(), () => !IsBusy);
        TransferCommand = new RelayCommand(async () => await TransferAsync(), () => !IsBusy && IsAuthenticated && MatchedCount > 0);
        ExportJsonCommand = new RelayCommand(ExportPlaylist, () => !IsBusy && _currentPlaylist is not null);
        CancelCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy);
        ConnectSpotifyCommand = new RelayCommand(async () => await ConnectSpotifyAsync(), () => !IsBusy && !IsAuthenticated);
        DisconnectSpotifyCommand = new RelayCommand(async () => await DisconnectSpotifyAsync(), () => !IsBusy && IsAuthenticated);
        NavigateTransferCommand = new RelayCommand(() => CurrentSection = "Transfer");
        NavigateHistoryCommand = new RelayCommand(() => CurrentSection = "History");
        NavigateSettingsCommand = new RelayCommand(() => CurrentSection = "Settings");

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
        OnPropertyChanged(nameof(AuthStatusShort));
        OnPropertyChanged(nameof(SourceServiceSubtitle));
        OnPropertyChanged(nameof(ConnectButtonVisibility));
        OnPropertyChanged(nameof(DisconnectButtonVisibility));
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
        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        IsBusy = true;
        Results.Clear();
        PlaylistNameDisplay = "";
        MatchedCount = AmbiguousCount = MissingCount = 0;
        OnPropertyChanged(nameof(ResultsVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ResultsSummary));
        OnPropertyChanged(nameof(BusyVisibility));
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
            var playlist = await _spotify.GetPublicPlaylistAsync(parsed, cancellationToken).ConfigureAwait(true);
            PlaylistNameDisplay = string.IsNullOrWhiteSpace(playlist.Name) ? "Playlist" : playlist.Name;
            Status = $"Loaded {playlist.Tracks.Count} tracks — matching (not transferred yet)…";

            _currentPlaylist = playlist;
            OnPropertyChanged(nameof(SourceServiceSubtitle));
            await AnalyzeTracksAsync(playlist, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "Analysis cancelled. No destination playlist was created.";
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
            EndOperation();
        }
    }

    private async Task ImportFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import playlist",
            Filter = "Playlist files (*.json;*.csv;*.m3u;*.m3u8)|*.json;*.csv;*.m3u;*.m3u8|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        IsBusy = true;
        Results.Clear();
        PlaylistNameDisplay = "";
        MatchedCount = AmbiguousCount = MissingCount = 0;
        OnPropertyChanged(nameof(ResultsVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ResultsSummary));

        try
        {
            if (!_auth.IsAuthenticated && string.IsNullOrEmpty(_auth.RefreshToken))
                throw new InvalidOperationException("Connect Spotify first. A signed-in account is required to search and create the destination playlist.");

            await _auth.GetValidAccessTokenAsync().ConfigureAwait(true);
            _auth.ApplyToProvider(_spotify);
            ApplyAuthToUi();

            var content = await File.ReadAllTextAsync(dialog.FileName, cancellationToken).ConfigureAwait(true);
            var playlist = PlaylistFileFormats.Import(content, Path.GetExtension(dialog.FileName));
            if (playlist.Tracks.Count == 0)
                throw new InvalidOperationException("No tracks were found in this file.");

            _currentPlaylist = playlist;
            PlaylistNameDisplay = string.IsNullOrWhiteSpace(playlist.Name)
                ? Path.GetFileNameWithoutExtension(dialog.FileName)
                : playlist.Name;
            OnPropertyChanged(nameof(SourceServiceSubtitle));
            Status = $"Imported {playlist.Tracks.Count} tracks from {Path.GetFileName(dialog.FileName)}. Starting Spotify matching…";
            await AnalyzeTracksAsync(playlist, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "File import or analysis cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"File import failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            EndOperation();
        }
    }

    private async Task AnalyzeTracksAsync(Playlist playlist, CancellationToken cancellationToken)
    {
        Results.Clear();
        var index = 0;
        foreach (var track in playlist.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > 0 && index % 25 == 0 && !string.IsNullOrEmpty(_auth.RefreshToken))
            {
                try
                {
                    await _auth.GetValidAccessTokenAsync().ConfigureAwait(true);
                    _auth.ApplyToProvider(_spotify);
                }
                catch (HttpRequestException)
                {
                    // Search below will report the concrete API failure if the token expired.
                }
            }

            Status = $"Matching track {index + 1} of {playlist.Tracks.Count}: {track.Title}";
            IReadOnlyList<Track> candidates;
            try
            {
                candidates = Destination == "zvuk"
                    ? Array.Empty<Track>()
                    : await _spotify.SearchTrackAsync(track).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Status = $"Search failed for track #{index + 1}: {ex.Message}";
                candidates = Array.Empty<Track>();
            }

            Results.Add(new MatchRow(MatchEngine.FindMatch(track, candidates)));
            index++;
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
        }

        MatchedCount = Results.Count(r => r.Status == MatchStatus.Matched.ToString());
        AmbiguousCount = Results.Count(r => r.Status == MatchStatus.Ambiguous.ToString());
        MissingCount = Results.Count(r => r.Status == MatchStatus.Missing.ToString());
        OnPropertyChanged(nameof(ResultsVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ResultsSummary));
        OnPropertyChanged(nameof(TransferHint));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        (TransferCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportJsonCommand as RelayCommand)?.RaiseCanExecuteChanged();
        Status = $"Analysis complete: {Results.Count} tracks — matched={MatchedCount}, ambiguous={AmbiguousCount}, missing={MissingCount}.";
    }

    private async Task TransferAsync()
    {
        if (_currentPlaylist is null)
        {
            Status = "Import a playlist before transferring.";
            return;
        }

        var selectedRows = Results.Where(row => row.IsSelected && row.Candidate is not null).ToList();
        if (selectedRows.Count == 0)
        {
            Status = "Select at least one high-confidence match before transferring.";
            return;
        }
        var confirm = MessageBox.Show(
            $"Create a new private Spotify playlist and add {selectedRows.Count} selected tracks?",
            "Confirm transfer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        IsBusy = true;
        try
        {
            await _auth.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(true);
            _auth.ApplyToProvider(_spotify);
            ApplyAuthToUi();

            var matchedTracks = selectedRows
                .Select(row => row.Candidate!)
                .ToList();

            if (matchedTracks.Count == 0)
            {
                Status = "Nothing to transfer: no high-confidence matches. Review ambiguous results or use another source file.";
                return;
            }

            var name = string.IsNullOrWhiteSpace(_currentPlaylist.Name)
                ? "TuneBridge playlist"
                : _currentPlaylist.Name;
            Status = $"Creating Spotify playlist “{name}”…";
            var playlistId = await _spotify.CreatePlaylistAsync(
                name,
                $"Imported with TuneBridge. {matchedTracks.Count} matched tracks.",
                isPublic: false,
                cancellationToken: cancellationToken).ConfigureAwait(true);

            Status = $"Adding {matchedTracks.Count} tracks to Spotify…";
            await _spotify.AddTracksAsync(playlistId, matchedTracks, cancellationToken).ConfigureAwait(true);
            var url = _spotify.GetPlaylistUrl(playlistId);
            Status = $"Transfer complete: {matchedTracks.Count} tracks added. {Results.Count - matchedTracks.Count} ambiguous/missing tracks skipped. {url}";
        }
        catch (OperationCanceledException)
        {
            Status = "Transfer cancelled. A private Spotify playlist may already exist; check your account before retrying.";
        }
        catch (Exception ex)
        {
            Status = $"Transfer failed: {ex.Message}. If the playlist was already created, check Spotify before retrying to avoid duplicates.";
        }
        finally
        {
            IsBusy = false;
            EndOperation();
        }
    }

    private void ExportPlaylist()
    {
        if (_currentPlaylist is null)
            return;

        var safeName = string.IsNullOrWhiteSpace(_currentPlaylist.Name) ? "playlist" :
            string.Concat(_currentPlaylist.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dialog = new SaveFileDialog
        {
            Title = "Export playlist",
            Filter = "JSON playlist (*.json)|*.json|CSV playlist (*.csv)|*.csv|M3U playlist (*.m3u)|*.m3u",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = safeName + ".json"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var extension = Path.GetExtension(dialog.FileName);\n            File.WriteAllText(dialog.FileName, PlaylistFileFormats.Export(_currentPlaylist, extension));
            Status = $"Playlist exported as {extension.ToUpperInvariant()}: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
        }
    }


    public int ProgressPercent => _currentPlaylist is null || _currentPlaylist.Tracks.Count == 0
        ? 0
        : (int)Math.Round((double)Results.Count / _currentPlaylist.Tracks.Count * 100);

    public string ProgressText => _currentPlaylist is null
        ? string.Empty
        : $"{Results.Count} / {_currentPlaylist.Tracks.Count} tracks analyzed";

    private void BeginOperation()
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
    }

    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void CancelCurrentOperation()
    {
        if (_operationCts is null || _operationCts.IsCancellationRequested)
            return;
        Status = "Cancellation requested…";
        _operationCts.Cancel();
    }
\n    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
