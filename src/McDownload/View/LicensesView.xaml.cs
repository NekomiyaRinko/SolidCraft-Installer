using System.Windows;
using System.Windows.Controls;
using McDownload.Model;
using McDownload.ViewModel;

namespace McDownload.View;

public partial class LicensesView : UserControl
{
    public LicensesView() => InitializeComponent();

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not LicenseViewModel model || e.NewValue is not LicenseDocument document) return;
        model.SelectedDocument = document;
        LicenseContent.ScrollToHome();
    }

    private void OnTreeLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LicenseViewModel model) return;
        LicenseTree.UpdateLayout();
        foreach (var group in model.Groups)
        {
            if (LicenseTree.ItemContainerGenerator.ContainerFromItem(group) is not TreeViewItem parent) continue;
            parent.UpdateLayout();
            if (parent.ItemContainerGenerator.ContainerFromItem(model.SelectedDocument) is TreeViewItem item)
            { item.IsSelected = true; break; }
        }
    }
}
