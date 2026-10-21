namespace McDownload.Model;

public readonly record struct InstanceId(Guid Value);

public readonly record struct InstanceRevision(string Value);

public enum IsolationPolicy
{
    PerInstance
}

public enum InstanceDeleteMode
{
    MetadataOnly,
    RecycleInstanceDirectory
}

public sealed record InstanceEditableMetadata(
    string DisplayName,
    string? Description,
    string? JavaExecutable);

public sealed record InstanceDisplayUpdate(
    string DisplayName,
    string? Description);

public sealed record InstanceMetadata(
    int SchemaVersion,
    InstanceId Id,
    string DirectoryName,
    string MinecraftVersionId,
    IsolationPolicy IsolationPolicy,
    InstanceEditableMetadata Editable,
    LoaderSelection? InstalledLoader,
    Guid? LoaderPatchId,
    IReadOnlyList<Guid> ActivePatchIds,
    InstanceRevision Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InstanceCreateRequest(
    string DirectoryName,
    string MinecraftVersionId,
    IsolationPolicy IsolationPolicy,
    InstanceEditableMetadata Editable);

public sealed record InstanceDeleteRequest(
    InstanceId InstanceId,
    InstanceRevision ExpectedRevision,
    InstanceDeleteMode Mode,
    bool UserDataDeletionConfirmed);

public sealed record InstancePaths(
    string MinecraftRoot,
    string GameDirectory,
    string MetadataFile,
    string VersionJsonFile,
    string ClientJarFile,
    string BaseVersionJsonFile,
    string PatchDirectory,
    string WorkDirectory,
    string ModsDirectory,
    string SavesDirectory,
    string AssetsDirectory,
    string LibrariesDirectory);
