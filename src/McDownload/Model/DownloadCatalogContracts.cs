using McDownload.i18n;
namespace McDownload.Model;

public enum DownloadSource
{
    Bmclapi,
    Official
}

public sealed record LoaderVersionEntry(string Version, bool IsStable)
{
    public string DisplayName => Text.Format("DownloadCatalogContracts", "VersionChannel", Version, (IsStable ? Text.Get("Common", "Release") : Text.Get("Common", "Snapshot")));
}

public sealed record InstalledVersionInfo(
    string Name,
    string DirectoryPath,
    string MinecraftVersion,
    string LoaderDescription,
    InstanceMetadata? Metadata,
    string? Warning);
