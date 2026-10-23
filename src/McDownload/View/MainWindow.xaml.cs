using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using McDownload.i18n;
using McDownload.ViewModel;
namespace McDownload.View;
public partial class MainWindow : Window
{
    private MainWindowViewModel? _model;
    private InstanceDeleteDialog? _deleteDialog;
    private LogDeleteDialog? _logDeleteDialog;
    private readonly DispatcherTimer _dragWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    public MainWindow()
    {
        InitializeComponent();
        _dragWatch.Tick += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            bool overWindow = GetCursorPos(out var point) && handle != 0 &&
                GetAncestor(WindowFromPoint(point), 2) == handle;
            CheckModpackDrag(overWindow, (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0);
        };
        DataContextChanged += (_, _) =>
        {
            HideModpackDrag();
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
            HideModpackDrag();
        };
    }

    internal Task DropImport { get; private set; } = Task.CompletedTask;
    private void HideModpackDrag()
    {
        _dragWatch.Stop();
        ModpackDragOverlay.Visibility = Visibility.Collapsed;
    }
    internal void CheckModpackDrag(bool overWindow, bool dragging)
    {
        if (!overWindow || !dragging || _model?.CanDropModpack != true) HideModpackDrag();
    }
    private static string[] DroppedFiles(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) &&
        data.GetData(DataFormats.FileDrop) is string[] paths ? paths : Array.Empty<string>();
    private void WindowDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { HideModpackDrag(); return; }
        bool accept = _model?.CanDropModpack == true && ModpackImportViewModel.AcceptsFiles(DroppedFiles(e.Data)) &&
            (e.AllowedEffects & DragDropEffects.Copy) != 0;
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        if (accept)
        {
            ModpackDragOverlay.Visibility = Visibility.Visible;
            _dragWatch.Start();
        }
        else HideModpackDrag();
        e.Handled = true;
    }
    private void WindowDragLeave(object sender, DragEventArgs e)
    {
        HideModpackDrag();
    }
    private async void WindowDrop(object sender, DragEventArgs e)
    {
        HideModpackDrag();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (_model?.CanDropModpack != true || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
        var paths = DroppedFiles(e.Data);
        if (paths.Length == 0) return;
        CommitSettingsInput();
        e.Effects = ModpackImportViewModel.AcceptsFiles(paths) ? DragDropEffects.Copy : DragDropEffects.None;
        try { await (DropImport = _model.ImportDroppedFilesAsync(paths)); }
        catch (Exception ex) { _model?.State.ReportError(Text.Get("MainWindowView", "ModpackDropFailed"), ex); }
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
