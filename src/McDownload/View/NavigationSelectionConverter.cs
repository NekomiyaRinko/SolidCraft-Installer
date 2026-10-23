using System.Globalization;
using System.Windows.Data;

namespace McDownload.View;

public sealed class NavigationSelectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is string page && values[1] is string target &&
        (page == target || (page is "Loaders" or "ModpackResume") && target == "Instances" ||
            (page is "Licenses" or "ProjectLicense") && target == "About");
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
