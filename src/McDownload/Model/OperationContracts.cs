namespace McDownload.Model;

public enum OperationStage
{
    Queued,
    Validating,
    Downloading,
    Verifying,
    Preparing,
    RemovingLoader,
    InstallingLoader,
    Committing,
    Completed,
    Canceled,
    Failed
}

public enum OperationStep
{
    Preparing,
    Client,
    Libraries,
    Assets,
    Loader,
    Saving
}

public sealed record OperationProgress(
    Guid OperationId,
    InstanceId InstanceId,
    long Sequence,
    OperationStage Stage,
    double? OverallFraction,
    double? StageFraction,
    long DownloadedBytes,
    long? TotalBytes,
    string? CurrentItem,
    OperationStep? Step = null);

public sealed record OperationContext(
    Guid OperationId,
    IProgress<OperationProgress>? Progress,
    CancellationToken CancellationToken);

public sealed record OperationError(
    Guid OperationId,
    InstanceId? InstanceId,
    string Code,
    string UserMessage,
    Exception Exception,
    LoaderState? FinalState,
    bool RequiresRecovery);

public sealed class OperationErrorEventArgs : EventArgs
{
    public required OperationError Error { get; init; }
}
