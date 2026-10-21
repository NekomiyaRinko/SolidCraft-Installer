using McDownload.Model;

namespace McDownload.Service;

public interface IVersionResolver
{
    Task<MinecraftVersionManifest> GetManifestAsync(CancellationToken cancellationToken);
    Task<VersionDocument> FetchVersionJsonAsync(MinecraftVersionEntry version,
        CancellationToken cancellationToken);
    Task<VersionDocument> ReadInstanceVersionJsonAsync(InstanceId instanceId,
        CancellationToken cancellationToken);
    Task<ResolvedVersion> ResolveInheritanceAsync(VersionDocument leaf,
        InheritanceResolutionOptions options, CancellationToken cancellationToken);
    VersionDocument Merge(VersionDocument parent, VersionDocument child);
    VersionDocument Compose(VersionDocument resolvedBase,
        IReadOnlyList<VersionPatch> activePatches, string outputVersionId);
}
