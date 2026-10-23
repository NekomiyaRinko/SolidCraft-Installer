using McDownload.i18n;
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
    public IReadOnlyList<string> Channels { get; } = new[] { Text.Get("Common", "All"), Text.Get("Common", "Release"), Text.Get("Common", "Snapshot") };
    [ObservableProperty] private string selectedMinecraftChannel = Text.Get("Common", "Release");
    public LoaderSelectionViewModel NewGameLoader { get; }
    public LoaderSelectionViewModel InstanceLoader { get; }
    public ModpackImportViewModel Modpacks { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMinecraftTab), nameof(IsPageBusy), nameof(IsPageInteractive))]
    private bool isModpackTab;
    public bool IsMinecraftTab => !IsModpackTab;
    [ObservableProperty] private MinecraftVersionEntry? selectedMinecraftVersion;
    [ObservableProperty] private string instanceName = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageBusy), nameof(IsPageInteractive), nameof(PageBusyMessage))]
    private bool isRefreshingVersions;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageBusy), nameof(IsPageInteractive), nameof(PageBusyMessage))]
    private bool isSubmittingGame;
    public bool IsPageBusy => !IsModpackTab && (IsRefreshingVersions || NewGameLoader.IsLoading || IsSubmittingGame);
    public bool IsPageInteractive => !IsPageBusy;
    public string PageBusyMessage => IsSubmittingGame ? Text.Get("Download", "AddingTask") : Text.Get("Download", "LoadingVersions");
    internal Task SubmissionFeedback { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private InstanceMetadata? targetInstance;
    [ObservableProperty] private string currentLoader = Text.Get("Common", "NoLoaderInstalled");
    [ObservableProperty] private bool isLoaderConfirmationVisible;
    [ObservableProperty] private string confirmationText = "";
    private LoaderState? _loaderState;
    private LoaderReplacementPlan? _plan;
    private bool _remove;
    private long _refreshGeneration;
    public static string LoaderWarning => Text.Get("Download", "LoaderWarning");
    public DownloadViewModel(WorkspaceViewModel state, SettingsViewModel settings)
    {
        State = state; _settings = settings;
        Modpacks = new(state, settings);
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
    [RelayCommand] private void SelectModpackTab() => IsModpackTab = true;
    [RelayCommand] private async Task SelectMinecraftTabAsync()
    {
        IsModpackTab = false;
        if (MinecraftVersions.Count == 0) await RefreshVersionsAsync();
    }
    partial void OnSelectedMinecraftVersionChanged(MinecraftVersionEntry? value) => NewGameLoader.MinecraftVersion = value?.Id ?? "";
    partial void OnSelectedMinecraftChannelChanged(string value) => ApplyMinecraftFilter();
    private void ApplyMinecraftFilter()
    {
        string? selected = SelectedMinecraftVersion?.Id;
        MinecraftVersions.Clear();
        foreach (var version in _allMinecraftVersions.Where(x => SelectedMinecraftChannel == Text.Get("Common", "All") ||
            (x.Channel == MinecraftVersionChannel.Release) == (SelectedMinecraftChannel == Text.Get("Common", "Release")))) MinecraftVersions.Add(version);
        SelectedMinecraftVersion = MinecraftVersions.FirstOrDefault(x => x.Id == selected) ?? MinecraftVersions.FirstOrDefault();
    }
    partial void OnTargetInstanceChanged(InstanceMetadata? value)
    {
        _refreshGeneration++;
        _loaderState = null;
        CurrentLoader = Text.Get("Download", "LoaderStateUnread");
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
            CurrentLoader = _loaderState.InstalledLoader is { } value ? Text.Format("Common", "InstalledLoader", DisplayNames.Loader(value.Kind), value.Version) : Text.Get("Common", "NoLoaderInstalled");
            return true;
        }
        catch (Exception ex)
        {
            if (generation == _refreshGeneration && TargetInstance?.Id == target.Id)
            { CurrentLoader = Text.Get("Download", "LoaderStateReadFailed"); State.ReportError(CurrentLoader, ex); }
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
                    State.ReportError(Text.Get("Download", "VersionListFailed"), ex);
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
        try { selection = NewGameLoader.GetSelection(); } catch (Exception ex) { State.ReportError(Text.Get("Download", "InvalidInstallArguments"), ex); return; }
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
                    if (report.Status != CompatibilityStatus.Compatible) throw new InvalidOperationException(string.Join(Text.Get("Common", "IssueSeparator"), report.Issues.Select(x => x.Message)));
                }
                var metadata = await State.Instances.CreateAsync(new(name, version.Id, IsolationPolicy.PerInstance,
                    new(name, null, string.IsNullOrWhiteSpace(java) ? null : java)), context.CancellationToken);
                installed = metadata;
                State.Log(Text.Format("Download", "InstanceCreated", metadata.Editable.DisplayName));
                State.Log(Text.Get("Download", "ResumeDownloadHint"));
                installed = await State.Games.InstallAsync(metadata.Id, version, selection is null ? context : context with
                {
                    Progress = new MappedProgress(context.Progress, p => p.Step == OperationStep.Saving
                        ? p with { Step = OperationStep.Assets, StageFraction = 1 } : p)
                });
                if (selection is not null)
                {
                    context.Progress?.Report(new(context.OperationId, installed.Id, 0, OperationStage.InstallingLoader,
                        null, null, 0, null, Text.Format("Download", "InstallingLoader", DisplayNames.Loader(selection.Kind)), OperationStep.Loader));
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
            (OperationStep.Preparing, Text.Get("Download", "PrepareInstance")), (OperationStep.Client, Text.Get("Common", "DownloadClient")),
            (OperationStep.Libraries, Text.Get("Common", "DownloadLibraries")), (OperationStep.Assets, Text.Get("Common", "DownloadAssets"))
        };
        if (loader is not null) steps.Add((OperationStep.Loader, Text.Format("Download", "InstallLoader", DisplayNames.Loader(loader.Kind))));
        steps.Add((OperationStep.Saving, Text.Get("Download", "SaveInstance")));
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
        await State.EnqueueAsync(Text.Format("Download", "ResumeGameTitle", target.Editable.DisplayName), async context =>
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
        _remove = true; _plan = null; ConfirmationText = Text.Get("Download", "RemoveLoaderConfirmation"); IsLoaderConfirmationVisible = true;
    }
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task InstallLoaderAsync()
    {
        if (TargetInstance is null) return;
        LoaderSelection? selection;
        try { selection = InstanceLoader.GetSelection() ?? throw new ArgumentException(Text.Get("Download", "SelectLoader")); }
        catch (Exception ex) { State.ReportError(Text.Get("Download", "InvalidLoaderSelection"), ex); return; }
        var target = TargetInstance;
        var targetId = target.Id;
        string java = _settings.JavaPath;
        long configuration = State.ConfigurationVersion;
        if (target.InstalledLoader is not null)
        {
            if (State.IsBusy) { State.Error = Text.Get("Download", "WaitForQueue"); return; }
            if (!await RefreshLoaderAsync() || _loaderState is null || TargetInstance?.Id != targetId) return;
            var loader = _loaderState;
            await State.RunAsync(Text.Get("Download", "CheckingCompatibility"), async context =>
            {
                var prepared = await State.Loaders.PrepareReplacementAsync(new(targetId, loader.Revision, selection, java), context.CancellationToken);
                if (TargetInstance?.Id != targetId || _loaderState != loader || InstanceLoader.GetSelection() != selection)
                    throw new InvalidOperationException(Text.Get("Download", "SelectionChanged"));
                _plan = prepared;
                if (_plan.Compatibility.Status != CompatibilityStatus.Compatible)
                    throw new InvalidOperationException(string.Join(Text.Get("Common", "IssueSeparator"), _plan.Compatibility.Issues.Select(x => x.Message)));
                _remove = false; ConfirmationText = Text.Format("Download", "ReplacementConfirmation", CurrentLoader, DisplayNames.Loader(selection.Kind), selection.Version);
                IsLoaderConfirmationVisible = true;
            });
            return;
        }
        await State.EnqueueAsync(Text.Format("Download", "InstallLoaderTitle", target.Editable.DisplayName, DisplayNames.Loader(selection.Kind)), context => State.Loaders.InstallAsync(
            new(targetId, target.Revision, selection, java), LoaderContext(context)), new[]
            {
                (OperationStep.Preparing, Text.Get("Download", "CheckInstance")), (OperationStep.Loader, Text.Format("Download", "InstallLoader", DisplayNames.Loader(selection.Kind))),
                (OperationStep.Saving, Text.Get("Download", "SaveInstance"))
            });
        if (configuration == State.ConfigurationVersion && TargetInstance?.Id == targetId) await RefreshLoaderAsync();
    }
    [RelayCommand] private async Task ConfirmLoaderAsync()
    {
        if (State.IsBusy || _loaderState is null || _loaderState.InstanceId != TargetInstance?.Id) return;
        IsLoaderConfirmationVisible = false;
        var state = _loaderState; var plan = _plan; bool remove = _remove;
        if (!remove && (plan is null || plan.CurrentState != state || plan.Request.InstanceId != TargetInstance.Id))
        { CancelConfirmation(); State.Error = Text.Get("Download", "ReplacementExpired"); return; }
        await State.RunAsync(remove ? Text.Get("Download", "RemovingLoader") : Text.Get("Download", "ReplacingLoader"), async context =>
        {
            if (remove && state.LoaderPatchId is { } patch)
                await State.Loaders.RemoveAsync(new(state.InstanceId, state.Revision, patch), context);
            else if (plan is not null) await State.Loaders.ReplaceAsync(plan, new(plan.PlanId, true), context);
        });
        CancelConfirmation(); await RefreshLoaderAsync();
    }
    [RelayCommand] private void CancelConfirmation() { IsLoaderConfirmationVisible = false; _plan = null; _remove = false; }
}
