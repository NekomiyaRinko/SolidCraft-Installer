using System.Windows.Controls;
using System.Windows;
using McDownload.ViewModel;
namespace McDownload.View;
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
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
        string key = CurseForgeKeyInput.Password;
        CurseForgeKeyInput.Clear();
        await settings.ApplyCurseForgeKeyAsync(key);
    }
    private void ClearCurseForgeKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || !settings.CanEditCurseForgeKey) return;
        CurseForgeKeyInput.Clear();
        settings.ClearCurseForgeKeyCommand.Execute(null);
    }
}
