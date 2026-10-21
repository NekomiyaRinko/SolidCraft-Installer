using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McDownload.Model;

namespace McDownload.ViewModel;

public partial class DownloadViewModel : ObservableObject
{
    public WorkspaceViewModel State { get; }
    private readonly SettingsViewModel _settings;
    public ObservableCollection<MinecraftVersionEntry> MinecraftVersions { get; } = new();
    private IReadOnlyList<MinecraftVersionEntry> _allMinecraftVersions = Array.Empty<MinecraftVersionEntry>();
    public IReadOnlyList<string> Channels { get; } = new[] { "全部", "正式版", "测试版" };
    [ObservableProperty] private string selectedMinecraftChannel = "正式版";
    public LoaderSelectionViewModel NewGameLoader { get; }
    public LoaderSelectionViewModel InstanceLoader { get; }
    [ObservableProperty] private MinecraftVersionEntry? selectedMinecraftVersion;
    [ObservableProperty] private string instanceName = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageBusy), nameof(IsPageInteractive), nameof(PageBusyMessage))]
    private bool isRefreshingVersions;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageBusy), nameof(IsPageInteractive), nameof(PageBusyMessage))]
    private bool isSubmittingGame;
    public bool IsPageBusy => IsRefreshingVersions || NewGameLoader.IsLoading || IsSubmittingGame;
    public bool IsPageInteractive => !IsPageBusy;
    public string PageBusyMessage => IsSubmittingGame ? "正在添加下载任务…" : "正在加载版本列表…";
    internal Task SubmissionFeedback { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private InstanceMetadata? targetInstance;
    [ObservableProperty] private string currentLoader = "未安装加载器";
    [ObservableProperty] private bool isLoaderConfirmationVisible;
    [ObservableProperty] private string confirmationText = "";
    private LoaderState? _loaderState;
    private LoaderReplacementPlan? _plan;
    private bool _remove;
    private long _refreshGeneration;
    public const string LoaderWarning = "不会删除 mods 文件夹中的模组，但旧模组可能不兼容新加载器。";
    public DownloadViewModel(WorkspaceViewModel state, SettingsViewModel settings)
    {
        State = state; _settings = settings;
        NewGameLoader = new(state); InstanceLoader = new(state);
        NewGameLoader.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(NewGameLoader.IsLoading)) return;
            OnPropertyChanged(nameof(IsPageBusy)); OnPropertyChanged(nameof(IsPageInteractive));
            OnPropertyChanged(nameof(PageBusyMessage));
        };
        InstanceLoader.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(InstanceLoader.SelectedType) or nameof(InstanceLoader.SelectedVersion)) CancelConfirmation();
        };
        state.ConfigurationChanged += (_, _) =>
        {
            TargetInstance = null; _allMinecraftVersions = Array.Empty<MinecraftVersionEntry>();
            MinecraftVersions.Clear(); SelectedMinecraftVersion = null;
        };
    }
    partial void OnSelectedMinecraftVersionChanged(MinecraftVersionEntry? value) => NewGameLoader.MinecraftVersion = value?.Id ?? "";
    partial void OnSelectedMinecraftChannelChanged(string value) => ApplyMinecraftFilter();
    private void ApplyMinecraftFilter()
    {
        string? selected = SelectedMinecraftVersion?.Id;
        MinecraftVersions.Clear();
        foreach (var version in _allMinecraftVersions.Where(x => SelectedMinecraftChannel == "全部" ||
            (x.Channel == MinecraftVersionChannel.Release) == (SelectedMinecraftChannel == "正式版"))) MinecraftVersions.Add(version);
        SelectedMinecraftVersion = MinecraftVersions.FirstOrDefault(x => x.Id == selected) ?? MinecraftVersions.FirstOrDefault();
    }
    partial void OnTargetInstanceChanged(InstanceMetadata? value)
    {
        _refreshGeneration++;
        _loaderState = null;
        CurrentLoader = "加载器状态未读取";
        CancelConfirmation();
        InstanceLoader.MinecraftVersion = value?.MinecraftVersionId ?? "";
    }
    public async Task SelectInstanceAsync(InstanceMetadata metadata)
    {
        CancelConfirmation(); TargetInstance = metadata;
        await RefreshLoaderAsync();
    }
    public async Task<bool> RefreshLoaderAsync()
    {
        if (TargetInstance is not { } target) return false;
        long generation = ++_refreshGeneration;
        _loaderState = null;
        CancelConfirmation();
        try
        {
            var metadata = await State.Instances.GetAsync(target.Id, default);
            var loader = await State.Loaders.GetStateAsync(target.Id, default);
            if (generation != _refreshGeneration || TargetInstance?.Id != target.Id) return false;
            TargetInstance = metadata;
            _loaderState = loader;
            CurrentLoader = _loaderState.InstalledLoader is { } value ? $"{value.Kind} · {value.Version}" : "未安装加载器";
            return true;
        }
        catch (Exception ex)
        {
            if (generation == _refreshGeneration && TargetInstance?.Id == target.Id)
            { CurrentLoader = "加载器状态读取失败"; State.ReportError(CurrentLoader, ex); }
            return false;
        }
    }
    [RelayCommand] public async Task RefreshVersionsAsync()
    {
        if (IsRefreshingVersions) return;
        IsRefreshingVersions = true;
        try
        {
            while (true)
            {
                long configuration = State.ConfigurationVersion;
                try
                {
                    var manifest = await State.Versions.GetManifestAsync(default);
                    if (configuration != State.ConfigurationVersion) continue;
                    _allMinecraftVersions = manifest.Versions;
                    ApplyMinecraftFilter();
                }
                catch (Exception ex)
                {
                    if (configuration != State.ConfigurationVersion) continue;
                    State.ReportError("版本列表获取失败", ex);
                }
                return;
            }
        }
        finally { IsRefreshingVersions = false; }
    }
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task InstallGameAsync()
    {
        if (IsPageBusy || SelectedMinecraftVersion is null || !NewGameLoader.CanInstall) return;
        LoaderSelection? selection;
        try { selection = NewGameLoader.GetSelection(); } catch (Exception ex) { State.ReportError("安装参数无效", ex); return; }
        var version = SelectedMinecraftVersion;
        string name = ResolveInstanceName(InstanceName, version.Id, selection);
        string java = _settings.JavaPath;
        long configuration = State.ConfigurationVersion;
        var previousTarget = TargetInstance?.Id;
        InstanceMetadata? installed = null;
        Task<bool> completion;
        IsSubmittingGame = true;
        try
        {
            completion = State.EnqueueAsync(name, async context =>
            {
                if (selection is not null)
                {
                    var report = await State.Loaders.ValidateCompatibilityAsync(new(version.Id, selection, java), context.CancellationToken);
                    if (report.Status != CompatibilityStatus.Compatible) throw new InvalidOperationException(string.Join("；", report.Issues.Select(x => x.Message)));
                }
                var metadata = await State.Instances.CreateAsync(new(name, version.Id, IsolationPolicy.PerInstance,
                    new(name, null, string.IsNullOrWhiteSpace(java) ? null : java)), context.CancellationToken);
                installed = metadata;
                State.Log("实例已创建：" + metadata.Editable.DisplayName);
                State.Log("未完成的下载可在实例中继续。");
                installed = await State.Games.InstallAsync(metadata.Id, version, selection is null ? context : context with
                {
                    Progress = new MappedProgress(context.Progress, p => p.Step == OperationStep.Saving
                        ? p with { Step = OperationStep.Assets, StageFraction = 1 } : p)
                });
                if (selection is not null)
                {
                    context.Progress?.Report(new(context.OperationId, installed.Id, 0, OperationStage.InstallingLoader,
                        null, null, 0, null, "正在安装 " + selection.Kind, OperationStep.Loader));
                    await State.Loaders.InstallAsync(new(installed.Id, installed.Revision, selection, java), LoaderContext(context));
                }
            }, GameSteps(selection));
            SubmissionFeedback = Task.Delay(650);
            await SubmissionFeedback;
        }
        finally { IsSubmittingGame = false; }
        await completion;
        if (configuration == State.ConfigurationVersion && installed is not null &&
            (TargetInstance?.Id == previousTarget || TargetInstance?.Id == installed.Id))
        { TargetInstance = installed; await RefreshLoaderAsync(); }
    }
    internal static string ResolveInstanceName(string? name, string minecraftVersion, LoaderSelection? loader)
        => !string.IsNullOrWhiteSpace(name) ? name.Trim() : loader is null ? minecraftVersion :
            $"{minecraftVersion}-{loader.Kind}-{loader.Version}";

    private static IReadOnlyList<(OperationStep Kind, string Title)> GameSteps(LoaderSelection? loader = null)
    {
        var steps = new List<(OperationStep, string)>
        {
            (OperationStep.Preparing, "准备实例"), (OperationStep.Client, "下载客户端"),
            (OperationStep.Libraries, "下载依赖库"), (OperationStep.Assets, "下载资源文件")
        };
        if (loader is not null) steps.Add((OperationStep.Loader, "安装 " + loader.Kind));
        steps.Add((OperationStep.Saving, "保存实例"));
        return steps;
    }

    private sealed class MappedProgress(IProgress<OperationProgress>? target, Func<OperationProgress, OperationProgress> map)
        : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => target?.Report(map(value));
    }

    private static OperationContext LoaderContext(OperationContext context) => context with
    {
        Progress = new MappedProgress(context.Progress, p => p with
        {
            Step = p.Stage is OperationStage.Committing or OperationStage.Completed ? OperationStep.Saving : OperationStep.Loader,
            StageFraction = p.Stage == OperationStage.Completed ? 1 : p.StageFraction
        })
    };

    [RelayCommand(AllowConcurrentExecutions = true)] private async Task ResumeGameAsync()
    {
        if (TargetInstance is null) return;
        var target = TargetInstance;
        long configuration = State.ConfigurationVersion;
        InstanceMetadata? installed = null;
        await State.EnqueueAsync(target.Editable.DisplayName + " · 下载 / 校验原版游戏", async context =>
        {
            var manifest = await State.Versions.GetManifestAsync(context.CancellationToken);
            var version = manifest.Versions.First(x => x.Id == target.MinecraftVersionId);
            installed = await State.Games.InstallAsync(target.Id, version, context);
        }, GameSteps());
        if (configuration == State.ConfigurationVersion && TargetInstance?.Id == target.Id && installed is not null)
        { TargetInstance = installed; await RefreshLoaderAsync(); }
    }
    [RelayCommand] private void RequestRemove()
    {
        if (State.IsBusy || _loaderState?.InstalledLoader is null || _loaderState.InstanceId != TargetInstance?.Id) return;
        _remove = true; _plan = null; ConfirmationText = "删除当前加载器？"; IsLoaderConfirmationVisible = true;
    }
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task InstallLoaderAsync()
    {
        if (TargetInstance is null) return;
        LoaderSelection? selection;
        try { selection = InstanceLoader.GetSelection() ?? throw new ArgumentException("请选择 Forge、Fabric 或 NeoForge。"); }
        catch (Exception ex) { State.ReportError("加载器选择无效", ex); return; }
        var target = TargetInstance;
        var targetId = target.Id;
        string java = _settings.JavaPath;
        long configuration = State.ConfigurationVersion;
        if (target.InstalledLoader is not null)
        {
            if (State.IsBusy) { State.Error = "请等待任务队列结束后更换加载器。"; return; }
            if (!await RefreshLoaderAsync() || _loaderState is null || TargetInstance?.Id != targetId) return;
            var loader = _loaderState;
            await State.RunAsync("正在检查加载器兼容性…", async context =>
            {
                var prepared = await State.Loaders.PrepareReplacementAsync(new(targetId, loader.Revision, selection, java), context.CancellationToken);
                if (TargetInstance?.Id != targetId || _loaderState != loader || InstanceLoader.GetSelection() != selection)
                    throw new InvalidOperationException("实例或加载器选择已变化，请重新检查。");
                _plan = prepared;
                if (_plan.Compatibility.Status != CompatibilityStatus.Compatible)
                    throw new InvalidOperationException(string.Join("；", _plan.Compatibility.Issues.Select(x => x.Message)));
                _remove = false; ConfirmationText = $"将 {CurrentLoader} 更换为 {selection.Kind} {selection.Version}？";
                IsLoaderConfirmationVisible = true;
            });
            return;
        }
        await State.EnqueueAsync(target.Editable.DisplayName + " · 安装 " + selection.Kind, context => State.Loaders.InstallAsync(
            new(targetId, target.Revision, selection, java), LoaderContext(context)), new[]
            {
                (OperationStep.Preparing, "检查实例"), (OperationStep.Loader, "安装 " + selection.Kind),
                (OperationStep.Saving, "保存实例")
            });
        if (configuration == State.ConfigurationVersion && TargetInstance?.Id == targetId) await RefreshLoaderAsync();
    }
    [RelayCommand] private async Task ConfirmLoaderAsync()
    {
        if (State.IsBusy || _loaderState is null || _loaderState.InstanceId != TargetInstance?.Id) return;
        IsLoaderConfirmationVisible = false;
        var state = _loaderState; var plan = _plan; bool remove = _remove;
        if (!remove && (plan is null || plan.CurrentState != state || plan.Request.InstanceId != TargetInstance.Id))
        { CancelConfirmation(); State.Error = "更换计划已失效，请重新检查加载器。"; return; }
        await State.RunAsync(remove ? "正在删除加载器…" : "正在更换加载器…", async context =>
        {
            if (remove && state.LoaderPatchId is { } patch)
                await State.Loaders.RemoveAsync(new(state.InstanceId, state.Revision, patch), context);
            else if (plan is not null) await State.Loaders.ReplaceAsync(plan, new(plan.PlanId, true), context);
        });
        CancelConfirmation(); await RefreshLoaderAsync();
    }
    [RelayCommand] private void CancelConfirmation() { IsLoaderConfirmationVisible = false; _plan = null; _remove = false; }
}
