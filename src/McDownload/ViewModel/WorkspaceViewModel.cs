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
    private bool _loggingFailureReported;
    public string? DiagnosticLogPath => _diagnostics.FilePath;
    public string DiagnosticDirectory => Path.GetDirectoryName(DiagnosticLogPath) ?? DiagnosticLog.DefaultDirectory;
    private CancellationTokenSource? _cancellation;
    public InstanceManager Instances { get; private set; } = null!;
    public HmclDownloadProvider DownloadProvider { get; private set; } = null!;
    public HmclFetchTask Fetcher { get; private set; } = null!;
    public VersionResolver Versions { get; private set; } = null!;
    public LoaderInstaller Loaders { get; private set; } = null!;
    public GameDownloadService Games { get; private set; } = null!;
    public LoaderVersionCatalog Catalog { get; private set; } = null!;
    public InstalledVersionScanner InstalledVersions { get; private set; } = null!;
    public long ConfigurationVersion { get; private set; }
    public WorkspaceViewModel(HttpClient? http = null, DiagnosticLog? diagnostics = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _diagnostics = diagnostics ?? new DiagnosticLog();
        Log("诊断日志文件：" + (_diagnostics.FilePath ?? "不可用"));
    }
    public ObservableCollection<string> Logs { get; } = new();
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isIndeterminate;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string status = "就绪";
    [ObservableProperty] private string? error;
    public event EventHandler? Changed;
    public event EventHandler? ConfigurationChanged;
    public void Configure(string root, DownloadSource downloadSource = DownloadSource.Bmclapi)
    {
        if (IsBusy) throw new InvalidOperationException("任务运行期间不能切换游戏目录。");
        Instances = new(root);
        DownloadProvider = new(downloadSource, Log);
        Fetcher = new(_http, DownloadProvider, _diagnostics);
        Versions = new(Instances, Fetcher);
        Loaders = new(Instances, Versions, Fetcher, Log);
        Games = new(Instances, Versions, Fetcher);
        Catalog = new(Fetcher);
        InstalledVersions = new(Instances);
        ConfigurationVersion++;
        Log($"游戏目录：{root}；下载源：{downloadSource}");
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Log(string message)
    {
        message = DiagnosticText.Redact(message);
        _diagnostics.Write("INFO", message);
        AppendLog(message);
    }
    public void LogException(string message, Exception error)
    {
        _diagnostics.Write("ERROR", message, error);
        AppendLog(DiagnosticText.Redact(message) + "：" + DiagnosticText.Summary(error));
    }
    public void ReportError(string message, Exception error)
    { Error = DiagnosticText.Redact(error.Message); LogException(message, error); }
    private void AppendLog(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }
        Logs.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (_diagnostics.Error is not null && !_loggingFailureReported)
        {
            _loggingFailureReported = true;
            Logs.Add("诊断日志写入失败，仅保留界面日志：" + _diagnostics.Error);
        }
        if (Logs.Count > 500) Logs.RemoveAt(0);
    }
    public async Task<bool> RunAsync(string label, Func<OperationContext, Task> action)
    {
        if (IsBusy) return false;
        IsBusy = true; Error = null; Status = label; Progress = 0; IsIndeterminate = true;
        using var source = new CancellationTokenSource();
        _cancellation = source;
        var id = Guid.NewGuid();
        _diagnostics.Write("OPERATION", $"Begin {id:N}: {label}");
        var reporter = new Progress<OperationProgress>(p =>
        {
            if (_cancellation != source || p.OperationId != id) return;
            double? fraction = p.OverallFraction ?? p.StageFraction;
            IsIndeterminate = !fraction.HasValue;
            Progress = Math.Clamp(fraction ?? 0, 0, 1) * 100;
            if (!string.IsNullOrEmpty(p.CurrentItem)) { Status = p.CurrentItem; Log(p.CurrentItem); }
        });
        try
        {
            Log(label);
            await action(new(id, reporter, source.Token));
            Status = "任务完成"; IsIndeterminate = false; Progress = 100; Log(Status);
            return true;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        { Status = "任务已取消"; Log(Status); return false; }
        catch (OperationCanceledException ex)
        { Error = "请求超时或被中断，请重试。"; Status = "任务未完成"; LogException(Error, ex); return false; }
        catch (Exception ex)
        {
            Error = DiagnosticText.Redact(ex.Message); Status = "任务未完成";
            LogException($"{label}：{Status}", ex); return false;
        }
        finally
        {
            _diagnostics.Write("OPERATION", $"End {id:N}: {Status}");
            _cancellation = null; IsBusy = false; IsIndeterminate = false; Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    [RelayCommand] private void Cancel()
    {
        if (_cancellation is not { IsCancellationRequested: false } cancellation) return;
        Log("已请求取消当前任务。");
        cancellation.Cancel(); Status = "正在取消…";
    }
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _diagnostics.Dispose();
    }
}
