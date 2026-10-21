using McDownload.Model;

namespace McDownload.Service;

public interface IErrorNotifier
{
    event EventHandler<OperationErrorEventArgs>? ErrorOccurred;
    void Notify(OperationError error);
}
