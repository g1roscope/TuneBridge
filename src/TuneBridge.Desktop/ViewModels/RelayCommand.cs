using System.Windows.Input;
using TuneBridge.Core.Models;

namespace TuneBridge.Desktop.ViewModels;

public sealed class MatchRow
{
    public MatchRow(MatchResult m)
    {
        Position = m.Source.Position;
        SourceTitle = m.Source.Title;
        SourceArtist = m.Source.Artist;
        MatchTitle = m.Candidate?.Title ?? "—";
        MatchArtist = m.Candidate?.Artist ?? "—";
        Status = m.Status.ToString();
        Score = m.Score;
    }

    public int Position { get; }
    public string SourceTitle { get; }
    public string SourceArtist { get; }
    public string MatchTitle { get; }
    public string MatchArtist { get; }
    public string Status { get; }
    public double Score { get; }
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
