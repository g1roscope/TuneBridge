namespace TuneBridge.Infrastructure.Yandex;

/// <summary>
/// Reports confirmed Yandex playlist insertions and whether the in-flight insertion may
/// have reached the service without a confirmation response.
/// </summary>
public sealed class YandexPartialTransferException : Exception
{
    public YandexPartialTransferException(int confirmedTracksAdded, bool currentOperationOutcomeUnknown, Exception innerException)
        : base(confirmedTracksAdded == 0
            ? "Яндекс Музыка не подтвердила добавление первого трека."
            : $"Яндекс Музыка подтвердила добавление {confirmedTracksAdded} треков до сбоя.", innerException)
    {
        if (confirmedTracksAdded < 0)
            throw new ArgumentOutOfRangeException(nameof(confirmedTracksAdded));
        ArgumentNullException.ThrowIfNull(innerException);

        ConfirmedTracksAdded = confirmedTracksAdded;
        CurrentOperationOutcomeUnknown = currentOperationOutcomeUnknown;
    }

    public int ConfirmedTracksAdded { get; }
    public bool CurrentOperationOutcomeUnknown { get; }
}
