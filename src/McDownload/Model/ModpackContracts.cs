using McDownload.i18n;
namespace McDownload.Model;

public enum ModpackFormat { Modrinth, CurseForge }
public enum ModpackImportStatus { Importing, NeedsFiles, Failed, Canceled, Completed }

public sealed record ModpackHashes(string? Sha1 = null, string? Sha512 = null, string? Md5 = null, string? Sha256 = null);
public sealed record CurseForgeReference(long ProjectId, long FileId);

public sealed record ModpackFile(
    string Key, string? Path, long? Size, ModpackHashes Hashes,
    IReadOnlyList<Uri> Downloads, bool Optional = false,
    CurseForgeReference? CurseForge = null, string? ArchiveEntry = null);

public sealed record ModpackDefinition(
    ModpackFormat Format, string Name, string Version, string? Description,
    string MinecraftVersion, LoaderSelection? Loader, IReadOnlyList<ModpackFile> Files,
    IReadOnlyList<string> Warnings, string ArchiveSha256, string SourceFileName);

public sealed record ModpackImportRequest(
    string ArchivePath, string DirectoryName, string? JavaExecutable = null,
    IReadOnlyList<string>? SelectedOptionalFiles = null, string? ExpectedArchiveSha256 = null);

public sealed record ModpackResumeRequest(
    InstanceId InstanceId, string? ArchivePath = null,
    IReadOnlyDictionary<string, string>? LocalFiles = null);

public sealed record MissingModpackFile(string Key, string Reason, Uri? Website = null);
public sealed record ModpackPendingWrite(string Path, string Sha256);

public sealed record ModpackImportState(
    int FormatVersion, InstanceId InstanceId, string DirectoryName,
    string ArchivePath, string ArchiveSha256, string Name, string Version,
    IReadOnlyList<string> SelectedOptionalFiles, ModpackImportStatus Status,
    bool GameInstalled, bool LoaderInstalled,
    IReadOnlyDictionary<string, ModpackFile> ResolvedFiles,
    IReadOnlyDictionary<string, string> WrittenFiles,
    IReadOnlyList<MissingModpackFile> MissingFiles, string? Error = null,
    ModpackPendingWrite? PendingWrite = null);

public sealed record ModpackImportResult(InstanceMetadata Instance, ModpackImportState State);

public sealed record CurseForgeFileResolution(ModpackFile? File, MissingModpackFile? Missing);

public sealed class ModpackFilesRequiredException(ModpackImportResult result)
    : IOException(Text.Format("ModpackContracts", "FilesRequired", result.State.MissingFiles.Count))
{
    public ModpackImportResult Result { get; } = result;
}
