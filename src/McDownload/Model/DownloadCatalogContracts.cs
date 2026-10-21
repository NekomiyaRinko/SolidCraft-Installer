namespace McDownload.Model;

public enum DownloadSource
{
    Bmclapi,
    Official
}

public sealed record LoaderVersionEntry(string Version, bool IsStable)
{
    public string DisplayName => $"{Version} · {(IsStable ? "正式版" : "测试版")}";
}

public sealed record InstalledVersionInfo(
    string Name,
    string DirectoryPath,
    string MinecraftVersion,
    string LoaderDescription,
    InstanceMetadata? Metadata,
    string? Warning);
