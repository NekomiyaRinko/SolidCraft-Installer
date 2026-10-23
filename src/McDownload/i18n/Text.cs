using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;

namespace McDownload.i18n;

public static class Text
{
    private static readonly ConcurrentDictionary<string, ResourceManager> Managers = new(StringComparer.Ordinal);

    public static string Get(string group, string key)
    {
        var manager = Managers.GetOrAdd(group, name => new ResourceManager("McDownload.i18n." + name, typeof(Text).Assembly));
        return manager.GetString(key, CultureInfo.CurrentUICulture)
            ?? throw new MissingManifestResourceException($"Missing localization resource: {group}.{key}");
    }

    public static string Format(string group, string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(group, key), args);
}
