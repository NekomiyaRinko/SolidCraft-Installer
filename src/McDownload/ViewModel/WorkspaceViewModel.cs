using McDownload.i18n;
using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McDownload.Model;
using McDownload.Service;

namespace McDownload.ViewModel;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DiagnosticLog _diagnostics;
    private readonly CurseForgeFileResolver _curseforge;
    private string? _sessionCurseForgeKey;
    public bool IsUpdatingCredentials { get; set; }
    public bool HasCurseForgeApiKey => !string.IsNullOrWhiteSpace(_sessionCurseForgeKey);
    internal Task RememberCurseForgeApiKeyAsync(CurseForgeCredentialStore credentials) =>
        credentials.SaveAsync(_sessionCurseForgeKey ?? throw new InvalidOperationException(Text.Get("Settings", "CredentialMissing")));
    public void SetCurseForgeApiKey(string? key)
    {
        if (IsBusy) throw new InvalidOperationException(Text.Get("Workspace", "WaitForQueueBeforeCredentials"));
        _sessionCurseForgeKey = key is null ? null : CurseForgeCredentialStore.Validate(key);
        OnPropertyChanged(nameof(HasCurseForgeApiKey));
    }
    private readonly ModpackFileDownloader _modpackDownloads;
    private bool _loggingFailureReported;
    public string? DiagnosticLogPath => _diagnostics.FilePath;
    public string DiagnosticDirectory => _diagnostics.DirectoryPath;
    public Task<int> ExportLogsAsync(string destination) => _diagnostics.ExportAsync(destination);
    public async Task<int> RecycleLogsAsync()
    {
        try { return await _diagnostics.RecycleLogsAsync(); }
        finally { OnPropertyChanged(nameof(DiagnosticLogPath)); OnPropertyChanged(nameof(DiagnosticDirectory)); }
    }
    private CancellationTokenSource? _cancellation;
    private readonly Queue<QueuedTaskViewModel> _pending = new();
    private bool _processing;
    private bool _stopping;
    private long _taskOrder;
    public ObservableCollection<QueuedTaskViewModel> Tasks { get; } = new();
    [ObservableProperty] private QueuedTaskViewModel? currentTask;
    public string QueueSummary => Text.Format("Workspace", "QueueSummary", Tasks.Count(x => x.IsRunning), Tasks.Count(x => x.IsPending));
    [ObservableProperty] private bool hasTaskNotification;
    private long _notificationGeneration;
    internal Task TaskNotification { get; private set; } = Task.CompletedTask;
    private async Task NotifyTaskAddedAsync()
    {
        long generation = ++_notificationGeneration;
        HasTaskNotification = true;
        await Task.Delay(3000);
        if (generation == _notificationGeneration) HasTaskNotification = false;
    }
    public InstanceManager Instances { get; private set; } = null!;
    public HmclDownloadProvider DownloadProvider { get; private set; } = null!;
    public HmclFetchTask Fetcher { get; private set; } = null!;
    public VersionResolver Versions { get; private set; } = null!;
    public LoaderInstaller Loaders { get; private set; } = null!;
    public GameDownloadService Games { get; private set; } = null!;
    public LoaderVersionCatalog Catalog { get; private set; } = null!;
    public InstalledVersionScanner InstalledVersions { get; private set; } = null!;
    public ModpackImportService Modpacks { get; private set; } = null!;
    public long ConfigurationVersion { get; private set; }
    public int MaxDownloadThreads { get; private set; } = 8;
    public WorkspaceViewModel(HttpClient? http = null, DiagnosticLog? diagnostics = null, Func<string?>? curseForgeApiKey = null,
        HttpMessageHandler? curseForgeHandler = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _diagnostics = diagnostics ?? new DiagnosticLog();
        _curseforge = new(() => _sessionCurseForgeKey ?? curseForgeApiKey?.Invoke(), curseForgeHandler);
        _modpackDownloads = new(diagnostics: _diagnostics);
        Log(Text.Format("Workspace", "DiagnosticLogFile", (_diagnostics.FilePath ?? Text.Get("Workspace", "Unavailable"))));
    }
    public ObservableCollection<string> Logs { get; } = new();
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isIndeterminate;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string status = Text.Get("Workspace", "Ready");
    [ObservableProperty] private string? error;
    public event EventHandler? Changed;
    public event EventHandler? ConfigurationChanged;
    public void Configure(string root, DownloadSource downloadSource = DownloadSource.Bmclapi, int? maxDownloadThreads = null)
    {
        if (IsBusy) throw new InvalidOperationException(Text.Get("Workspace", "CannotSwitchDirectory"));
        int threads = maxDownloadThreads ?? MaxDownloadThreads;
        if (threads is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxDownloadThreads));
        MaxDownloadThreads = threads;
        Instances = new(root);
        DownloadProvider = new(downloadSource, Log);
        Fetcher = new(_http, DownloadProvider, _diagnostics);
        Versions = new(Instances, Fetcher);
        Loaders = new(Instances, Versions, Fetcher, Log);
        Games = new(Instances, Versions, Fetcher, threads);
        Catalog = new(Fetcher);
        InstalledVersions = new(Instances);
        Modpacks = new(Instances, Versions, Games, Loaders, _curseforge, _modpackDownloads);
        ConfigurationVersion++;
        Log(Text.Format("Workspace", "ConfigurationDetails", root, downloadSource));
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Log(string message)
    {
        message = DiagnosticText.Redact(message);
        _diagnostics.Write("INFO", message);
        AppendLog(message, CurrentTask);
    }
    public void LogException(string message, Exception error)
    {
        _diagnostics.Write("ERROR", message, error);
        AppendLog(Text.Format("Workspace", "MessageDetails", DiagnosticText.Redact(message), DiagnosticText.Summary(error)), CurrentTask);
    }
    public void ReportError(string message, Exception error)
    { Error = DiagnosticText.Redact(error.Message); LogException(message, error); }
    private void AppendLog(string message, QueuedTaskViewModel? task)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => AppendLog(message, task));
            return;
        }
        Logs.Add(Text.Format("Workspace", "TimestampedLog", DateTime.Now, message));
        task?.Logs.Add(Logs[^1]);
        if (task?.Logs.Count > 500) task.Logs.RemoveAt(0);
        if (_diagnostics.Error is not null && !_loggingFailureReported)
        {
            _loggingFailureReported = true;
            Logs.Add(Text.Format("Workspace", "DiagnosticWriteFailed", _diagnostics.Error));
        }
        if (Logs.Count > 500) Logs.RemoveAt(0);
    }
    public Task<bool> RunAsync(string label, Func<OperationContext, Task> action)
    {
        if (IsBusy) return Task.FromResult(false);
        return EnqueueAsync(label, action);
    }

    public Task<bool> EnqueueModpackAsync(ModpackImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedArchiveSha256))
            throw new ArgumentException(Text.Get("Workspace", "PreviewRequired"), nameof(request));
        var snapshot = request with { SelectedOptionalFiles = request.SelectedOptionalFiles?.ToArray() };
        return EnqueueAsync(Text.Format("Workspace", "ImportPackTitle", snapshot.DirectoryName), async context =>
        {
            var result = await Modpacks.ImportAsync(snapshot, context);
            if (result.State.Status != ModpackImportStatus.Completed) throw new ModpackFilesRequiredException(result);
        }, ModpackImportService.Steps);
    }

    public Task<bool> ResumeModpackAsync(ModpackResumeRequest request)
    {
        var snapshot = request with { LocalFiles = request.LocalFiles?.ToDictionary(x => x.Key, x => x.Value) };
        return EnqueueAsync(Text.Get("Workspace", "ResumePackTitle"), async context =>
        {
            var result = await Modpacks.ResumeAsync(snapshot, context);
            if (result.State.Status != ModpackImportStatus.Completed) throw new ModpackFilesRequiredException(result);
        }, ModpackImportService.Steps);
    }

    public Task<bool> EnqueueAsync(string label, Func<OperationContext, Task> action,
        IReadOnlyList<(OperationStep Kind, string Title)>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_stopping || IsUpdatingCredentials) return Task.FromResult(false);
        steps ??= new[] { (OperationStep.Preparing, label) };
        if (steps.Count == 0 || steps.Select(x => x.Kind).Distinct().Count() != steps.Count)
            throw new ArgumentException(Text.Get("Workspace", "InvalidSteps"), nameof(steps));
        var task = new QueuedTaskViewModel(label, ++_taskOrder, steps, action);
        Tasks.Add(task); _pending.Enqueue(task);
        TaskNotification = NotifyTaskAddedAsync();
        UpdateQueue();
        IsBusy = true;
        if (!_processing) { _processing = true; _ = ProcessQueueAsync(); }
        return task.Completion.Task;
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (_pending.TryDequeue(out var task))
            {
                if (!task.IsPending) continue;
                CurrentTask = task; task.Start(); UpdateQueue();
                bool success = await ExecuteAsync(task);
                task.Completion.TrySetResult(success);
            }
        }
        finally
        {
            CurrentTask = null; _processing = false; IsBusy = false; IsIndeterminate = false;
            UpdateQueue(); Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> ExecuteAsync(QueuedTaskViewModel task)
    {
        Error = null; Status = task.Title; Progress = 0; IsIndeterminate = true;
        using var source = new CancellationTokenSource();
        _cancellation = source;
        var id = task.Id;
        _diagnostics.Write("OPERATION", $"Begin {id:N}: {task.Title}");
        var reporter = new Progress<OperationProgress>(p =>
        {
            if (_cancellation != source || p.OperationId != id || !task.IsRunning) return;
            if (!task.Report(p with { CurrentItem = p.CurrentItem is null ? null : DiagnosticText.Redact(p.CurrentItem) })) return;
            IsIndeterminate = task.IsIndeterminate; Progress = task.Progress;
            if (!string.IsNullOrEmpty(p.CurrentItem)) { Status = task.CurrentItem; Log(task.CurrentItem); }
        });
        try
        {
            Log(task.Title);
            await task.Action(new(id, reporter, source.Token));
            task.Finish(QueuedTaskState.Completed);
            Status = Text.Get("Common", "TaskCompleted"); IsIndeterminate = false; Progress = 100; Log(Status);
            return true;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        { task.Finish(QueuedTaskState.Canceled); Status = Text.Get("Common", "TaskCanceled"); Log(Status); return false; }
        catch (OperationCanceledException ex)
        { Error = Text.Get("Workspace", "RequestInterrupted"); task.Finish(QueuedTaskState.Failed, Error); Status = Text.Get("Common", "TaskIncomplete"); LogException(Error, ex); return false; }
        catch (Exception ex)
        {
            Error = DiagnosticText.Redact(ex.Message); Status = Text.Get("Common", "TaskIncomplete");
            task.Finish(QueuedTaskState.Failed, Error);
            LogException(Text.Format("Workspace", "MessageDetails", task.Title, Status), ex); return false;
        }
        finally
        {
            _diagnostics.Write("OPERATION", $"End {id:N}: {Status}");
            _cancellation = null; IsIndeterminate = false; UpdateQueue();
        }
    }

    private void UpdateQueue()
    {
        var ordered = Tasks.OrderBy(x => x.IsRunning ? 0 : x.IsPending ? 1 : 2).ThenBy(x => x.Order).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            int index = Tasks.IndexOf(ordered[i]);
            if (index != i) Tasks.Move(index, i);
        }
        while (Tasks.Count(x => !x.IsPending && !x.IsRunning) > 50)
            Tasks.Remove(Tasks.First(x => !x.IsPending && !x.IsRunning));
        OnPropertyChanged(nameof(QueueSummary));
    }

    [RelayCommand] private void RemoveQueued(QueuedTaskViewModel task)
    {
        if (!task.IsPending || !Tasks.Contains(task)) return;
        task.Finish(QueuedTaskState.Canceled);
        Tasks.Remove(task); task.Completion.TrySetResult(false); UpdateQueue();
    }

    public void StopQueue()
    {
        _stopping = true;
        foreach (var task in Tasks.Where(x => x.IsPending).ToArray()) RemoveQueued(task);
        Cancel();
    }
    [RelayCommand] private void Cancel()
    {
        if (_cancellation is not { IsCancellationRequested: false } cancellation) return;
        Log(Text.Get("Workspace", "CancelRequested"));
        cancellation.Cancel(); Status = Text.Get("Workspace", "Canceling");
        if (CurrentTask is not null) CurrentTask.CurrentItem = Status;
    }
    public void Dispose()
    {
        StopQueue();
        if (_ownsHttp) _http.Dispose();
        _curseforge.Dispose();
        _modpackDownloads.Dispose();
        _sessionCurseForgeKey = null;
        _diagnostics.Dispose();
    }
}
