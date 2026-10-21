using McDownload.Model;

namespace McDownload.Service;

/// <remarks>同一实例最多安装一种加载器；加载器操作不删除 mods 或 saves。</remarks>
public interface ILoaderInstaller
{
    Task<LoaderState> GetStateAsync(InstanceId instanceId, CancellationToken cancellationToken);
    Task<CompatibilityReport> ValidateCompatibilityAsync(LoaderCompatibilityRequest request,
        CancellationToken cancellationToken);
    Task<LoaderOperationResult> InstallAsync(LoaderInstallRequest request, OperationContext context);
    Task<LoaderOperationResult> RemoveAsync(LoaderRemovalRequest request, OperationContext context);
    Task<LoaderReplacementPlan> PrepareReplacementAsync(LoaderReplacementRequest request,
        CancellationToken cancellationToken);
    Task<LoaderOperationResult> ReplaceAsync(LoaderReplacementPlan plan,
        LoaderReplacementApproval approval, OperationContext context);
}
