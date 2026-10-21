namespace McDownload.Model;

public class InstanceValidationException(string message, Exception? innerException = null)
    : ArgumentException(message, innerException);

public class InstanceNotFoundException(string message, Exception? innerException = null)
    : KeyNotFoundException(message, innerException);

public class InstanceConflictException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public class UserConfirmationRequiredException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public class LoaderStateException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public class LoaderCompatibilityException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public class VersionResolutionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public class DownloadIntegrityException(string message, Exception? innerException = null)
    : IOException(message, innerException);

public class LoaderOperationFailedException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public Guid OperationId { get; init; }
    public string Code { get; init; } = "";
    public OperationStage FailedStage { get; init; }
    public LoaderState? FinalState { get; init; }
    public bool RequiresRecovery { get; init; }
}

public class LoaderOperationCanceledException(string message, Exception? innerException = null)
    : OperationCanceledException(message, innerException)
{
    public Guid OperationId { get; init; }
    public LoaderState FinalState { get; init; } = null!;
}
