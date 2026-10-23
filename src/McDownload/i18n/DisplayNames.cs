using McDownload.Model;

namespace McDownload.i18n;

public static class DisplayNames
{
    public static string Loader(LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => Text.Get("Common", "Forge"),
        LoaderKind.Fabric => Text.Get("Common", "Fabric"),
        LoaderKind.NeoForge => Text.Get("Common", "NeoForge"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Pack(ModpackFormat format) => format switch
    {
        ModpackFormat.Modrinth => Text.Get("Common", "Modrinth"),
        ModpackFormat.CurseForge => Text.Get("Common", "CurseForge"),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}
