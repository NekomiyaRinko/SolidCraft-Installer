using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace McDownload.View;

public sealed class LoaderVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string type && type != "不安装" ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
