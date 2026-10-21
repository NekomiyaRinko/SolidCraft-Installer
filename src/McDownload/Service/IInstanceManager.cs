using McDownload.Model;

namespace McDownload.Service;

public interface IInstanceManager
{
    Task<IReadOnlyList<InstanceMetadata>> ListAsync(CancellationToken cancellationToken);
    Task<InstanceMetadata> GetAsync(InstanceId instanceId, CancellationToken cancellationToken);
    Task<InstanceMetadata> CreateAsync(InstanceCreateRequest request, CancellationToken cancellationToken);
    Task<InstanceMetadata> UpdateAsync(InstanceId instanceId, InstanceDisplayUpdate update,
        InstanceRevision expectedRevision, CancellationToken cancellationToken);
    Task DeleteAsync(InstanceDeleteRequest request, CancellationToken cancellationToken);
    Task<InstancePaths> ResolvePathsAsync(InstanceId instanceId, CancellationToken cancellationToken);
    Task<InstanceMetadata> ReadMetadataAsync(InstanceId instanceId, CancellationToken cancellationToken);
    Task<InstanceMetadata> WriteMetadataAsync(InstanceId instanceId, InstanceEditableMetadata editableMetadata,
        InstanceRevision expectedRevision, CancellationToken cancellationToken);
}
