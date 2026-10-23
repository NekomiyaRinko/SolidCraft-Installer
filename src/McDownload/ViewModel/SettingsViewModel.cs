using McDownload.i18n;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using McDownload.Model;

namespace McDownload.ViewModel;

public partial class SettingsViewModel : ObservableObject
{
    private readonly WorkspaceViewModel _state;
    private readonly string _filePath;
    private readonly Service.CurseForgeCredentialStore _credentials;
    private readonly Action<string> _openExternal;
    public const string CurseForgeConsoleUrl = "https://console.curseforge.com/";
    [ObservableProperty] private bool isExternalLinkConfirmationVisible;
    [ObservableProperty] private bool rememberCurseForgeKey;
    [ObservableProperty] private bool isUpdatingCurseForgeKey;
    [ObservableProperty] private string curseForgeMessage = Text.Get("Settings", "CredentialMissing");
    public bool CanEditCurseForgeKey => !_state.IsBusy && !IsUpdatingCurseForgeKey;
    public bool HasConfiguredCurseForgeKey => _state.HasCurseForgeApiKey;
    private bool _autoSaveEnabled;
    private long _saveVersion;
    private int _pendingSaves;
    public Task PendingSave { get; private set; } = Task.CompletedTask;
    public static string DefaultMinecraftRoot => Path.Combine(AppContext.BaseDirectory, ".minecraft");
    [ObservableProperty] private string javaPath = "";
    [ObservableProperty] private int defaultMemoryMb = 4096;
    [ObservableProperty] private int maxDownloadThreads = 8;
    [ObservableProperty] private string minecraftRoot = DefaultMinecraftRoot;
    [ObservableProperty] private string selectedDownloadSource = Text.Get("Settings", "BmclapiSource");
    public IReadOnlyList<string> DownloadSources { get; } = new[] { Text.Get("Settings", "BmclapiSource"), Text.Get("Settings", "OfficialSource") };
    [ObservableProperty] private string message = "";
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private bool isManagingLogs;
    [ObservableProperty] private bool isLogDeleteConfirmationVisible;
    [ObservableProperty] private string logMessage = "";
    public string LogDeleteWarning => Text.Get("Settings", "LogDeleteWarning");
    public string AssetsPath => Path.Combine(MinecraftRoot, "assets");
    public string LibrariesPath => Path.Combine(MinecraftRoot, "libraries");
    public string DiagnosticDirectory => _state.DiagnosticDirectory;
    public string? DiagnosticLogPath => _state.DiagnosticLogPath;
    public WorkspaceViewModel State => _state;
    public SettingsViewModel(WorkspaceViewModel state, string? filePath = null, Action<string>? openExternal = null)
    {
        _state = state;
        _openExternal = openExternal ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCI", "settings.json");
        _credentials = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_filePath))!, "curseforge-key.dpapi"));
        _state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WorkspaceViewModel.DiagnosticLogPath))
            { OnPropertyChanged(nameof(DiagnosticLogPath)); OnPropertyChanged(nameof(DiagnosticDirectory)); }
            if (args.PropertyName == nameof(WorkspaceViewModel.IsBusy)) OnPropertyChanged(nameof(CanEditCurseForgeKey));
            if (args.PropertyName == nameof(WorkspaceViewModel.HasCurseForgeApiKey)) OnPropertyChanged(nameof(HasConfiguredCurseForgeKey));
        };
    }
    public void ConfirmOpenCurseForgeConsole()
    {
        if (!IsExternalLinkConfirmationVisible) return;
        IsExternalLinkConfirmationVisible = false;
        try { _openExternal(CurseForgeConsoleUrl); }
        catch (Exception) { CurseForgeMessage = Text.Get("Settings", "BrowserOpenFailed"); }
    }
    partial void OnIsUpdatingCurseForgeKeyChanged(bool value) => OnPropertyChanged(nameof(CanEditCurseForgeKey));
    public async Task<bool> ApplyCurseForgeKeyAsync(string? key)
    {
        if (!CanEditCurseForgeKey) return false;
        IsUpdatingCurseForgeKey = true; _state.IsUpdatingCredentials = true;
        try
        {
            if (key is null)
            {
                if (!_state.HasCurseForgeApiKey) return false;
                if (RememberCurseForgeKey) await _state.RememberCurseForgeApiKeyAsync(_credentials);
                else _credentials.Clear();
            }
            else
            {
                key = Service.CurseForgeCredentialStore.Validate(key);
                if (RememberCurseForgeKey) await _credentials.SaveAsync(key);
                else _credentials.Clear();
                _state.SetCurseForgeApiKey(key);
            }
            CurseForgeMessage = RememberCurseForgeKey ? Text.Get("Settings", "CredentialSaved") : Text.Get("Settings", "CredentialConfigured");
            return true;
        }
        catch (ArgumentException) { CurseForgeMessage = Text.Get("Settings", "InvalidApiKey"); }
        catch (Exception) { CurseForgeMessage = Text.Get("Settings", "CredentialApplyFailed"); }
        finally { IsUpdatingCurseForgeKey = false; _state.IsUpdatingCredentials = false; }
        return false;
    }
    [RelayCommand] private void ClearCurseForgeKey()
    {
        if (!CanEditCurseForgeKey) return;
        try
        {
            _credentials.Clear();
            _state.SetCurseForgeApiKey(null);
            RememberCurseForgeKey = false;
            CurseForgeMessage = Text.Get("Settings", "CredentialCleared");
        }
        catch (Exception) { CurseForgeMessage = Text.Get("Settings", "CredentialClearFailed"); }
    }
    private DownloadSource Source => SelectedDownloadSource == Text.Get("Settings", "OfficialSource") ? DownloadSource.Official : DownloadSource.Bmclapi;
    public static string ResolveMinecraftRoot(IReadOnlyDictionary<string, string> settings)
    {
        string oldDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCI", "Minecraft");
        string? saved = settings.GetValueOrDefault("root");
        return settings.GetValueOrDefault("rootMode") == "portable" || string.IsNullOrWhiteSpace(saved) ||
            (settings.GetValueOrDefault("rootMode") is null && saved.Equals(oldDefault, StringComparison.OrdinalIgnoreCase))
            ? DefaultMinecraftRoot : saved;
    }
    partial void OnMinecraftRootChanged(string value)
    { OnPropertyChanged(nameof(AssetsPath)); OnPropertyChanged(nameof(LibrariesPath)); AutoSave(); }
    partial void OnJavaPathChanged(string value) => AutoSave();
    partial void OnDefaultMemoryMbChanged(int value) => AutoSave();
    partial void OnMaxDownloadThreadsChanged(int value) => AutoSave();
    partial void OnSelectedDownloadSourceChanged(string value) => AutoSave();
    private void AutoSave() { if (_autoSaveEnabled) _ = SaveAsync(); }
    public async Task LoadAsync()
    {
        try
        {
            var key = await _credentials.LoadAsync();
            if (key is not null)
            {
                _state.SetCurseForgeApiKey(key); RememberCurseForgeKey = true;
                CurseForgeMessage = Text.Get("Settings", "CredentialLoaded");
            }
        }
        catch (Exception) { CurseForgeMessage = Text.Get("Settings", "CredentialLoadFailed"); }
        if (File.Exists(Service.InstanceManager.Extended(_filePath)))
        {
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(await Service.InstanceManager.ReadBytesAsync(_filePath, 1024 * 1024, default));
                if (data is not null)
                {
                    JavaPath = data.GetValueOrDefault("java", "");
                    MinecraftRoot = ResolveMinecraftRoot(data);
                    SelectedDownloadSource = data.GetValueOrDefault("source") == "Official" ? Text.Get("Settings", "OfficialSource") : DownloadSources[0];
                    if (int.TryParse(data.GetValueOrDefault("memory"), out int memory)) DefaultMemoryMb = memory;
                    if (int.TryParse(data.GetValueOrDefault("maxDownloadThreads"), out int threads) && threads is >= 1 and <= 64)
                        MaxDownloadThreads = threads;
                }
            }
            catch (Exception ex)
            { Message = Text.Format("Settings", "SettingsReadFailed", Service.DiagnosticText.Redact(ex.Message)); _state.LogException(Text.Get("Settings", "SettingsReadLog"), ex); }
        }
        _state.Configure(MinecraftRoot, Source, MaxDownloadThreads);
        _autoSaveEnabled = true;
    }
    [RelayCommand] private Task SaveAsync()
    {
        long version = ++_saveVersion;
        if (_state.IsBusy) { Message = Text.Get("Settings", "WaitForQueue"); return PendingSave; }
        try
        {
            if (DefaultMemoryMb < 512 || DefaultMemoryMb > 1048576) throw new ArgumentException(Text.Get("Settings", "MemoryRange"));
            if (MaxDownloadThreads is < 1 or > 64) throw new ArgumentException(Text.Get("Settings", "ThreadRange"));
            if (JavaPath.Length > 0 && (!File.Exists(Service.InstanceManager.Extended(JavaPath)) || !JavaPath.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException(Text.Get("Common", "InvalidJava"));
            if (!Path.IsPathFullyQualified(MinecraftRoot)) throw new ArgumentException(Text.Get("Settings", "AbsoluteGamePath"));
            string root = Service.InstanceManager.Normal(MinecraftRoot);
            if (_state.ConfigurationVersion == 0 || !_state.Instances.MinecraftRoot.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                _state.DownloadProvider.Source != Source || _state.MaxDownloadThreads != MaxDownloadThreads)
                _state.Configure(root, Source, MaxDownloadThreads);
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            { ["java"] = JavaPath, ["root"] = MinecraftRoot, ["memory"] = DefaultMemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["source"] = Source.ToString(), ["maxDownloadThreads"] = MaxDownloadThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["rootMode"] = root.Equals(Path.GetFullPath(DefaultMinecraftRoot), StringComparison.OrdinalIgnoreCase) ? "portable" : "custom" });
            IsSaving = true; _pendingSaves++; Message = Text.Get("Settings", "Saving");
            PendingSave = PersistAsync(PendingSave, data, version);
            return PendingSave;
        }
        catch (Exception ex) { Message = Service.DiagnosticText.Redact(ex.Message); _state.LogException(Text.Get("Settings", "SettingsSaveLog"), ex); }
        return PendingSave;
    }

    private async Task PersistAsync(Task previous, byte[] data, long version)
    {
        try
        {
            await previous;
            await Service.InstanceManager.AtomicBytesAsync(_filePath, data, default);
            if (version == _saveVersion) Message = Text.Get("Settings", "AutoSaved");
        }
        catch (Exception ex)
        {
            if (version == _saveVersion) Message = Text.Format("Settings", "SaveFailed", Service.DiagnosticText.Summary(ex));
            _state.LogException(Text.Get("Settings", "SettingsSaveLog"), ex);
        }
        finally { _pendingSaves--; IsSaving = _pendingSaves > 0; }
    }
    [RelayCommand] private void BrowseJava()
    {
        var dialog = new OpenFileDialog { Filter = Text.Get("Common", "JavaFileFilter"), CheckFileExists = true };
        if (dialog.ShowDialog() == true) JavaPath = dialog.FileName;
    }
    [RelayCommand] private void UseDefaultRoot() => MinecraftRoot = DefaultMinecraftRoot;
    [RelayCommand] private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(Service.InstanceManager.Extended(DiagnosticDirectory));
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { DiagnosticDirectory } });
        }
        catch (Exception ex) { _state.ReportError(Text.Get("Settings", "LogDirectoryOpenFailed"), ex); }
    }
    [RelayCommand] private async Task ExportLogsAsync()
    {
        if (IsManagingLogs) return;
        var dialog = new SaveFileDialog
        {
            Title = Text.Get("Settings", "ExportLogs"), Filter = Text.Get("Settings", "ZipFilter"), DefaultExt = ".zip",
            FileName = $"SCI-Logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog() == true) await ExportLogsToAsync(dialog.FileName);
    }
    public async Task ExportLogsToAsync(string destination)
    {
        if (IsManagingLogs) return;
        IsManagingLogs = true;
        try { int count = await _state.ExportLogsAsync(destination); LogMessage = Text.Format("Settings", "LogsExported", count); }
        catch (Exception ex) { LogMessage = Text.Format("Settings", "ExportFailed", Service.DiagnosticText.Summary(ex)); _state.LogException(Text.Get("Settings", "ExportLog"), ex); }
        finally { IsManagingLogs = false; }
    }
    [RelayCommand] private void RequestDeleteLogs()
    {
        if (!IsManagingLogs) IsLogDeleteConfirmationVisible = true;
    }
    [RelayCommand] private void CancelDeleteLogs() => IsLogDeleteConfirmationVisible = false;
    [RelayCommand] private async Task ConfirmDeleteLogsAsync()
    {
        if (!IsLogDeleteConfirmationVisible || IsManagingLogs) return;
        IsLogDeleteConfirmationVisible = false;
        IsManagingLogs = true;
        try { int count = await _state.RecycleLogsAsync(); LogMessage = Text.Format("Settings", "LogsRecycled", count); }
        catch (Exception ex) { LogMessage = Text.Format("Settings", "RecycleIncomplete", Service.DiagnosticText.Summary(ex)); _state.LogException(Text.Get("Settings", "RecycleLog"), ex); }
        finally { IsManagingLogs = false; }
    }
    [RelayCommand] private void BrowseRoot()
    {
        var dialog = new OpenFolderDialog { Title = Text.Get("Settings", "SelectMinecraftDirectory") };
        if (dialog.ShowDialog() == true) MinecraftRoot = dialog.FolderName;
    }
}
