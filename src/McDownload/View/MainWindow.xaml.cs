using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using McDownload.ViewModel;
namespace McDownload.View;
public partial class MainWindow : Window
{
    private MainWindowViewModel? _model;
    private InstanceDeleteDialog? _deleteDialog;
    private LogDeleteDialog? _logDeleteDialog;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null)
            { _model.Instances.PropertyChanged -= InstancesChanged; _model.Settings.PropertyChanged -= SettingsChanged; }
            _deleteDialog?.Close();
            _logDeleteDialog?.Close();
            _model = DataContext as MainWindowViewModel;
            if (_model is not null)
            { _model.Instances.PropertyChanged += InstancesChanged; _model.Settings.PropertyChanged += SettingsChanged; }
        };
        Closed += (_, _) =>
        {
            if (_model is not null)
             { _model.Instances.PropertyChanged -= InstancesChanged; _model.Settings.PropertyChanged -= SettingsChanged; }
            _model?.Download.Modpacks.Reset();
             _model = null;
            ModpackDragOverlay.Visibility = Visibility.Collapsed;
        };
    }

    internal Task DropImport { get; private set; } = Task.CompletedTask;
    private static string[] DroppedFiles(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) &&
        data.GetData(DataFormats.FileDrop) is string[] paths ? paths : Array.Empty<string>();
    private void WindowDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        bool accept = _model?.CanDropModpack == true && ModpackImportViewModel.AcceptsFiles(DroppedFiles(e.Data)) &&
            (e.AllowedEffects & DragDropEffects.Copy) != 0;
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        ModpackDragOverlay.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }
    private void WindowDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(this);
        if (point.X <= 0 || point.Y <= 0 || point.X >= ActualWidth || point.Y >= ActualHeight)
            ModpackDragOverlay.Visibility = Visibility.Collapsed;
    }
    private async void WindowDrop(object sender, DragEventArgs e)
    {
        ModpackDragOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (_model?.CanDropModpack != true || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
        var paths = DroppedFiles(e.Data);
        if (paths.Length == 0) return;
        CommitSettingsInput();
        e.Effects = ModpackImportViewModel.AcceptsFiles(paths) ? DragDropEffects.Copy : DragDropEffects.None;
        try { await (DropImport = _model.ImportDroppedFilesAsync(paths)); }
        catch (Exception ex) { _model?.State.ReportError("整合包拖入失败", ex); }
    }

    private void InstancesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InstanceListViewModel.IsDeleteConfirmationVisible)) return;
        if (_model?.Instances.IsDeleteConfirmationVisible != true)
        {
            if (_deleteDialog is { IsVisible: true } dialog) dialog.Close();
            return;
        }
        if (_deleteDialog is not null) return;
        var model = _model;
        Dispatcher.InvokeAsync(() =>
        {
            if (_model != model || !model.Instances.IsDeleteConfirmationVisible || _deleteDialog is not null) return;
            _deleteDialog = new InstanceDeleteDialog { Owner = this, DataContext = model.Instances };
            try { _deleteDialog.ShowDialog(); }
            finally { _deleteDialog = null; }
        });
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.IsLogDeleteConfirmationVisible)) return;
        if (_model?.Settings.IsLogDeleteConfirmationVisible != true)
        {
            if (_logDeleteDialog is { IsVisible: true } dialog) dialog.Close();
            return;
        }
        var model = _model;
        Dispatcher.InvokeAsync(() =>
        {
            if (_model != model || !model.Settings.IsLogDeleteConfirmationVisible || _logDeleteDialog is not null) return;
            _logDeleteDialog = new LogDeleteDialog { Owner = this, DataContext = model.Settings };
            try { _logDeleteDialog.ShowDialog(); }
            finally { _logDeleteDialog = null; }
        });
    }

    internal void CommitSettingsInput()
    {
        if (_model?.CurrentPage == "Settings" && Keyboard.FocusedElement is TextBox { IsReadOnly: false } input &&
            ReferenceEquals(input.DataContext, _model.Settings))
            input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
