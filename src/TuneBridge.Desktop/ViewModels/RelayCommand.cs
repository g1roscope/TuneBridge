using System.Windows.Input;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TuneBridge.Core.Models;

namespace TuneBridge.Desktop.ViewModels;

public sealed class MatchRow : INotifyPropertyChanged
{
    private bool _isSelected;
    public MatchRow(MatchResult m)
    {
        SourceTrack = m.Source;
        Candidate = m.Candidate;
        _isSelected = m.Status == MatchStatus.Matched;
        CanSelect = m.Status == MatchStatus.Matched;
        Position = m.Source.Position;
        SourceTitle = m.Source.Title;
        SourceArtist = m.Source.Artist;
        MatchTitle = m.Candidate?.Title ?? "—";
        MatchArtist = m.Candidate?.Artist ?? "—";
        Status = m.Status.ToString();
        Score = m.Score;
        StatusLabel = m.Status switch
        {
            MatchStatus.Matched => "Found",
            MatchStatus.Ambiguous => "Ambiguous",
            MatchStatus.AlternateVersion => "Alt. version",
            MatchStatus.Missing => "Not found",
            MatchStatus.Manual => "Manual",
            MatchStatus.Error => "Search error",
            _ => m.Status.ToString()
        };
    }

    public Track SourceTrack { get; }
    public Track? Candidate { get; }
    public bool CanSelect { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect || _isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }
    public int Position { get; }
    public string SourceTitle { get; }
    public string SourceArtist { get; }
    public string MatchTitle { get; }
    public string MatchArtist { get; }
    public string Status { get; }
    public string StatusLabel { get; }
    public double Score { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task>? _asyncExecute;
    private readonly Action? _execute;
    private readonly Func<bool>? _can;

    public RelayCommand(Func<Task> asyncExecute, Func<bool>? can = null)
    {
        _asyncExecute = asyncExecute;
        _can = can;
    }

    public RelayCommand(Action execute, Func<bool>? can = null)
    {
        _execute = execute;
        _can = can;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        if (_asyncExecute is not null)
            await _asyncExecute();
        else
            _execute?.Invoke();
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
