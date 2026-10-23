using McDownload.i18n;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McDownload.Model;

namespace McDownload.ViewModel;

public sealed record InstanceItem(InstalledVersionInfo Version, ModpackImportState? Modpack = null, string? ModpackError = null)
{
    public InstanceMetadata? Metadata => Version.Metadata;
    public string Name => Version.Name;
    public string MinecraftVersion => Version.MinecraftVersion;
    public string Loader => Version.LoaderDescription;
    public bool CanManage => Metadata is not null;
    public string Isolation => CanManage ? Text.Get("InstanceList", "ManagedIsolation") : Text.Get("InstanceList", "ReadOnlyVersion");
    public string Details => Version.Warning ?? (CanManage ? Text.Get("InstanceList", "IsolatedData") : Text.Get("InstanceList", "ReadOnlyDetails"));
    public bool HasModpack => Modpack is not null || ModpackError is not null;
    public bool CanResumeModpack => Modpack is { Status: not ModpackImportStatus.Completed };
    public string ModpackStatus => Modpack is { } state ? Text.Format("InstanceList", "ModpackStatus", DescribeImportStatus(state.Status)) : ModpackError ?? "";
    public static string DescribeImportStatus(ModpackImportStatus status) => status switch
    {
        ModpackImportStatus.Completed => Text.Get("Common", "Completed"), ModpackImportStatus.NeedsFiles => Text.Get("InstanceList", "NeedsFiles"),
        ModpackImportStatus.Canceled => Text.Get("InstanceList", "ImportCanceled"), ModpackImportStatus.Failed => Text.Get("InstanceList", "ImportFailed"), _ => Text.Get("InstanceList", "ImportIncomplete")
    };
}

public partial class InstanceListViewModel : ObservableObject
{
    public WorkspaceViewModel State { get; }
    private readonly Func<InstanceMetadata, Task> _manage;
    private readonly Func<InstanceMetadata, Task>? _resumeModpack;
    public ObservableCollection<InstanceItem> Instances { get; } = new();
    [ObservableProperty] private InstanceItem? selectedInstance;
    [ObservableProperty] private bool isDeleteConfirmationVisible;
    [ObservableProperty] private string deleteWarning = "";
    private InstanceDeleteRequest? _pendingDelete;
    private long _refreshGeneration;
    public InstanceListViewModel(WorkspaceViewModel state, Func<InstanceMetadata, Task> manage, Func<InstanceMetadata, Task>? resumeModpack = null)
    {
        State = state; _manage = manage; _resumeModpack = resumeModpack;
        state.ConfigurationChanged += (_, _) => { _refreshGeneration++; SelectedInstance = null; Instances.Clear(); };
    }
    partial void OnSelectedInstanceChanged(InstanceItem? value) { IsDeleteConfirmationVisible = false; _pendingDelete = null; }
    [RelayCommand] public async Task RefreshAsync()
    {
        if (State.IsBusy) return;
        try
        {
            long generation = ++_refreshGeneration, configuration = State.ConfigurationVersion;
            string? directory = SelectedInstance?.Version.DirectoryPath;
            var items = await State.InstalledVersions.ScanAsync(default);
            var loaded = new List<InstanceItem>();
            foreach (var item in items)
            {
                ModpackImportState? pack = null; string? error = null;
                if (item.Metadata is { } metadata)
                {
                    try { pack = await State.Modpacks.GetStateAsync(metadata.Id, default); }
                    catch (Exception ex) { error = Text.Format("InstanceList", "ModpackStateReadFailed", Service.DiagnosticText.Summary(ex)); }
                }
                loaded.Add(new(item, pack, error));
            }
            if (generation != _refreshGeneration || configuration != State.ConfigurationVersion) return;
            Instances.Clear();
            foreach (var item in loaded) Instances.Add(item);
            SelectedInstance = Instances.FirstOrDefault(x => x.Version.DirectoryPath == directory) ?? Instances.FirstOrDefault();
        }
        catch (Exception ex) { State.ReportError(Text.Get("InstanceList", "ListReadFailed"), ex); }
    }
    [RelayCommand] private async Task ManageLoaderAsync()
    { if (SelectedInstance?.Metadata is { } metadata) await _manage(metadata); }
    [RelayCommand] private async Task ResumeModpackAsync()
    {
        if (!State.IsBusy && SelectedInstance is { CanResumeModpack: true, Metadata: { } metadata } && _resumeModpack is not null)
            await _resumeModpack(metadata);
    }
    [RelayCommand] private async Task OpenFolderAsync()
    {
        if (SelectedInstance is null) return;
        try
        {
            string directory = SelectedInstance.Version.DirectoryPath;
            if (SelectedInstance.Metadata is { } metadata)
                directory = (await State.Instances.ResolvePathsAsync(metadata.Id, default)).GameDirectory;
            directory = Service.InstanceManager.Normal(directory);
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { directory } });
        }
        catch (Exception ex) { State.ReportError(Text.Get("InstanceList", "DirectoryOpenFailed"), ex); }
    }
    [RelayCommand] private async Task DeleteAsync()
    {
        if (State.IsBusy || SelectedInstance?.Metadata is not { } metadata) return;
        InstanceDeleteRequest? pending = null;
        try
        {
            var item = await State.Instances.GetAsync(metadata.Id, default);
            var request = new InstanceDeleteRequest(item.Id, item.Revision, InstanceDeleteMode.RecycleInstanceDirectory, false);
            pending = request with { UserDataDeletionConfirmed = true };
            await State.Instances.DeleteAsync(request, default);
        }
        catch (UserConfirmationRequiredException ex)
        {
            _pendingDelete = pending;
            DeleteWarning = ex.Message; IsDeleteConfirmationVisible = true;
        }
        catch (Exception ex) { State.ReportError(Text.Get("InstanceList", "DeletePreparationFailed"), ex); }
    }
    [RelayCommand] private async Task ConfirmDeleteAsync()
    {
        if (State.IsBusy || _pendingDelete is not { } request || SelectedInstance?.Metadata?.Id != request.InstanceId) return;
        IsDeleteConfirmationVisible = false;
        await State.RunAsync(Text.Get("InstanceList", "RecyclingInstance"), context => State.Instances.DeleteAsync(request, context.CancellationToken));
        _pendingDelete = null;
        await RefreshAsync();
    }
    [RelayCommand] private void CancelDelete() { _pendingDelete = null; IsDeleteConfirmationVisible = false; }
}
