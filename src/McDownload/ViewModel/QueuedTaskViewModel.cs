using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using McDownload.Model;

namespace McDownload.ViewModel;

public enum QueuedTaskState { Pending, Running, Completed, Canceled, Failed }

public partial class TaskStepViewModel(OperationStep kind, string title) : ObservableObject
{
    public OperationStep Kind { get; } = kind;
    public string Title { get; } = title;
    [ObservableProperty] private QueuedTaskState state;
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool isIndeterminate = true;
    public bool IsRunning => State == QueuedTaskState.Running;
    public string Symbol => State switch
    {
        QueuedTaskState.Running => "●",
        QueuedTaskState.Completed => "✓",
        QueuedTaskState.Canceled or QueuedTaskState.Failed => "×",
        _ => "○"
    };
    public string StatusText => State switch
    {
        QueuedTaskState.Running => IsIndeterminate ? "进行中" : $"{Progress:0}%",
        QueuedTaskState.Completed => "已完成",
        QueuedTaskState.Canceled => "已取消",
        QueuedTaskState.Failed => "失败",
        _ => "未开始"
    };
    partial void OnStateChanged(QueuedTaskState value)
    { OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(Symbol)); OnPropertyChanged(nameof(StatusText)); }
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(StatusText));
    partial void OnIsIndeterminateChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

public partial class QueuedTaskViewModel : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public long Order { get; }
    public IReadOnlyList<TaskStepViewModel> Steps { get; }
    public ObservableCollection<string> Logs { get; } = new();
    internal Func<OperationContext, Task> Action { get; }
    internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeStep;
    [ObservableProperty] private QueuedTaskState state;
    [ObservableProperty] private string currentItem = "等待执行";
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool isIndeterminate = true;
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool isExpanded;
    public bool IsPending => State == QueuedTaskState.Pending;
    public bool IsRunning => State == QueuedTaskState.Running;
    public bool HasStarted => State != QueuedTaskState.Pending;
    public string ProgressText => IsIndeterminate ? "进行中" : $"{Progress:0}%";
    public string StatusText => State switch
    {
        QueuedTaskState.Running => "执行中",
        QueuedTaskState.Completed => "已完成",
        QueuedTaskState.Canceled => "已取消",
        QueuedTaskState.Failed => "失败",
        _ => "等待执行"
    };

    internal QueuedTaskViewModel(string title, long order, IReadOnlyList<(OperationStep Kind, string Title)> steps,
        Func<OperationContext, Task> action)
    {
        Title = title; Order = order; Action = action;
        Steps = steps.Select(x => new TaskStepViewModel(x.Kind, x.Title)).ToArray();
    }

    internal void Start()
    {
        State = QueuedTaskState.Running; IsExpanded = true;
        CurrentItem = Steps[0].Title;
        Steps[0].State = QueuedTaskState.Running;
    }

    internal bool Report(OperationProgress report)
    {
        var kind = report.Step ?? (report.Stage switch
        {
            OperationStage.InstallingLoader or OperationStage.RemovingLoader or OperationStage.Downloading => OperationStep.Loader,
            OperationStage.Committing or OperationStage.Completed => OperationStep.Saving,
            _ => OperationStep.Preparing
        });
        int index = Steps.ToList().FindIndex(x => x.Kind == kind);
        if (Steps.Count == 1) index = 0;
        if (index < _activeStep || index < 0) return false;
        double? fraction = report.StageFraction ?? report.OverallFraction;
        if (index == _activeStep && fraction is double older && double.IsFinite(older) &&
            Math.Clamp(older, 0, 1) * 100 < Steps[index].Progress) return false;
        for (int i = _activeStep; i < index; i++)
        { Steps[i].State = QueuedTaskState.Completed; Steps[i].Progress = 100; Steps[i].IsIndeterminate = false; }
        _activeStep = index;
        var step = Steps[index];
        step.State = QueuedTaskState.Running;
        step.IsIndeterminate = fraction is null || !double.IsFinite(fraction.Value);
        if (fraction is double value && double.IsFinite(value))
        { step.Progress = Math.Max(step.Progress, Math.Clamp(value, 0, 1) * 100); step.IsIndeterminate = false; }
        IsIndeterminate = step.IsIndeterminate;
        Progress = Math.Max(Progress, (index + step.Progress / 100) / Steps.Count * 100);
        if (!string.IsNullOrEmpty(report.CurrentItem)) CurrentItem = report.CurrentItem;
        return true;
    }

    internal void Finish(QueuedTaskState final, string? message = null)
    {
        State = final; Error = message; IsIndeterminate = false; IsExpanded = false;
        CurrentItem = final switch
        {
            QueuedTaskState.Completed => "任务完成",
            QueuedTaskState.Canceled => "任务已取消",
            _ => "任务未完成"
        };
        if (final == QueuedTaskState.Completed)
        {
            Progress = 100;
            foreach (var step in Steps)
            { step.State = QueuedTaskState.Completed; step.Progress = 100; step.IsIndeterminate = false; }
        }
        else if (Steps[_activeStep].IsRunning) Steps[_activeStep].State = final;
    }

    partial void OnStateChanged(QueuedTaskState value)
    { OnPropertyChanged(nameof(IsPending)); OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(HasStarted)); OnPropertyChanged(nameof(StatusText)); }
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnIsIndeterminateChanged(bool value) => OnPropertyChanged(nameof(ProgressText));
}
