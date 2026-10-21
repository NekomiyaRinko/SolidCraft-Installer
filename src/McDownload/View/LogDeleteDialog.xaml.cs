using System;
using System.Windows;
using System.Windows.Input;
using McDownload.ViewModel;

namespace McDownload.View;

public partial class LogDeleteDialog : Window
{
    public LogDeleteDialog() => InitializeComponent();
    protected override void OnContentRendered(EventArgs e) { CancelButton.Focus(); base.OnContentRendered(e); }
    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is SettingsViewModel { IsLogDeleteConfirmationVisible: true } model)
            model.CancelDeleteLogsCommand.Execute(null);
        base.OnClosed(e);
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        base.OnPreviewKeyDown(e);
    }
}
