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
    public static string DefaultMinecraftRoot => Path.Combine(AppContext.BaseDirectory, ".minecraft");
    [ObservableProperty] private string javaPath = "";
    [ObservableProperty] private int defaultMemoryMb = 4096;
    [ObservableProperty] private string minecraftRoot = DefaultMinecraftRoot;
    [ObservableProperty] private string selectedDownloadSource = "BMCLAPI（失败回退官方）";
    public IReadOnlyList<string> DownloadSources { get; } = new[] { "BMCLAPI（失败回退官方）", "官方" };
    [ObservableProperty] private string message = "";
    public string AssetsPath => Path.Combine(MinecraftRoot, "assets");
    public string LibrariesPath => Path.Combine(MinecraftRoot, "libraries");
    public string DiagnosticDirectory => _state.DiagnosticDirectory;
    public string? DiagnosticLogPath => _state.DiagnosticLogPath;
    public WorkspaceViewModel State => _state;
    public SettingsViewModel(WorkspaceViewModel state, string? filePath = null)
    {
        _state = state;
        _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCI", "settings.json");
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
    partial void OnMinecraftRootChanged(string value) { OnPropertyChanged(nameof(AssetsPath)); OnPropertyChanged(nameof(LibrariesPath)); }
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
    }
    [RelayCommand] private async Task SaveAsync()
    {
        if (_state.IsBusy) { Message = "请等待当前任务结束。"; return; }
        try
        {
            if (DefaultMemoryMb < 512 || DefaultMemoryMb > 1048576) throw new ArgumentException("内存范围为 512 至 1048576 MB。");
            if (JavaPath.Length > 0 && (!File.Exists(Service.InstanceManager.Extended(JavaPath)) || !JavaPath.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("请选择有效的 java.exe。");
            if (!Path.IsPathFullyQualified(MinecraftRoot)) throw new ArgumentException("请选择完整的游戏目录路径。");
            _ = new Service.InstanceManager(MinecraftRoot);
            await Service.InstanceManager.AtomicBytesAsync(_filePath, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            { ["java"] = JavaPath, ["root"] = MinecraftRoot, ["memory"] = DefaultMemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["source"] = Source.ToString(), ["rootMode"] = Path.GetFullPath(MinecraftRoot).Equals(Path.GetFullPath(DefaultMinecraftRoot), StringComparison.OrdinalIgnoreCase) ? "portable" : "custom" }), default);
            _state.Configure(MinecraftRoot, Source);
            Message = "设置已保存。目录设置不会迁移现有实例。";
        }
        catch (Exception ex) { Message = Service.DiagnosticText.Redact(ex.Message); _state.LogException("设置保存失败", ex); }
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
