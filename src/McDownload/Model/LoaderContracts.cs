namespace McDownload.Model;

public enum LoaderKind
{
    Forge,
    Fabric,
    NeoForge
}

public enum CompatibilityStatus
{
    Compatible,
    Incompatible,
    Unknown
}

public sealed record LoaderSelection(LoaderKind Kind, string Version);

public sealed record LoaderRelease(
    string MinecraftVersionId,
    LoaderSelection Loader,
    bool IsStable);

public sealed record LoaderState(
    InstanceId InstanceId,
    InstanceRevision Revision,
    LoaderSelection? InstalledLoader,
    Guid? LoaderPatchId);

public sealed record CompatibilityIssue(
    string Code,
    string Message,
    bool IsBlocking);

public sealed record CompatibilityReport(
    CompatibilityStatus Status,
    IReadOnlyList<CompatibilityIssue> Issues);

public sealed record LoaderCompatibilityRequest(
    string MinecraftVersionId,
    LoaderSelection Target,
    string? InstallerJavaExecutable);

public sealed record LoaderInstallRequest(
    InstanceId InstanceId,
    InstanceRevision ExpectedRevision,
    LoaderSelection Target,
    string? InstallerJavaExecutable);

public sealed record LoaderRemovalRequest(
    InstanceId InstanceId,
    InstanceRevision ExpectedRevision,
    Guid ExpectedLoaderPatchId);

public sealed record LoaderReplacementRequest(
    InstanceId InstanceId,
    InstanceRevision ExpectedRevision,
    LoaderSelection Target,
    string? InstallerJavaExecutable);

public sealed record LoaderReplacementPlan(
    Guid PlanId,
    LoaderReplacementRequest Request,
    LoaderState CurrentState,
    CompatibilityReport Compatibility,
    string ModCompatibilityWarning);

public sealed record LoaderReplacementApproval(
    Guid PlanId,
    bool ModCompatibilityWarningAccepted);

public sealed record LoaderOperationResult(
    Guid OperationId,
    LoaderState FinalState);
