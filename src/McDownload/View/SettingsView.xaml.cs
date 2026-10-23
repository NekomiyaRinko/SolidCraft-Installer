using System.Windows.Controls;
using System.Windows;
using McDownload.ViewModel;
namespace McDownload.View;
public partial class SettingsView : UserControl
{
    private bool _showingConfiguredKey;
    public SettingsView()
    {
        InitializeComponent();
        CurseForgeKeyInput.PasswordChar = '*';
        CurseForgeKeyInput.GotKeyboardFocus += (_, _) =>
        {
            if (_showingConfiguredKey) CurseForgeKeyInput.Clear();
        };
        CurseForgeKeyInput.PasswordChanged += (_, _) => _showingConfiguredKey = false;
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel previous) previous.PropertyChanged -= SettingsChanged;
            if (e.NewValue is SettingsViewModel current) current.PropertyChanged += SettingsChanged;
            RefreshConfiguredKey();
        };
    }
    private void SettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.HasConfiguredCurseForgeKey)) RefreshConfiguredKey();
    }
    private void RefreshConfiguredKey()
    {
        if (DataContext is SettingsViewModel { HasConfiguredCurseForgeKey: true }) ShowConfiguredKey();
        else if (_showingConfiguredKey) CurseForgeKeyInput.Clear();
    }
    private void ShowConfiguredKey()
    {
        CurseForgeKeyInput.Password = new string('*', 64);
        _showingConfiguredKey = true;
    }
    private void OpenCurseForgeConsole(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || settings.IsExternalLinkConfirmationVisible) return;
        settings.IsExternalLinkConfirmationVisible = true;
        try
        {
            var dialog = new ExternalLinkDialog { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true) settings.ConfirmOpenCurseForgeConsole();
        }
        finally { settings.IsExternalLinkConfirmationVisible = false; }
    }
    private async void ApplyCurseForgeKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || !settings.CanEditCurseForgeKey) return;
        string? key = _showingConfiguredKey ? null : CurseForgeKeyInput.Password;
        CurseForgeKeyInput.Clear();
        if (await settings.ApplyCurseForgeKeyAsync(key))
            ShowConfiguredKey();
    }
    private void ClearCurseForgeKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || !settings.CanEditCurseForgeKey) return;
        CurseForgeKeyInput.Clear();
        settings.ClearCurseForgeKeyCommand.Execute(null);
    }
}
