using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace McDownload.ViewModel;

public partial class MainWindowViewModel : ObservableObject
{
    public WorkspaceViewModel State { get; }
    public SettingsViewModel Settings { get; }
    public DownloadViewModel Download { get; }
    public InstanceListViewModel Instances { get; }
    private readonly List<Task> _backgroundRefreshes = new();
    private string _taskReturnPage = "Instances";
    public Task BackgroundRefresh => Task.WhenAll(_backgroundRefreshes);
    [ObservableProperty] private string currentPage = "Instances";
    public MainWindowViewModel(HttpClient? http = null, string? settingsFile = null, Service.DiagnosticLog? diagnostics = null)
    {
        State = new(http, diagnostics);
        Settings = new(State, settingsFile); Download = new(State, Settings);
        Instances = new(State, async metadata =>
        {
            await Download.SelectInstanceAsync(metadata); CurrentPage = "Loaders";
        });
        State.Changed += (_, _) =>
        {
            if (State.IsBusy) return;
            _backgroundRefreshes.RemoveAll(x => x.IsCompleted);
            _backgroundRefreshes.Add(Instances.RefreshAsync());
        };
    }
    public async Task InitializeAsync() { await Settings.LoadAsync(); await Instances.RefreshAsync(); }
    [RelayCommand] private void OpenTasks()
    {
        if (CurrentPage == "Tasks") return;
        _taskReturnPage = CurrentPage;
        CurrentPage = "Tasks";
    }
    [RelayCommand] private void CloseTasks() => CurrentPage = _taskReturnPage;
    [RelayCommand] private async Task NavigateAsync(string page)
    {
        CurrentPage = page;
        if (page == "Instances") await Instances.RefreshAsync();
        if (page == "Download" && Download.MinecraftVersions.Count == 0) await Download.RefreshVersionsAsync();
    }
}
