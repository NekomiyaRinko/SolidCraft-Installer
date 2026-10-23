using System.Windows.Markup;

namespace McDownload.i18n;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string group, string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Text.Get(group, key);
}
