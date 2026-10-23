using McDownload.Model;

namespace McDownload.Service;

public interface IModpackImporter
{
    Task<ModpackDefinition> PreviewAsync(string archivePath, CancellationToken token);
    Task<ModpackImportResult> ImportAsync(ModpackImportRequest request, OperationContext context);
    Task<ModpackImportResult> ResumeAsync(ModpackResumeRequest request, OperationContext context);
    Task<ModpackImportState?> GetStateAsync(InstanceId id, CancellationToken token);
}

public interface ICurseForgeFileResolver
{
    Task<CurseForgeFileResolution> ResolveAsync(ModpackFile file, CancellationToken token);
}
