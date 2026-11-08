using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TuneBridge.Core.Matching;
using TuneBridge.Core.Models;
using TuneBridge.Infrastructure.Spotify;
using TuneBridge.Infrastructure.Zvuk;

namespace TuneBridge.Desktop.ViewModels;

/// <summary>
/// Main window VM. Spotify network calls require ClientId/Secret from environment:
///   TUNEBRIDGE_SPOTIFY_CLIENT_ID, TUNEBRIDGE_SPOTIFY_CLIENT_SECRET
/// No secrets are compiled into the binary.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly HttpClient _http = new();
    private string _playlistUrl = "";
    private string _status = "Ready. Paste a public Spotify playlist URL. Configure ClientId/Secret via environment.";
    private string _destination = "spotify";
    private bool _isBusy;

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

    public string Destination
    {
        get => _destination;
        set { _destination = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    public ICommand ImportCommand { get; }
    public ICommand ExportJsonCommand { get; }

    public MainViewModel()
    {
        ImportCommand = new RelayCommand(async () => await ImportAsync(), () => !IsBusy);
        ExportJsonCommand = new RelayCommand(ExportJson, () => Results.Count > 0);
    }

    private async Task ImportAsync()
    {
        IsBusy = true;
        Results.Clear();
        try
        {
            var options = new SpotifyOptions
            {
                ClientId = Environment.GetEnvironmentVariable("TUNEBRIDGE_SPOTIFY_CLIENT_ID"),
                ClientSecret = Environment.GetEnvironmentVariable("TUNEBRIDGE_SPOTIFY_CLIENT_SECRET")
            };
            var spotify = new SpotifyProvider(_http, options);
            var zvuk = new ZvukProvider();

            var url = PlaylistUrl.Trim();
            var parsed = spotify.ParsePlaylistUrl(url) ?? zvuk.ParsePlaylistUrl(url);
            if (parsed is null)
            {
                Status = "URL not recognized. Supported parse: Spotify (live), Звук (parse only).";
                return;
            }

            if (parsed.ProviderId == "zvuk")
            {
                Status = "Звук: " + zvuk.GetCapabilities().Notes;
                return;
            }

            Status = "Fetching playlist from Spotify…";
            var playlist = await spotify.GetPublicPlaylistAsync(parsed).ConfigureAwait(true);
            Status = $"Loaded {playlist.Tracks.Count} tracks — matching…";

            foreach (var track in playlist.Tracks)
            {
                IReadOnlyList<Track> candidates;
                try
                {
                    candidates = Destination == "zvuk"
                        ? Array.Empty<Track>()
                        : await spotify.SearchTrackAsync(track).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Status = $"Search error: {ex.Message}";
                    candidates = Array.Empty<Track>();
                }

                var match = MatchEngine.FindMatch(track, candidates);
                Results.Add(new MatchRow(match));
            }

            var matched = Results.Count(r => r.Status == MatchStatus.Matched.ToString());
            var ambiguous = Results.Count(r => r.Status == MatchStatus.Ambiguous.ToString());
            var missing = Results.Count(r => r.Status == MatchStatus.Missing.ToString());
            Status = $"Done: {Results.Count} tracks — matched={matched}, ambiguous={ambiguous}, missing={missing}";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            (ImportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void ExportJson()
    {
        Status = $"Export: {Results.Count} rows ready (wire SaveFileDialog in view code-behind or service).";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
