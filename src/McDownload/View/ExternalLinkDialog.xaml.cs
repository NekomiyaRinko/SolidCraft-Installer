using System.Windows;
using System.Windows.Input;

namespace McDownload.View;

public partial class ExternalLinkDialog : Window
{
    public ExternalLinkDialog() => InitializeComponent();
    protected override void OnContentRendered(EventArgs e) { CancelButton.Focus(); base.OnContentRendered(e); }
    private void Confirm(object sender, RoutedEventArgs e) => DialogResult = true;
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        base.OnPreviewKeyDown(e);
    }
}
