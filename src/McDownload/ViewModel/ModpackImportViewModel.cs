using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McDownload.Model;
using McDownload.Service;
using Microsoft.Win32;

namespace McDownload.ViewModel;

public partial class ModpackOptionalFile : ObservableObject
{
    public ModpackFile File { get; }
    public string Name => File.Path ?? File.Key;
    [ObservableProperty] private bool isSelected;
    public ModpackOptionalFile(ModpackFile file) => File = file;
}

public partial class ModpackImportViewModel : ObservableObject
{
    private readonly WorkspaceViewModel _state;
    private readonly SettingsViewModel _settings;
    private CancellationTokenSource? _previewCancellation;
    private long _generation;
    private string? _credentialError;
    private readonly HashSet<string> _queuedNames = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<ModpackOptionalFile> OptionalFiles { get; } = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsHome), nameof(HasPack), nameof(CanSubmit), nameof(PackName), nameof(PackVersion), nameof(PackFormat), nameof(MinecraftVersion), nameof(Loader), nameof(FileSummary), nameof(IsCurseForge))]
    private ModpackDefinition? definition;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsHome), nameof(CanSubmit))] private bool isReading;
    [ObservableProperty] private string archivePath = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string error = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] private string message = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NameError), nameof(HasNameError), nameof(InstancePath), nameof(CanSubmit))] private string instanceName = "";
    [ObservableProperty] private string javaPath = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSubmit))] private bool isChecking;
    public bool IsHome => Definition is null && !IsReading;
    public bool HasPack => Definition is not null;
    public bool HasError => Error.Length > 0;
    public bool HasMessage => Message.Length > 0;
    public string PackName => Definition?.Name ?? "";
    public string PackVersion => "整合包版本 " + Definition?.Version;
    public string PackFormat => Definition?.Format.ToString() ?? "";
    public string MinecraftVersion => Definition?.MinecraftVersion ?? "";
    public string Loader => Definition?.Loader is { } loader ? $"{loader.Kind} {loader.Version}" : "不安装加载器";
    public bool IsCurseForge => Definition?.Format == ModpackFormat.CurseForge;
    public string CurseForgeAccessMessage => _state.HasCurseForgeApiKey
        ? "已配置 CurseForge 应用 API key，安装前将验证文件访问权限；限制自动下载的文件仍需手动补齐。"
        : "尚未配置 CurseForge 应用 API key，请先前往设置配置；限制自动下载的文件仍需手动补齐。";
    public string FileSummary => Definition is { } pack
        ? $"{pack.Files.Count(x => x.ArchiveEntry is null)} 个清单文件 · {pack.Files.Count(x => x.ArchiveEntry is not null)} 个覆盖文件" : "";
    public string InstancePath => _state.ConfigurationVersion == 0 ? "" : Path.Combine(_state.Instances.MinecraftRoot, "versions", InstanceName);
    public bool HasNameError => HasPack && NameError.Length > 0;
    public string NameError
    {
        get
        {
            try
            {
                InstanceManager.ValidateName(InstanceName);
                InstanceManager.ValidateName(InstanceName + ".json");
                if (_queuedNames.Contains(InstanceName) || Directory.Exists(InstancePath) || File.Exists(InstancePath))
                    return "实例名称已存在或已排队，请使用其他名称。";
                return "";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InstanceValidationException)
            { return DiagnosticText.Redact(ex.Message); }
        }
    }
    public bool CanSubmit => HasPack && !IsReading && !IsChecking && NameError.Length == 0;
    public Task<bool> LastSubmission { get; private set; } = Task.FromResult(false);
    public ModpackImportViewModel(WorkspaceViewModel state, SettingsViewModel settings)
    {
        _state = state; _settings = settings;
        state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(WorkspaceViewModel.HasCurseForgeApiKey)) return;
            OnPropertyChanged(nameof(CurseForgeAccessMessage));
            if (state.HasCurseForgeApiKey && _credentialError is not null)
            {
                if (Error == _credentialError) Error = "";
                _credentialError = null;
            }
        };
        state.ConfigurationChanged += (_, _) => { _queuedNames.Clear(); Reset(); OnPropertyChanged(nameof(InstancePath)); };
    }
    public static bool AcceptsFiles(IReadOnlyList<string> paths) => paths.Count == 1 &&
        (Path.GetExtension(paths[0]).Equals(".mrpack", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(paths[0]).Equals(".zip", StringComparison.OrdinalIgnoreCase));

    public async Task LoadFilesAsync(IReadOnlyList<string> paths)
    {
        Reset();
        if (!AcceptsFiles(paths)) { Error = "请一次选择一个 .mrpack 或 CurseForge .zip 分发包。"; return; }
        long generation = _generation;
        using var source = new CancellationTokenSource();
        _previewCancellation = source;
        IsReading = true; ArchivePath = paths[0];
        var importer = _state.Modpacks;
        try
        {
            var pack = await Task.Run(() => importer.PreviewAsync(paths[0], source.Token), source.Token);
            if (generation != _generation) return;
            Definition = pack;
            foreach (var file in pack.Files.Where(x => x.Optional)) OptionalFiles.Add(new(file));
            string name = new(pack.Name.Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x).ToArray());
            name = name.Trim().TrimEnd('.');
            if (name.Length > 120) name = name[..120].TrimEnd('.', ' ');
            try { InstanceManager.ValidateName(name); } catch (InstanceValidationException) { name = "Modpack"; }
            InstanceName = name;
            for (int suffix = 2; NameError.Length > 0; suffix++) InstanceName = name + "-" + suffix;
            JavaPath = _settings.JavaPath;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            Error = "整合包读取失败：" + DiagnosticText.Summary(ex);
            _state.LogException("整合包读取失败", ex);
        }
        finally
        {
            if (generation == _generation) { IsReading = false; _previewCancellation = null; }
        }
    }
    [RelayCommand] private async Task BrowseAsync()
    {
        var dialog = new OpenFileDialog { Title = "选择整合包", Filter = "整合包分发文件|*.mrpack;*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true) await LoadFilesAsync(new[] { dialog.FileName });
    }
    [RelayCommand] private void BrowseJava()
    {
        var dialog = new OpenFileDialog { Title = "选择安装器 Java", Filter = "Java 程序|java.exe", CheckFileExists = true };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true) JavaPath = dialog.FileName;
    }
    [RelayCommand] public void Reset()
    {
        _generation++; _previewCancellation?.Cancel(); _previewCancellation = null;
        Definition = null; IsReading = false; ArchivePath = ""; InstanceName = ""; JavaPath = "";
        Error = ""; Message = ""; _credentialError = null; OptionalFiles.Clear();
    }
    [RelayCommand] private async Task SubmitAsync()
    {
        if (!CanSubmit || Definition is not { } pack) return;
        string name = InstanceName;
        long generation = _generation;
        var importer = _state.Modpacks;
        bool checkingAccess = false;
        using var cancellation = new CancellationTokenSource();
        try
        {
            if (!string.IsNullOrWhiteSpace(JavaPath) && (!File.Exists(JavaPath) || !JavaPath.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("请选择有效的 java.exe。");
            var request = new ModpackImportRequest(ArchivePath, name, string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath,
                OptionalFiles.Where(x => x.IsSelected).Select(x => x.File.Key).ToArray(), pack.ArchiveSha256);
            Error = ""; IsChecking = true; _previewCancellation = cancellation;
            Message = IsCurseForge ? "正在检查 CurseForge 文件访问权限…" : "";
            checkingAccess = true;
            await importer.CheckAccessAsync(pack, request.SelectedOptionalFiles!, cancellation.Token);
            checkingAccess = false;
            if (generation != _generation) return;
            int count = _state.Tasks.Count;
            LastSubmission = _state.EnqueueModpackAsync(request);
            if (_state.Tasks.Count == count && LastSubmission.IsCompletedSuccessfully && !LastSubmission.Result)
                throw new InvalidOperationException("任务队列暂不可用，未添加任务；请等待凭据应用完成后重试。");
            _queuedNames.Add(name);
            Reset(); Message = $"「{name}」已加入下载队列。";
            _ = ReleaseNameAsync(name, LastSubmission, _state.ConfigurationVersion);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                Message = ""; Error = DiagnosticText.Summary(ex);
                _credentialError = checkingAccess && ex is InvalidOperationException && IsCurseForge && !_state.HasCurseForgeApiKey ? Error : null;
            }
        }
        finally { IsChecking = false; if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null; }
    }
    private async Task ReleaseNameAsync(string name, Task<bool> completion, long configuration)
    {
        await completion;
        if (_state.ConfigurationVersion == configuration)
        {
            _queuedNames.Remove(name); OnPropertyChanged(nameof(NameError)); OnPropertyChanged(nameof(HasNameError)); OnPropertyChanged(nameof(CanSubmit));
        }
    }
}
