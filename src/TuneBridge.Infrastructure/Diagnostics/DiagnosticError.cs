namespace TuneBridge.Infrastructure.Diagnostics;

public sealed record DiagnosticError(string Code, string Summary, string SuggestedAction)
{
    public static DiagnosticError From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            OperationCanceledException => new("TB-000", "Operation cancelled", "No action is required unless the cancellation was unexpected."),
            UnauthorizedAccessException => new("TB-401", "Access denied", "Reconnect the affected music service and check its permissions."),
            HttpRequestException => new("TB- NETWORK", "Network or service communication failed", "Check the internet connection and service availability, then retry only after checking whether a write already completed."),
            TimeoutException => new("TB-408", "The operation timed out", "Check the service and destination before retrying a transfer."),
            InvalidDataException => new("TB-422", "Local or remote data is invalid", "Keep the diagnostic report and check the affected playlist or local data file."),
            IOException => new("TB-IO", "A local file operation failed", "Check disk space and access permissions. Your original data is preserved where possible."),
            NotSupportedException => new("TB-501", "This service operation is not supported", "Choose a destination that supports playlist creation and track insertion."),
            _ => new("TB-999", "An unexpected error occurred", "Save the diagnostic report and include it when reporting this issue.")
        };
    }
}