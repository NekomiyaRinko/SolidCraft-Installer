using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McDownload.Model;

namespace McDownload.ViewModel;

public sealed record InstanceItem(InstalledVersionInfo Version)
{
    public InstanceMetadata? Metadata => Version.Metadata;
    public string Name => Version.Name;
    public string MinecraftVersion => Version.MinecraftVersion;
    public string Loader => Version.LoaderDescription;
    public bool CanManage => Metadata is not null;
    public string Isolation => CanManage ? "SCI 实例 · 模组与存档独立" : "已有版本 · 只读";
    public string Details => Version.Warning ?? (CanManage ? "模组与存档独立" : "非 SCI 创建的版本，仅查看信息和打开文件夹。");
}

public partial class InstanceListViewModel : ObservableObject
{
    public WorkspaceViewModel State { get; }
    private readonly Func<InstanceMetadata, Task> _manage;
    public ObservableCollection<InstanceItem> Instances { get; } = new();
    [ObservableProperty] private InstanceItem? selectedInstance;
    [ObservableProperty] private bool isDeleteConfirmationVisible;
    [ObservableProperty] private string deleteWarning = "";
    private InstanceDeleteRequest? _pendingDelete;
    private long _refreshGeneration;
    public InstanceListViewModel(WorkspaceViewModel state, Func<InstanceMetadata, Task> manage)
    {
        State = state; _manage = manage;
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
            if (generation != _refreshGeneration || configuration != State.ConfigurationVersion) return;
            Instances.Clear();
            foreach (var item in items) Instances.Add(new(item));
            SelectedInstance = Instances.FirstOrDefault(x => x.Version.DirectoryPath == directory) ?? Instances.FirstOrDefault();
        }
        catch (Exception ex) { State.ReportError("实例列表读取失败", ex); }
    }
    [RelayCommand] private async Task ManageLoaderAsync()
    { if (SelectedInstance?.Metadata is { } metadata) await _manage(metadata); }
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
        catch (Exception ex) { State.ReportError("实例目录打开失败", ex); }
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
        catch (Exception ex) { State.ReportError("实例删除准备失败", ex); }
    }
    [RelayCommand] private async Task ConfirmDeleteAsync()
    {
        if (State.IsBusy || _pendingDelete is not { } request || SelectedInstance?.Metadata?.Id != request.InstanceId) return;
        IsDeleteConfirmationVisible = false;
        await State.RunAsync("正在回收实例…", context => State.Instances.DeleteAsync(request, context.CancellationToken));
        _pendingDelete = null;
        await RefreshAsync();
    }
    [RelayCommand] private void CancelDelete() { _pendingDelete = null; IsDeleteConfirmationVisible = false; }
}
