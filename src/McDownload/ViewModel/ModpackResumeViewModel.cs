using McDownload.i18n;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McDownload.Model;
using McDownload.Service;
using Microsoft.Win32;

namespace McDownload.ViewModel;

public partial class ModpackMissingFileViewModel : ObservableObject
{
    public MissingModpackFile Missing { get; }
    public string Name { get; }
    public string Reason => Missing.Reason;
    public bool CanChoose { get; }
    public bool CanOpenWebsite => Missing.Website is { IsAbsoluteUri: true } uri &&
        uri.AbsolutePath.LastIndexOf("/files/", StringComparison.Ordinal) is var index && index > 0 &&
        long.TryParse(uri.AbsolutePath[(index + 7)..], out long id) &&
        CurseForgeFileResolver.TryGetFileWebsite(uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath[..index] + uri.Query + uri.Fragment, id, out _);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectionStatus))] private string localPath = "";
    [ObservableProperty] private string error = "";
    public string SelectionStatus => LocalPath.Length == 0 ? "" : Text.Get("ModpackResume", "SelectedPendingValidation");
    public ModpackMissingFileViewModel(MissingModpackFile missing, ModpackFile? file)
    { Missing = missing; Name = file?.Path ?? missing.Key; CanChoose = file?.Path is not null; }
    [RelayCommand] private void Browse()
    {
        if (!CanChoose) return;
        var dialog = new OpenFileDialog { Title = Text.Get("ModpackResume", "SelectMissingFile"), CheckFileExists = true };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true) LocalPath = dialog.FileName;
    }
    [RelayCommand] private void OpenWebsite()
    {
        if (!CanOpenWebsite) return;
        try { Process.Start(new ProcessStartInfo(Missing.Website!.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Error = Text.Format("ModpackResume", "DownloadPageOpenFailed", DiagnosticText.Summary(ex)); }
    }
}

public partial class ModpackResumeViewModel : ObservableObject
{
    private readonly WorkspaceViewModel _state;
    private long _generation;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanResume))] private ModpackImportState? importState;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanResume))] private bool isLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanResume))] private bool isQueued;
    [ObservableProperty] private string title = Text.Get("ModpackResume", "ResumeImport");
    [ObservableProperty] private string archivePath = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string error = "";
    public bool HasError => Error.Length > 0;
    public bool CanResume => ImportState is not null && !IsLoading && !IsQueued;
    public ObservableCollection<ModpackMissingFileViewModel> MissingFiles { get; } = new();
    public Task<bool> LastSubmission { get; private set; } = Task.FromResult(false);
    public ModpackResumeViewModel(WorkspaceViewModel state)
    {
        _state = state;
        state.ConfigurationChanged += (_, _) => { _generation++; ImportState = null; MissingFiles.Clear(); IsQueued = false; IsLoading = false; Error = ""; };
    }
    public async Task LoadAsync(InstanceMetadata metadata)
    {
        long generation = ++_generation;
        ImportState = null; MissingFiles.Clear(); Error = ""; Title = metadata.Editable.DisplayName; IsLoading = true; IsQueued = false;
        var importer = _state.Modpacks;
        try
        {
            var state = await importer.GetStateAsync(metadata.Id, default) ?? throw new InvalidDataException(Text.Get("ModpackResume", "ImportRecordMissing"));
            if (generation != _generation) return;
            ApplyState(state);
        }
        catch (Exception ex) { if (generation == _generation) Error = DiagnosticText.Summary(ex); }
        finally { if (generation == _generation) IsLoading = false; }
    }
    private void ApplyState(ModpackImportState state)
    {
        ImportState = state; ArchivePath = state.ArchivePath;
        Status = Text.Format("ModpackResume", "ImportSummary", state.Name, state.Version, InstanceItem.DescribeImportStatus(state.Status));
        Error = state.Error ?? "";
        MissingFiles.Clear();
        foreach (var file in state.MissingFiles)
            MissingFiles.Add(new(file, state.ResolvedFiles.GetValueOrDefault(file.Key)));
    }
    [RelayCommand] private void BrowseArchive()
    {
        var dialog = new OpenFileDialog { Title = Text.Get("ModpackResume", "SelectSameArchive"), Filter = Text.Get("Common", "ModpackFileFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true) ArchivePath = dialog.FileName;
    }
    [RelayCommand] private async Task ResumeAsync()
    {
        if (!CanResume || ImportState is not { } state) return;
        long generation = _generation;
        var importer = _state.Modpacks;
        string sourcePath = ArchivePath;
        var localFiles = MissingFiles.Where(x => x.LocalPath.Length > 0).ToDictionary(x => x.Missing.Key, x => x.LocalPath);
        try
        {
            Error = ""; IsQueued = true;
            long previousOrder = _state.Tasks.Select(x => x.Order).DefaultIfEmpty().Max();
            LastSubmission = _state.ResumeModpackAsync(new(state.InstanceId, sourcePath, localFiles));
            var submitted = _state.Tasks.FirstOrDefault(x => x.Order > previousOrder);
            bool completed = await LastSubmission;
            if (generation != _generation) return;
            string? failure = completed ? null : submitted?.Error ?? (submitted?.State == QueuedTaskState.Canceled
                ? Text.Get("ModpackResume", "CanceledFilesRetained") : Text.Get("ModpackResume", "ResumeFailed"));
            var updated = await importer.GetStateAsync(state.InstanceId, default);
            if (generation != _generation) return;
            if (updated is not null) ApplyState(updated);
            if (!completed)
            {
                ArchivePath = sourcePath;
                foreach (var file in MissingFiles) file.LocalPath = localFiles.GetValueOrDefault(file.Missing.Key, "");
                Error = failure!;
            }
        }
        catch (Exception ex) { if (generation == _generation) Error = DiagnosticText.Summary(ex); }
        finally { if (generation == _generation) IsQueued = false; }
    }
}
