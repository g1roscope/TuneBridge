using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows.Input;
using System.Windows;
using TuneBridge.Core.Abstractions;
using TuneBridge.Core.ImportExport;
using TuneBridge.Core.Matching;
using TuneBridge.Core.Models;
using TuneBridge.Core.Services;
using TuneBridge.Infrastructure.Security;
using TuneBridge.Infrastructure.Diagnostics;
using TuneBridge.Infrastructure.Spotify;
using TuneBridge.Infrastructure.Storage;
using TuneBridge.Infrastructure.Zvuk;
using TuneBridge.Infrastructure.Yandex;

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
    private readonly ZvukProvider _zvuk;
    private readonly YandexMusicProvider _yandex;
    private readonly MusicProviderRegistry _providers;
    private readonly TransferHistoryStore _historyStore = new();
    private Playlist? _currentPlaylist;
    private CancellationTokenSource? _operationCts;

    private string _playlistUrl = "";
    private string _status = "Ready. Connect Spotify (PKCE) or paste a playlist URL.";
    private string _authStatus = "Not connected";
    private string _yandexAuthInstructions = "Вход выполняется на сайте Яндекса. TuneBridge не запрашивает пароль.";
    private string _destination = "spotify";
    private bool _isBusy;
    private bool _isAuthenticated;
    private string _currentSection = "Transfer";
    private string _playlistNameDisplay = "";
    private int _matchedCount;
    private int _ambiguousCount;
    private int _missingCount;
    private int _alternateVersionCount;
    private int _searchErrorCount;

    public ObservableCollection<MatchRow> Results { get; } = new();
    public ObservableCollection<TransferHistoryEntry> History { get; } = new();
    public ObservableCollection<IMusicProvider> DestinationProviders { get; } = new();

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

    public bool IsYandexAuthenticated => _yandex.IsAuthenticated;
    public string YandexAuthStatus => IsYandexAuthenticated ? "Аккаунт Яндекс Музыки подключён" : "Аккаунт Яндекс Музыки не подключён";
    public string YandexAuthInstructions
    {
        get => _yandexAuthInstructions;
        private set { _yandexAuthInstructions = value; OnPropertyChanged(); }
    }

    public string Destination
    {
        get => _destination;
        set
        {
            if (string.Equals(_destination, value, StringComparison.OrdinalIgnoreCase)) return;
            _destination = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DestinationDisplayName));
            (TransferCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string DestinationDisplayName => DestinationProviders
        .FirstOrDefault(provider => string.Equals(provider.ProviderId, Destination, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? "No writable service connected";

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
            (ConnectYandexCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectYandexCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
            OnPropertyChanged(nameof(SourceServiceGlyph));
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

    public bool IsDestinationAvailable => DestinationProviders.Any(provider =>
        string.Equals(provider.ProviderId, Destination, StringComparison.OrdinalIgnoreCase));

    public string AuthStatusShort =>
        IsAuthenticated
            ? (string.IsNullOrWhiteSpace(_auth.DisplayName) ? "Spotify connected" : _auth.DisplayName!)
            : "Not connected";

    public string SourceServiceSubtitle =>
        _currentPlaylist?.SourceService switch
        {
            "file" => "Imported from local file",
            "spotify" => "Loaded from Spotify",
            "zvuk" => "Loaded from Zvuk (experimental)",
            "yandex" => "Loaded from Yandex Music",
            _ => IsAuthenticated ? "Spotify connected" : "Connect Spotify to analyze"
        };

    public string SourceServiceGlyph => _currentPlaylist?.SourceService switch
    {
        "spotify" => "S",
        "zvuk" => "З",
        "yandex" => "Я",
        "file" => "♫",
        _ => "♪"
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

    public int AlternateVersionCount
    {
        get => _alternateVersionCount;
        private set { _alternateVersionCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TransferHint)); }
    }

    public int SearchErrorCount
    {
        get => _searchErrorCount;
        private set { _searchErrorCount = value; OnPropertyChanged(); }
    }

    public string TransferHint =>
        $"Only selected high-confidence matches will be added. Alternate versions ({AlternateVersionCount}), ambiguous ({AmbiguousCount}), missing ({MissingCount}) and search errors ({SearchErrorCount}) are excluded.";

    public string AppVersion => "TuneBridge 1.0 Preview · .NET 10";

    public string AboutText =>
        "TuneBridge transfers playlists using supported integrations and portable playlist files. " +
        "Spotify uses Authorization Code + PKCE. Yandex Music uses OAuth device-code sign-in; its API integration is unofficial and experimental. " +
        "Zvuk public playlist reading and track search are experimental. VK Music integration is not available yet. " +
        "Local JSON, CSV, M3U and M3U8 playlists can be imported and exported. Review matches before transferring.";

    public ICommand ImportCommand { get; }
    public ICommand ImportFileCommand { get; }
    public ICommand TransferCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ConnectSpotifyCommand { get; }
    public ICommand DisconnectSpotifyCommand { get; }
    public ICommand ConnectYandexCommand { get; }
    public ICommand DisconnectYandexCommand { get; }
    public ICommand NavigateTransferCommand { get; }
    public ICommand NavigateHistoryCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand OpenDiagnosticsCommand { get; }
    public string DiagnosticsLocation => DiagnosticsLogger.LogDirectory;

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
        _zvuk = new ZvukProvider();
        _yandex = new YandexMusicProvider(store);
        _providers = new MusicProviderRegistry([_spotify, _zvuk, _yandex]);
        RefreshAvailableDestinations();

        ImportCommand = new RelayCommand(async () => await ImportAsync(), () => !IsBusy);
        ImportFileCommand = new RelayCommand(async () => await ImportFileAsync(), () => !IsBusy);
        TransferCommand = new RelayCommand(async () => await TransferAsync(), () => !IsBusy && IsDestinationAvailable && MatchedCount > 0);
        ExportJsonCommand = new RelayCommand(ExportPlaylist, () => !IsBusy && _currentPlaylist is not null);
        CancelCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy);
        ConnectSpotifyCommand = new RelayCommand(async () => await ConnectSpotifyAsync(), () => !IsBusy && !IsAuthenticated);
        DisconnectSpotifyCommand = new RelayCommand(async () => await DisconnectSpotifyAsync(), () => !IsBusy && IsAuthenticated);
        ConnectYandexCommand = new RelayCommand(async () => await ConnectYandexAsync(), () => !IsBusy && !IsYandexAuthenticated);
        DisconnectYandexCommand = new RelayCommand(async () => await DisconnectYandexAsync(), () => !IsBusy && IsYandexAuthenticated);
        NavigateTransferCommand = new RelayCommand(() => CurrentSection = "Transfer");
        NavigateHistoryCommand = new RelayCommand(async () => await NavigateHistoryAsync());
        NavigateSettingsCommand = new RelayCommand(() => CurrentSection = "Settings");
        ClearHistoryCommand = new RelayCommand(async () => await ClearHistoryAsync(), () => !IsBusy && History.Count > 0);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnosticsFolder);

        _ = RestoreAuthOnStartupAsync();
    }

    private void OpenDiagnosticsFolder()
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsLogger.LogDirectory);
            Process.Start(new ProcessStartInfo(DiagnosticsLogger.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "diagnostics.open-folder");
            Status = $"Не удалось открыть папку диагностики: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
        }
    }

    private IMusicProvider GetSelectedDestinationProvider()
    {
        if (!IsDestinationAvailable)
            throw new InvalidOperationException("No connected destination service can create playlists and add tracks.");
        return _providers.GetRequired(Destination);
    }

    private async Task PrepareProviderAsync(IMusicProvider provider, CancellationToken cancellationToken, bool requireWrite = false)
    {
        if (provider is YandexMusicProvider yandexProvider)
        {
            if (requireWrite && !yandexProvider.IsAuthenticated)
                throw new UnauthorizedAccessException("Сначала подключите Яндекс Музыку в разделе «Настройки», чтобы создавать плейлисты и добавлять треки.");
            return;
        }

        if (provider is SpotifyProvider)
        {
            if (!_auth.IsAuthenticated && string.IsNullOrEmpty(_auth.RefreshToken))
                throw new InvalidOperationException("Connect Spotify first to use it for playlist reading or writing.");

            await _auth.GetValidAccessTokenAsync().ConfigureAwait(true);
            _auth.ApplyToProvider(_spotify);
            ApplyAuthToUi();
            RefreshAvailableDestinations();
            return;
        }

        var capabilities = provider.GetCapabilities();
        if (!capabilities.CanReadPublic && !capabilities.CanReadPrivate)
            throw new NotSupportedException($"{provider.DisplayName} cannot read playlists.");
    }

    private void RefreshAvailableDestinations()
    {
        var available = _providers.GetAvailableDestinations();
        DestinationProviders.Clear();
        foreach (var provider in available)
            DestinationProviders.Add(provider);

        if (!DestinationProviders.Any(provider =>
                string.Equals(provider.ProviderId, Destination, StringComparison.OrdinalIgnoreCase)) &&
            DestinationProviders.Count > 0)
            Destination = DestinationProviders[0].ProviderId;

        OnPropertyChanged(nameof(IsDestinationAvailable));
        OnPropertyChanged(nameof(DestinationDisplayName));
        (TransferCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Could not restore Spotify session: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
        }

        try
        {
            await _yandex.RestoreSessionAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(IsYandexAuthenticated));
            OnPropertyChanged(nameof(YandexAuthStatus));
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Не удалось восстановить сессию Яндекс Музыки: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
        }
        RefreshAvailableDestinations();
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
            RefreshAvailableDestinations();
            Status = "Spotify connected. You can import playlists you own or collaborate on.";
        }
        catch (OperationCanceledException ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Login cancelled or denied: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
            ApplyAuthToUi();
        }
        catch (TimeoutException ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Login timed out: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
            ApplyAuthToUi();
        }
        catch (HttpRequestException ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Network error during Spotify login: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
            ApplyAuthToUi();
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Spotify login failed: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
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
            RefreshAvailableDestinations();
            Status = "Disconnected from Spotify. Tokens removed from secure store.";
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Disconnect error: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConnectYandexAsync()
    {
        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        IsBusy = true;
        YandexAuthInstructions = "Запрашиваем код подтверждения у Яндекса…";
        Status = "Подключение Яндекс Музыки: подтвердите вход на сайте Яндекса.";
        try
        {
            await _yandex.SignInWithDeviceFlowAsync(deviceCode =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    YandexAuthInstructions = $"Откройте {deviceCode.VerificationUrl} и введите код: {deviceCode.UserCode}. Ожидаем подтверждения…";
                    try
                    {
                        Process.Start(new ProcessStartInfo(deviceCode.VerificationUrl) { UseShellExecute = true });
                    }
                    catch
                    {
                        // The verification URL and code remain visible in Settings if the browser cannot be opened.
                    }
                });
            }, cancellationToken).ConfigureAwait(true);

            OnPropertyChanged(nameof(IsYandexAuthenticated));
            OnPropertyChanged(nameof(YandexAuthStatus));
            YandexAuthInstructions = "Сессия сохранена в защищённом хранилище Windows.";
            RefreshAvailableDestinations();
            Status = "Яндекс Музыка подключена. Теперь её можно выбрать как источник или назначение переноса.";
        }
        catch (OperationCanceledException)
        {
            YandexAuthInstructions = "Подключение отменено.";
            Status = "Подключение Яндекс Музыки отменено.";
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            YandexAuthInstructions = "Не удалось подключить аккаунт. Подробности указаны в статусе.";
            Status = $"Ошибка подключения Яндекс Музыки: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
        }
        finally
        {
            OnPropertyChanged(nameof(IsYandexAuthenticated));
            OnPropertyChanged(nameof(YandexAuthStatus));
            IsBusy = false;
            EndOperation();
        }
    }

    private async Task DisconnectYandexAsync()
    {
        IsBusy = true;
        try
        {
            await _yandex.SignOutAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(IsYandexAuthenticated));
            OnPropertyChanged(nameof(YandexAuthStatus));
            YandexAuthInstructions = "Сессия Яндекс Музыки удалена из защищённого хранилища.";
            RefreshAvailableDestinations();
            Status = "Яндекс Музыка отключена.";
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Не удалось отключить Яндекс Музыку: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
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
        IsBusy = true;
        Results.Clear();
        PlaylistNameDisplay = "";
        MatchedCount = AmbiguousCount = MissingCount = AlternateVersionCount = SearchErrorCount = 0;
        OnPropertyChanged(nameof(ResultsVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ResultsSummary));
        OnPropertyChanged(nameof(BusyVisibility));
        try
        {
            var url = PlaylistUrl.Trim();
            if (!_providers.TryParsePlaylistUrl(url, out var sourceProvider, out var parsed) ||
                sourceProvider is null || parsed is null)
            {
                Status = "URL not recognized. Supported: Spotify, public Zvuk or Yandex Music playlist URL.";
                return;
            }

            var destinationProvider = GetSelectedDestinationProvider();
            await PrepareProviderAsync(destinationProvider, cancellationToken, requireWrite: true).ConfigureAwait(true);
            if (!ReferenceEquals(sourceProvider, destinationProvider))
                await PrepareProviderAsync(sourceProvider, cancellationToken).ConfigureAwait(true);

            var sourceCapabilities = sourceProvider.GetCapabilities();
            Status = sourceCapabilities.IsExperimental
                ? $"Fetching playlist from {sourceProvider.DisplayName} (experimental)…"
                : $"Fetching playlist from {sourceProvider.DisplayName}…";
            var playlist = await sourceProvider.GetPublicPlaylistAsync(parsed, cancellationToken).ConfigureAwait(true);
            PlaylistNameDisplay = string.IsNullOrWhiteSpace(playlist.Name) ? "Playlist" : playlist.Name;
            Status = $"Loaded {playlist.Tracks.Count} tracks — matching (not transferred yet)…";

            _currentPlaylist = playlist;
            OnPropertyChanged(nameof(SourceServiceSubtitle));
            OnPropertyChanged(nameof(SourceServiceGlyph));
            await AnalyzeTracksAsync(playlist, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "Analysis cancelled. No destination playlist was created.";
        }
        catch (HttpRequestException ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Network error: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]. Session was not cleared.";
            ApplyAuthToUi();
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Error: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
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

        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        IsBusy = true;
        Results.Clear();
        PlaylistNameDisplay = "";
        MatchedCount = AmbiguousCount = MissingCount = AlternateVersionCount = SearchErrorCount = 0;
        OnPropertyChanged(nameof(ResultsVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ResultsSummary));

        try
        {
            var destinationProvider = GetSelectedDestinationProvider();
            await PrepareProviderAsync(destinationProvider, cancellationToken, requireWrite: true).ConfigureAwait(true);

            var content = await File.ReadAllTextAsync(dialog.FileName, cancellationToken).ConfigureAwait(true);
            var playlist = PlaylistFileFormats.Import(content, Path.GetExtension(dialog.FileName));
            if (playlist.Tracks.Count == 0)
                throw new InvalidOperationException("No tracks were found in this file.");

            _currentPlaylist = playlist;
            PlaylistNameDisplay = string.IsNullOrWhiteSpace(playlist.Name)
                ? Path.GetFileNameWithoutExtension(dialog.FileName)
                : playlist.Name;
            OnPropertyChanged(nameof(SourceServiceSubtitle));
            OnPropertyChanged(nameof(SourceServiceGlyph));
            Status = $"Imported {playlist.Tracks.Count} tracks from {Path.GetFileName(dialog.FileName)}. Starting Spotify matching…";
            await AnalyzeTracksAsync(playlist, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "File import or analysis cancelled.";
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"File import failed: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
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
        // Repeated tracks are common in long playlists. Cache successful search results
        // by the exact query shape so duplicates do not consume extra API calls or rate limit.
        var candidateCache = new Dictionary<string, IReadOnlyList<Track>>(StringComparer.Ordinal);
        var destinationProvider = GetSelectedDestinationProvider();
        foreach (var track in playlist.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (destinationProvider is SpotifyProvider && index > 0 && index % 25 == 0 && !string.IsNullOrEmpty(_auth.RefreshToken))
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
                var cacheKey = !string.IsNullOrWhiteSpace(track.Isrc)
                    ? "isrc:" + track.Isrc.Trim()
                    : "text:" + track.Artist.Trim() + "\u001F" + track.Title.Trim();

                if (!candidateCache.TryGetValue(cacheKey, out candidates!))
                {
                    candidates = await destinationProvider.SearchTrackAsync(
                        track, cancellationToken: cancellationToken).ConfigureAwait(true);
                    candidateCache[cacheKey] = candidates;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
            DiagnosticsLogger.Write(ex, "desktop.operation");
                Results.Add(new MatchRow(new MatchResult
                {
                    Source = track,
                    Status = MatchStatus.Error,
                    Score = 0,
                    Reasons = [$"Search failed: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]"]
                }));
                index++;
                SearchErrorCount++;
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressText));
                continue;
            }

            Results.Add(new MatchRow(MatchEngine.FindMatch(track, candidates)));
            index++;
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
        }

        MatchedCount = Results.Count(r => r.Status == MatchStatus.Matched.ToString());
        AmbiguousCount = Results.Count(r => r.Status == MatchStatus.Ambiguous.ToString());
        MissingCount = Results.Count(r => r.Status == MatchStatus.Missing.ToString());
        AlternateVersionCount = Results.Count(r => r.Status == MatchStatus.AlternateVersion.ToString());
        SearchErrorCount = Results.Count(r => r.Status == MatchStatus.Error.ToString());
        OnPropertyChanged(nameof(ResultsVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ResultsSummary));
        OnPropertyChanged(nameof(TransferHint));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        (TransferCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportJsonCommand as RelayCommand)?.RaiseCanExecuteChanged();
        Status = $"Analysis complete: {Results.Count} tracks — matched={MatchedCount}, alternate versions={AlternateVersionCount}, ambiguous={AmbiguousCount}, missing={MissingCount}, errors={SearchErrorCount}.";
    }

    private async Task TransferAsync()
    {
        if (_currentPlaylist is null)
        {
            Status = "Import a playlist before transferring.";
            return;
        }

        var selectedRows = Results.Where(row => row.IsSelected && row.Status == MatchStatus.Matched.ToString() && row.Candidate is not null).ToList();
        if (selectedRows.Count == 0)
        {
            Status = "Select at least one high-confidence match before transferring.";
            return;
        }
        var destinationProvider = GetSelectedDestinationProvider();
        var confirm = MessageBox.Show(
            $"Create a new private {destinationProvider.DisplayName} playlist and add {selectedRows.Count} selected tracks?",
            "Confirm transfer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        IsBusy = true;
        string? createdPlaylistId = null;
        try
        {
            await PrepareProviderAsync(destinationProvider, cancellationToken, requireWrite: true).ConfigureAwait(true);

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
            Status = $"Creating {destinationProvider.DisplayName} playlist “{name}”…";
            createdPlaylistId = await destinationProvider.CreatePlaylistAsync(
                name,
                $"Imported with TuneBridge. {matchedTracks.Count} matched tracks.",
                isPublic: false,
                cancellationToken: cancellationToken).ConfigureAwait(true);

            Status = $"Adding {matchedTracks.Count} tracks to {destinationProvider.DisplayName}…";
            await destinationProvider.AddTracksAsync(createdPlaylistId, matchedTracks, cancellationToken).ConfigureAwait(true);
            var url = destinationProvider.GetPlaylistUrl(createdPlaylistId);
            Status = $"Transfer complete: {matchedTracks.Count} tracks added. {Results.Count - matchedTracks.Count} source tracks were not transferred (unselected matches, alternate versions, ambiguous results, missing tracks, or search errors). {url}";
            await RecordTransferAsync(createdPlaylistId, matchedTracks.Count, 0, "Completed", url, destinationProvider);
        }
        catch (SpotifyPartialTransferException ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            var confirmed = Math.Clamp(ex.ConfirmedTracksAdded, 0, selectedRows.Count);
            var uncertainty = ex.CurrentBatchOutcomeUnknown
                ? " The interrupted batch may have reached Spotify; check the destination playlist before retrying."
                : " Do not retry the full transfer; only the remaining tracks should be considered.";
            Status = $"Partial transfer: {confirmed} of {selectedRows.Count} tracks are confirmed in {destinationProvider.DisplayName}. {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]{uncertainty}";
            await RecordTransferAsync(createdPlaylistId, confirmed, selectedRows.Count - confirmed, "Partial", Status, destinationProvider);
        }
        catch (YandexPartialTransferException ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            var confirmed = Math.Clamp(ex.ConfirmedTracksAdded, 0, selectedRows.Count);
            var uncertainty = ex.CurrentOperationOutcomeUnknown
                ? " Текущая операция могла быть выполнена сервером; проверьте плейлист перед повторным запуском."
                : " Повторно добавляйте только оставшиеся треки.";
            Status = $"Частичный перенос в {destinationProvider.DisplayName}: подтверждено {confirmed} из {selectedRows.Count}. {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]{uncertainty}";
            await RecordTransferAsync(createdPlaylistId, confirmed, selectedRows.Count - confirmed, "Partial", Status, destinationProvider);
        }
        catch (OperationCanceledException)
        {
            Status = $"Transfer cancelled. A private {destinationProvider.DisplayName} playlist may already exist; check your account before retrying.";
            await RecordTransferAsync(createdPlaylistId, 0, selectedRows.Count, "Cancelled", Status, destinationProvider);
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Transfer failed: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]. If the playlist was already created, check {destinationProvider.DisplayName} before retrying to avoid duplicates.";
            await RecordTransferAsync(createdPlaylistId, 0, selectedRows.Count, "Failed", Status, destinationProvider);
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
            var extension = Path.GetExtension(dialog.FileName);
            File.WriteAllText(dialog.FileName, PlaylistFileFormats.Export(_currentPlaylist, extension));
            Status = $"Playlist exported as {extension.ToUpperInvariant()}: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Export failed: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
        }
    }



    private async Task NavigateHistoryAsync()
    {
        CurrentSection = "History";
        await RefreshHistoryAsync();
    }

    private async Task RefreshHistoryAsync()
    {
        try
        {
            var entries = await _historyStore.GetAllAsync();
            History.Clear();
            foreach (var entry in entries)
                History.Add(entry);
            (ClearHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status = $"Could not load transfer history: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
        }
    }

    private async Task ClearHistoryAsync()
    {
        var confirm = MessageBox.Show(
            "Delete all locally stored transfer history? This does not change playlists in Spotify.",
            "Clear transfer history", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        await _historyStore.ClearAsync();
        History.Clear();
        (ClearHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task RecordTransferAsync(string? destinationPlaylistId, int transferredCount, int errorCount, string status, string? message, IMusicProvider destinationProvider)
    {
        if (_currentPlaylist is null)
            return;

        try
        {
            await _historyStore.AddAsync(new TransferHistoryEntry
            {
                SourceProvider = _currentPlaylist.SourceService,
                DestinationProvider = destinationProvider.ProviderId,
                PlaylistName = _currentPlaylist.Name,
                SourceUrl = _currentPlaylist.SourceUrl,
                DestinationPlaylistId = destinationPlaylistId,
                DestinationUrl = destinationPlaylistId is null ? null : destinationProvider.GetPlaylistUrl(destinationPlaylistId),
                TotalTracks = _currentPlaylist.Tracks.Count,
                TransferredCount = transferredCount,
                SkippedCount = Math.Max(0, _currentPlaylist.Tracks.Count - transferredCount),
                ErrorCount = errorCount,
                Status = status,
                Message = message
            });
            if (CurrentSection == "History")
                await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write(ex, "desktop.operation");
            Status += $" History could not be saved: {DiagnosticsLogger.Sanitize(ex.Message)} [{DiagnosticError.From(ex).Code}]";
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
