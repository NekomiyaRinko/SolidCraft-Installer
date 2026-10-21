using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McDownload.Model;

namespace McDownload.ViewModel;

public partial class LoaderSelectionViewModel : ObservableObject
{
    private readonly WorkspaceViewModel _state;
    private IReadOnlyList<LoaderVersionEntry> _allVersions = Array.Empty<LoaderVersionEntry>();
    private CancellationTokenSource? _request;
    public IReadOnlyList<string> LoaderTypes { get; } = new[] { "不安装", "Forge", "Fabric", "NeoForge" };
    public IReadOnlyList<string> Channels { get; } = new[] { "全部", "正式版", "测试版" };
    public ObservableCollection<LoaderVersionEntry> Versions { get; } = new();
    [ObservableProperty] private string minecraftVersion = "";
    [ObservableProperty] private string selectedType = "不安装";
    [ObservableProperty] private string selectedChannel = "正式版";
    [ObservableProperty] private LoaderVersionEntry? selectedVersion;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string message = "请选择加载器。";
    public Task PendingRefresh { get; private set; } = Task.CompletedTask;
    public bool CanInstall => !IsLoading && (SelectedType == "不安装" || SelectedVersion is not null);

    public LoaderSelectionViewModel(WorkspaceViewModel state)
    {
        _state = state;
        state.ConfigurationChanged += (_, _) => QueueRefresh();
    }
    partial void OnMinecraftVersionChanged(string value) => QueueRefresh();
    partial void OnSelectedTypeChanged(string value) => QueueRefresh();
    partial void OnSelectedChannelChanged(string value) => ApplyFilter();
    partial void OnSelectedVersionChanged(LoaderVersionEntry? value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    private void QueueRefresh() => PendingRefresh = RefreshAsync();

    [RelayCommand] private Task ReloadAsync() => RefreshAsync(true);
    public async Task RefreshAsync(bool forceRefresh = false)
    {
        _request?.Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        long configuration = _state.ConfigurationVersion;
        string minecraft = MinecraftVersion, type = SelectedType;
        _allVersions = Array.Empty<LoaderVersionEntry>();
        Versions.Clear(); SelectedVersion = null;
        try
        {
            if (type == "不安装") { Message = "不安装加载器。"; return; }
            if (string.IsNullOrEmpty(minecraft)) { Message = "请先选择 Minecraft 版本。"; return; }
            if (configuration == 0) { Message = "游戏目录尚未就绪。"; return; }
            IsLoading = true; Message = "正在获取版本…";
            var versions = await _state.Catalog.GetVersionsAsync(minecraft, Enum.Parse<LoaderKind>(type), request.Token, forceRefresh);
            if (request.IsCancellationRequested || _request != request || configuration != _state.ConfigurationVersion) return;
            _allVersions = versions;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_request == request && configuration == _state.ConfigurationVersion)
            {
                Message = "版本获取失败，请重试：" + Service.DiagnosticText.Summary(ex);
                _state.LogException($"加载器版本获取失败：{minecraft} / {type}", ex);
            }
        }
        finally
        {
            if (_request == request) { IsLoading = false; _request = null; }
        }
    }

    private void ApplyFilter()
    {
        var selected = SelectedVersion;
        Versions.Clear();
        foreach (var version in _allVersions.Where(v => SelectedChannel == "全部" || v.IsStable == (SelectedChannel == "正式版")))
            Versions.Add(version);
        SelectedVersion = Versions.FirstOrDefault(x => x == selected) ?? Versions.FirstOrDefault();
        Message = Versions.Count > 0 ? $"{MinecraftVersion} · {SelectedType} · {Versions.Count} 个版本" :
            _allVersions.Count > 0 ? "当前筛选下没有版本，可切换为“全部”或“测试版”。" : "此 Minecraft 版本暂无该加载器。";
    }

    public LoaderSelection? GetSelection()
    {
        if (SelectedType == "不安装") return null;
        if (IsLoading || SelectedVersion is null || !Versions.Contains(SelectedVersion))
            throw new ArgumentException("请选择已获取的加载器版本。");
        return new(Enum.Parse<LoaderKind>(SelectedType), SelectedVersion.Version);
    }
}
