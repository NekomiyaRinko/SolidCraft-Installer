using System.Text.Json.Nodes;

namespace McDownload.Model;

public enum MinecraftVersionChannel
{
    Release,
    Snapshot,
    OldBeta,
    OldAlpha
}

public enum VersionPatchKind
{
    Loader,
    Component
}

public sealed record MinecraftVersionEntry(
    string Id,
    MinecraftVersionChannel Channel,
    Uri JsonUri,
    string Sha1);

public sealed record MinecraftVersionManifest(
    string LatestReleaseId,
    string LatestSnapshotId,
    IReadOnlyList<MinecraftVersionEntry> Versions);

public sealed record VersionDocument(string Id, JsonObject Json);

public sealed record VersionPatch(
    Guid PatchId,
    VersionPatchKind Kind,
    LoaderSelection? Loader,
    int Order,
    JsonObject Json);

public sealed record InheritanceResolutionOptions(
    InstanceId InstanceId,
    int MaxDepth,
    bool AllowOfficialDownload);

public sealed record ResolvedVersion(
    VersionDocument EffectiveDocument,
    IReadOnlyList<VersionDocument> InheritanceChain);
