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
            _model = null;
        };
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
