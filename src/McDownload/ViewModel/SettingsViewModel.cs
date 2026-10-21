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
    private bool _autoSaveEnabled;
    private long _saveVersion;
    private int _pendingSaves;
    public Task PendingSave { get; private set; } = Task.CompletedTask;
    public static string DefaultMinecraftRoot => Path.Combine(AppContext.BaseDirectory, ".minecraft");
    [ObservableProperty] private string javaPath = "";
    [ObservableProperty] private int defaultMemoryMb = 4096;
    [ObservableProperty] private string minecraftRoot = DefaultMinecraftRoot;
    [ObservableProperty] private string selectedDownloadSource = "BMCLAPI（失败回退官方）";
    public IReadOnlyList<string> DownloadSources { get; } = new[] { "BMCLAPI（失败回退官方）", "官方" };
    [ObservableProperty] private string message = "";
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private bool isManagingLogs;
    [ObservableProperty] private bool isLogDeleteConfirmationVisible;
    [ObservableProperty] private string logMessage = "";
    public string LogDeleteWarning => "将 SCI 诊断日志移至回收站，并重新开始当前日志。任务记录和游戏文件保持不变。";
    public string AssetsPath => Path.Combine(MinecraftRoot, "assets");
    public string LibrariesPath => Path.Combine(MinecraftRoot, "libraries");
    public string DiagnosticDirectory => _state.DiagnosticDirectory;
    public string? DiagnosticLogPath => _state.DiagnosticLogPath;
    public WorkspaceViewModel State => _state;
    public SettingsViewModel(WorkspaceViewModel state, string? filePath = null)
    {
        _state = state;
        _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCI", "settings.json");
        _state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WorkspaceViewModel.DiagnosticLogPath))
            { OnPropertyChanged(nameof(DiagnosticLogPath)); OnPropertyChanged(nameof(DiagnosticDirectory)); }
        };
    }
    private DownloadSource Source => SelectedDownloadSource == "官方" ? DownloadSource.Official : DownloadSource.Bmclapi;
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
    partial void OnSelectedDownloadSourceChanged(string value) => AutoSave();
    private void AutoSave() { if (_autoSaveEnabled) _ = SaveAsync(); }
    public async Task LoadAsync()
    {
        if (File.Exists(Service.InstanceManager.Extended(_filePath)))
        {
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(await Service.InstanceManager.ReadBytesAsync(_filePath, 1024 * 1024, default));
                if (data is not null)
                {
                    JavaPath = data.GetValueOrDefault("java", "");
                    MinecraftRoot = ResolveMinecraftRoot(data);
                    SelectedDownloadSource = data.GetValueOrDefault("source") == "Official" ? "官方" : DownloadSources[0];
                    if (int.TryParse(data.GetValueOrDefault("memory"), out int memory)) DefaultMemoryMb = memory;
                }
            }
            catch (Exception ex)
            { Message = "设置读取失败：" + Service.DiagnosticText.Redact(ex.Message); _state.LogException("设置读取失败", ex); }
        }
        _state.Configure(MinecraftRoot, Source);
        _autoSaveEnabled = true;
    }
    [RelayCommand] private Task SaveAsync()
    {
        long version = ++_saveVersion;
        if (_state.IsBusy) { Message = "请等待任务队列结束后修改设置。"; return PendingSave; }
        try
        {
            if (DefaultMemoryMb < 512 || DefaultMemoryMb > 1048576) throw new ArgumentException("内存范围为 512 至 1048576 MB。");
            if (JavaPath.Length > 0 && (!File.Exists(Service.InstanceManager.Extended(JavaPath)) || !JavaPath.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("请选择有效的 java.exe。");
            if (!Path.IsPathFullyQualified(MinecraftRoot)) throw new ArgumentException("请选择完整的游戏目录路径。");
            string root = Service.InstanceManager.Normal(MinecraftRoot);
            if (_state.ConfigurationVersion == 0 || !_state.Instances.MinecraftRoot.Equals(root, StringComparison.OrdinalIgnoreCase) || _state.DownloadProvider.Source != Source)
                _state.Configure(root, Source);
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            { ["java"] = JavaPath, ["root"] = MinecraftRoot, ["memory"] = DefaultMemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["source"] = Source.ToString(), ["rootMode"] = root.Equals(Path.GetFullPath(DefaultMinecraftRoot), StringComparison.OrdinalIgnoreCase) ? "portable" : "custom" });
            IsSaving = true; _pendingSaves++; Message = "正在保存…";
            PendingSave = PersistAsync(PendingSave, data, version);
            return PendingSave;
        }
        catch (Exception ex) { Message = Service.DiagnosticText.Redact(ex.Message); _state.LogException("设置保存失败", ex); }
        return PendingSave;
    }

    private async Task PersistAsync(Task previous, byte[] data, long version)
    {
        try
        {
            await previous;
            await Service.InstanceManager.AtomicBytesAsync(_filePath, data, default);
            if (version == _saveVersion) Message = "已自动保存";
        }
        catch (Exception ex)
        {
            if (version == _saveVersion) Message = "保存失败：" + Service.DiagnosticText.Summary(ex);
            _state.LogException("设置保存失败", ex);
        }
        finally { _pendingSaves--; IsSaving = _pendingSaves > 0; }
    }
    [RelayCommand] private void BrowseJava()
    {
        var dialog = new OpenFileDialog { Filter = "Java 程序|java.exe", CheckFileExists = true };
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
        catch (Exception ex) { _state.ReportError("诊断日志目录打开失败", ex); }
    }
    [RelayCommand] private async Task ExportLogsAsync()
    {
        if (IsManagingLogs) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断日志", Filter = "ZIP 压缩包|*.zip", DefaultExt = ".zip",
            FileName = $"SCI-Logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog() == true) await ExportLogsToAsync(dialog.FileName);
    }
    public async Task ExportLogsToAsync(string destination)
    {
        if (IsManagingLogs) return;
        IsManagingLogs = true;
        try { int count = await _state.ExportLogsAsync(destination); LogMessage = $"已导出 {count} 份日志。"; }
        catch (Exception ex) { LogMessage = "导出失败：" + Service.DiagnosticText.Summary(ex); _state.LogException("日志导出失败", ex); }
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
        try { int count = await _state.RecycleLogsAsync(); LogMessage = $"已将 {count} 份日志移至回收站，当前日志已重新开始。"; }
        catch (Exception ex) { LogMessage = "日志清理未完成：" + Service.DiagnosticText.Summary(ex); _state.LogException("日志清理失败", ex); }
        finally { IsManagingLogs = false; }
    }
    [RelayCommand] private void OpenLicenseFiles()
    {
        try
        {
            string directory = AppContext.BaseDirectory;
            if (!File.Exists(Path.Combine(directory, "LICENSE")))
                throw new FileNotFoundException("许可证文件未随程序附带。", Path.Combine(directory, "LICENSE"));
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { directory } });
        }
        catch (Exception ex) { _state.ReportError("许可证文件夹打开失败", ex); }
    }
    [RelayCommand] private void BrowseRoot()
    {
        var dialog = new OpenFolderDialog { Title = "选择 Minecraft 目录" };
        if (dialog.ShowDialog() == true) MinecraftRoot = dialog.FolderName;
    }
}
