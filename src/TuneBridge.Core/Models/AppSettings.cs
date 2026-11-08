namespace TuneBridge.Core.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    /// <summary>Auto-include high-confidence matches in transfer selection.</summary>
    public bool AutoSelectMatched { get; set; } = true;
    /// <summary>Never auto-select Ambiguous rows.</summary>
    public bool RequireManualAmbiguous { get; set; } = true;
    public int MaxHistoryEntries { get; set; } = 100;
    public bool ConfirmBeforeTransfer { get; set; } = true;
}
