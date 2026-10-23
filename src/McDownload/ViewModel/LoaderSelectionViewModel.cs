using McDownload.i18n;
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
    public IReadOnlyList<string> LoaderTypes { get; } = new[] { Text.Get("Common", "NoInstall"), Text.Get("Common", "Forge"), Text.Get("Common", "Fabric"), Text.Get("Common", "NeoForge") };
    public IReadOnlyList<string> Channels { get; } = new[] { Text.Get("Common", "All"), Text.Get("Common", "Release"), Text.Get("Common", "Snapshot") };
    public ObservableCollection<LoaderVersionEntry> Versions { get; } = new();
    [ObservableProperty] private string minecraftVersion = "";
    [ObservableProperty] private string selectedType = Text.Get("Common", "NoInstall");
    [ObservableProperty] private string selectedChannel = Text.Get("Common", "Release");
    [ObservableProperty] private LoaderVersionEntry? selectedVersion;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string message = Text.Get("LoaderSelection", "SelectLoader");
    public Task PendingRefresh { get; private set; } = Task.CompletedTask;
    public bool CanInstall => !IsLoading && (SelectedType == Text.Get("Common", "NoInstall") || SelectedVersion is not null);

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
            if (type == Text.Get("Common", "NoInstall")) { Message = Text.Get("LoaderSelection", "NoLoaderInstallation"); return; }
            if (string.IsNullOrEmpty(minecraft)) { Message = Text.Get("LoaderSelection", "SelectMinecraft"); return; }
            if (configuration == 0) { Message = Text.Get("LoaderSelection", "DirectoryNotReady"); return; }
            IsLoading = true; Message = Text.Get("LoaderSelection", "LoadingVersions");
            var versions = await _state.Catalog.GetVersionsAsync(minecraft, ParseLoaderKind(type), request.Token, forceRefresh);
            if (request.IsCancellationRequested || _request != request || configuration != _state.ConfigurationVersion) return;
            _allVersions = versions;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_request == request && configuration == _state.ConfigurationVersion)
            {
                Message = Text.Format("LoaderSelection", "VersionFetchFailed", Service.DiagnosticText.Summary(ex));
                _state.LogException(Text.Format("LoaderSelection", "VersionFetchLog", minecraft, type), ex);
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
        foreach (var version in _allVersions.Where(v => SelectedChannel == Text.Get("Common", "All") || v.IsStable == (SelectedChannel == Text.Get("Common", "Release"))))
            Versions.Add(version);
        SelectedVersion = Versions.FirstOrDefault(x => x == selected) ?? Versions.FirstOrDefault();
        Message = Versions.Count > 0 ? Text.Format("LoaderSelection", "VersionCount", MinecraftVersion, SelectedType, Versions.Count) :
            _allVersions.Count > 0 ? Text.Get("LoaderSelection", "NoFilteredVersions") : Text.Get("LoaderSelection", "LoaderUnavailable");
    }

    public LoaderSelection? GetSelection()
    {
        if (SelectedType == Text.Get("Common", "NoInstall")) return null;
        if (IsLoading || SelectedVersion is null || !Versions.Contains(SelectedVersion))
            throw new ArgumentException(Text.Get("LoaderSelection", "SelectFetchedVersion"));
        return new(ParseLoaderKind(SelectedType), SelectedVersion.Version);
    }

    private static LoaderKind ParseLoaderKind(string label)
    {
        if (label == Text.Get("Common", "Forge")) return LoaderKind.Forge;
        if (label == Text.Get("Common", "Fabric")) return LoaderKind.Fabric;
        if (label == Text.Get("Common", "NeoForge")) return LoaderKind.NeoForge;
        return Enum.Parse<LoaderKind>(label);
    }
}
