namespace TuneBridge.Infrastructure.Yandex;

/// <summary>
/// Reports how many Yandex playlist insertions were confirmed before a later operation failed.
/// </summary>
public sealed class YandexPartialTransferException : Exception
{
    public YandexPartialTransferException(int confirmedTracksAdded, bool currentOperationOutcomeUnknown, Exception innerException)
        : base($"Яндекс Музыка подтвердила добавление {confirmedTracksAdded} треков до сбоя.", innerException)
    {
        if (confirmedTracksAdded <= 0)
            throw new ArgumentOutOfRangeException(nameof(confirmedTracksAdded));

        ConfirmedTracksAdded = confirmedTracksAdded;
        CurrentOperationOutcomeUnknown = currentOperationOutcomeUnknown;
    }

    public int ConfirmedTracksAdded { get; }
    public bool CurrentOperationOutcomeUnknown { get; }
}