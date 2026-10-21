using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using McDownload.Model;

namespace McDownload.Service;

public sealed class LoaderInstaller : ILoaderInstaller
{
    private const string ModWarning = "旧加载器下的模组可能不兼容新加载器";
    private readonly InstanceManager _instances;
    private readonly VersionResolver _versions;
    private readonly HmclFetchTask _fetcher;
    private readonly Action<string>? _log;
    private readonly Func<string?, int, CancellationToken, Task<string>> _findJava;
    private readonly Func<string, IEnumerable<string>, string, CancellationToken, Task<string>> _runJava;
    private readonly ConcurrentDictionary<Guid, (LoaderReplacementPlan Plan, DateTimeOffset Expires)> _plans = new();

    private readonly record struct ForgePackage(string Installer, JsonObject Install, JsonObject Version,
        bool ProcessorFormat, string? ClientOption);
    private readonly record struct PreparedTarget(VersionDocument Vanilla, JsonObject Profile,
        ForgePackage? Forge, string? Java);
    private delegate void Reporter(OperationStage stage, double? fraction = null, string? item = null,
        long downloaded = 0, long? total = null);

    public LoaderInstaller(InstanceManager instances, VersionResolver versions, HmclFetchTask fetcher, Action<string>? log = null)
        : this(instances, versions, fetcher, FindJavaAsync, RunJavaAsync, log) { }

    internal LoaderInstaller(InstanceManager instances, VersionResolver versions, HmclFetchTask fetcher,
        Func<string?, int, CancellationToken, Task<string>> findJava,
        Func<string, IEnumerable<string>, string, CancellationToken, Task<string>> runJava,
        Action<string>? log = null)
    {
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _findJava = findJava ?? throw new ArgumentNullException(nameof(findJava));
        _runJava = runJava ?? throw new ArgumentNullException(nameof(runJava));
        _log = log;
    }

    public async Task<LoaderState> GetStateAsync(InstanceId instanceId, CancellationToken cancellationToken)
    {
        string directory = await _instances.FindDirectoryAsync(instanceId, cancellationToken);
        await using var lease = await _instances.LockInstanceAsync(instanceId, cancellationToken);
        var metadata = await _instances.ReadLockedAsync(directory, cancellationToken);
        if (metadata.InstalledLoader is not null) _ = await ReadManagedAsync(metadata, cancellationToken);
        return State(metadata);
    }

    public async Task<CompatibilityReport> ValidateCompatibilityAsync(LoaderCompatibilityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await InspectAsync(request.MinecraftVersionId, request.Target,
            request.InstallerJavaExecutable, cancellationToken)).Report;
    }

    public async Task<LoaderOperationResult> InstallAsync(LoaderInstallRequest request, OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateContext(context);
        string directory = await _instances.FindDirectoryAsync(request.InstanceId, context.CancellationToken);
        await using var lease = await _instances.LockInstanceAsync(request.InstanceId, context.CancellationToken);
        var metadata = await _instances.ReadLockedAsync(directory, context.CancellationToken);
        InstanceManager.CheckRevision(metadata, request.ExpectedRevision);
        if (metadata.InstalledLoader is not null) throw new LoaderStateException("请先删除实例中已安装的加载器。");
        RequireClient(metadata);
        var inspected = await InspectAsync(metadata.MinecraftVersionId, request.Target,
            request.InstallerJavaExecutable ?? metadata.Editable.JavaExecutable, context.CancellationToken);
        RequireCompatible(inspected.Report);
        return await RunLockedAsync(metadata, context, report =>
            InstallCoreAsync(metadata, request.Target, inspected.Target!.Value, context, report));
    }

    public async Task<LoaderOperationResult> RemoveAsync(LoaderRemovalRequest request, OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateContext(context);
        string directory = await _instances.FindDirectoryAsync(request.InstanceId, context.CancellationToken);
        await using var lease = await _instances.LockInstanceAsync(request.InstanceId, context.CancellationToken);
        var metadata = await _instances.ReadLockedAsync(directory, context.CancellationToken);
        InstanceManager.CheckRevision(metadata, request.ExpectedRevision);
        if (metadata.InstalledLoader is null || metadata.LoaderPatchId != request.ExpectedLoaderPatchId)
            throw new LoaderStateException("加载器 patch 与当前实例不一致。");
        _ = await ReadManagedAsync(metadata, context.CancellationToken);
        return await RunLockedAsync(metadata, context, report => RemoveCoreAsync(metadata, context, report));
    }

    public async Task<LoaderReplacementPlan> PrepareReplacementAsync(LoaderReplacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string directory = await _instances.FindDirectoryAsync(request.InstanceId, cancellationToken);
        await using var lease = await _instances.LockInstanceAsync(request.InstanceId, cancellationToken);
        var metadata = await _instances.ReadLockedAsync(directory, cancellationToken);
        InstanceManager.CheckRevision(metadata, request.ExpectedRevision);
        if (metadata.InstalledLoader is null) throw new LoaderStateException("实例没有可更换的加载器。");
        if (metadata.InstalledLoader == request.Target) throw new LoaderStateException("目标加载器与当前加载器相同。");
        _ = await ReadManagedAsync(metadata, cancellationToken);
        var inspected = await InspectAsync(metadata.MinecraftVersionId, request.Target,
            request.InstallerJavaExecutable ?? metadata.Editable.JavaExecutable, cancellationToken);
        var plan = new LoaderReplacementPlan(Guid.NewGuid(), request, State(metadata), inspected.Report, ModWarning);
        foreach (var entry in _plans)
            if (entry.Value.Expires <= DateTimeOffset.UtcNow) _plans.TryRemove(entry.Key, out _);
        if (_plans.Count >= 256) throw new InstanceConflictException("等待确认的加载器计划数量超过上限。");
        _plans[plan.PlanId] = (plan, DateTimeOffset.UtcNow.AddMinutes(10));
        return plan;
    }

    public async Task<LoaderOperationResult> ReplaceAsync(LoaderReplacementPlan plan,
        LoaderReplacementApproval approval, OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        ValidateContext(context);
        if (!approval.ModCompatibilityWarningAccepted || approval.PlanId != plan.PlanId ||
            !_plans.TryGetValue(plan.PlanId, out var saved) || saved.Expires <= DateTimeOffset.UtcNow ||
            saved.Plan.Request != plan.Request || saved.Plan.CurrentState != plan.CurrentState)
            throw new UserConfirmationRequiredException("更换计划无效、已过期或尚未确认模组兼容性提示。");
        plan = saved.Plan;
        string directory = await _instances.FindDirectoryAsync(plan.Request.InstanceId, context.CancellationToken);
        await using var lease = await _instances.LockInstanceAsync(plan.Request.InstanceId, context.CancellationToken);
        var metadata = await _instances.ReadLockedAsync(directory, context.CancellationToken);
        InstanceManager.CheckRevision(metadata, plan.Request.ExpectedRevision);
        if (State(metadata) != plan.CurrentState || metadata.InstalledLoader is null)
            throw new LoaderStateException("更换计划与当前加载器状态不一致。");
        _ = await ReadManagedAsync(metadata, context.CancellationToken);
        RequireClient(metadata);
        var inspected = await InspectAsync(metadata.MinecraftVersionId, plan.Request.Target,
            plan.Request.InstallerJavaExecutable ?? metadata.Editable.JavaExecutable, context.CancellationToken);
        RequireCompatible(inspected.Report);
        if (!_plans.TryRemove(plan.PlanId, out _)) throw new UserConfirmationRequiredException("更换计划已被使用。");
        Log(ModWarning, true);
        return await RunLockedAsync(metadata, context, async report =>
        {
            // 删除提交后再安装；后续失败不恢复被撤销的加载器。
            var withoutLoader = await RemoveCoreAsync(metadata, context, report);
            context.CancellationToken.ThrowIfCancellationRequested();
            return await InstallCoreAsync(withoutLoader, plan.Request.Target, inspected.Target!.Value, context, report);
        });
    }

    private async Task<LoaderOperationResult> RunLockedAsync(InstanceMetadata initial, OperationContext context,
        Func<Reporter, Task<InstanceMetadata>> action)
    {
        long sequence = 0;
        OperationStage stage = OperationStage.Validating;
        void Report(OperationStage current, double? fraction = null, string? item = null, long bytes = 0, long? total = null)
        {
            stage = current;
            try
            {
                context.Progress?.Report(new(context.OperationId, initial.Id, Interlocked.Increment(ref sequence), current,
                    current == OperationStage.Completed ? 1 : null, fraction, bytes, total, item));
            }
            catch (Exception error) { Log("进度回调失败：" + error.Message, true); }
        }
        try
        {
            Report(OperationStage.Validating);
            var final = await action(Report);
            Report(OperationStage.Completed, 1);
            return new(context.OperationId, State(final));
        }
        catch (Exception error)
        {
            OperationStage failedStage = stage;
            LoaderState? finalState = null;
            bool requiresRecovery = false;
            Exception cause = error;
            try
            {
                string directory = _instances.Paths(initial).GameDirectory;
                finalState = State(await _instances.ReadLockedAsync(directory, CancellationToken.None));
            }
            catch (Exception recoveryError)
            {
                requiresRecovery = true;
                cause = new AggregateException(error, recoveryError);
            }
            if (error is OperationCanceledException && context.CancellationToken.IsCancellationRequested && !requiresRecovery)
            {
                Report(OperationStage.Canceled);
                throw new LoaderOperationCanceledException("加载器操作已取消。", error)
                {
                    OperationId = context.OperationId,
                    FinalState = finalState!
                };
            }
            Report(OperationStage.Failed);
            throw new LoaderOperationFailedException("加载器操作失败：" + error.Message, cause)
            {
                OperationId = context.OperationId,
                Code = requiresRecovery ? "RecoveryRequired" : "LoaderExecutionFailed",
                FailedStage = failedStage,
                FinalState = finalState,
                RequiresRecovery = requiresRecovery
            };
        }
    }

    private async Task<InstanceMetadata> RemoveCoreAsync(InstanceMetadata metadata, OperationContext context, Reporter report)
    {
        report(OperationStage.RemovingLoader);
        var managed = await ReadManagedAsync(metadata, context.CancellationToken);
        var remaining = managed.Patches.Where(x => x.PatchId != metadata.LoaderPatchId).ToArray();
        var effective = _versions.Compose(managed.Base, remaining, metadata.DirectoryName);
        var next = metadata with
        {
            InstalledLoader = null,
            LoaderPatchId = null,
            ActivePatchIds = remaining.Select(x => x.PatchId).ToArray()
        };
        report(OperationStage.Committing);
        // 不删除 mods 目录，不删除任何模组文件或共享库文件。
        return await _instances.CommitLockedAsync(_instances.Paths(metadata).GameDirectory, next,
            new() { [metadata.DirectoryName + ".json"] = _versions.SerializeStandalone(effective) }, context.CancellationToken);
    }

    private async Task<InstanceMetadata> InstallCoreAsync(InstanceMetadata metadata, LoaderSelection target,
        PreparedTarget prepared, OperationContext context, Reporter report)
    {
        var paths = _instances.Paths(metadata);
        string work = InstanceManager.Child(paths.WorkDirectory, context.OperationId.ToString("N"));
        if (Directory.Exists(InstanceManager.Extended(work))) throw new LoaderStateException("安装工作目录已经存在。");
        InstanceManager.EnsureDirectory(work);
        try
        {
            report(OperationStage.Preparing);
            var managed = await ReadBaseForInstallAsync(metadata, context.CancellationToken);
            var profile = (JsonObject)prepared.Profile.DeepClone();
            if (prepared.Forge is ForgePackage forge)
            {
                if (forge.ProcessorFormat)
                {
                    profile = await InstallProcessorFormatAsync(metadata, prepared.Vanilla, forge, prepared.Java!, work,
                        context.CancellationToken, report);
                }
                else await ExtractLegacyAsync(forge, work, context.CancellationToken);
            }
            profile.Remove("id");
            profile.Remove("jar");
            profile.Remove("inheritsFrom");
            var patch = new VersionPatch(Guid.NewGuid(), VersionPatchKind.Loader, target, 1000, profile);
            var patches = managed.Patches.Append(patch).ToArray();
            var effective = _versions.Compose(managed.Base, patches, metadata.DirectoryName);
            await EnsureLibrariesAsync(effective.Json, work, context.CancellationToken, report);
            var next = metadata with
            {
                InstalledLoader = target,
                LoaderPatchId = patch.PatchId,
                ActivePatchIds = patches.Select(x => x.PatchId).ToArray()
            };
            report(OperationStage.Committing);
            return await _instances.CommitLockedAsync(paths.GameDirectory, next, new()
            {
                [".launcher/base.json"] = InstanceManager.Serialize(managed.Base.Json),
                [$".launcher/patches/{patch.PatchId:N}.json"] = InstanceManager.Serialize(patch),
                [metadata.DirectoryName + ".json"] = _versions.SerializeStandalone(effective)
            }, context.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(InstanceManager.Extended(work)))
            {
                InstanceManager.CheckTree(work);
                Directory.Delete(InstanceManager.Extended(work), true);
            }
        }
    }

    private async Task<(VersionDocument Base, IReadOnlyList<VersionPatch> Patches)> ReadBaseForInstallAsync(
        InstanceMetadata metadata, CancellationToken token)
    {
        var paths = _instances.Paths(metadata);
        if (File.Exists(InstanceManager.Extended(paths.BaseVersionJsonFile))) return await ReadManagedAsync(metadata, token);
        if (metadata.ActivePatchIds.Count != 0) throw new LoaderStateException("实例缺少基础版本快照。");
        var leaf = await InstanceManager.ReadObjectAsync(paths.VersionJsonFile, token);
        var resolved = await _versions.ResolveLockedAsync(new(VersionResolver.Text(leaf, "id"), leaf), metadata,
            new(metadata.Id, 64, true), token);
        var json = (JsonObject)resolved.EffectiveDocument.Json.DeepClone();
        string mainClass = json["mainClass"]?.GetValue<string>() ?? "";
        string[] markers = { "net.minecraftforge", "net.neoforged", "net.fabricmc", "org.quiltmc" };
        if (markers.Any(x => mainClass.StartsWith(x, StringComparison.Ordinal)) ||
            (json["libraries"] as JsonArray ?? new()).OfType<JsonObject>()
            .Any(x => markers.Any(marker => (x["name"]?.GetValue<string>() ?? "").StartsWith(marker + ":", StringComparison.Ordinal))))
            throw new LoaderStateException("实例包含没有受管 patch 记录的加载器。");
        json["id"] = VersionResolver.ParentId(metadata);
        json.Remove("jar");
        json.Remove("inheritsFrom");
        return (new(VersionResolver.ParentId(metadata), json), Array.Empty<VersionPatch>());
    }

    private async Task<(VersionDocument Base, IReadOnlyList<VersionPatch> Patches)> ReadManagedAsync(
        InstanceMetadata metadata, CancellationToken token)
    {
        var paths = _instances.Paths(metadata);
        if (!File.Exists(InstanceManager.Extended(paths.BaseVersionJsonFile))) throw new LoaderStateException("基础版本快照不存在。");
        var json = await InstanceManager.ReadObjectAsync(paths.BaseVersionJsonFile, token);
        if (VersionResolver.Text(json, "id") != VersionResolver.ParentId(metadata) || json["inheritsFrom"] is not null)
            throw new LoaderStateException("基础版本快照不合法。");
        var baseVersion = new VersionDocument(VersionResolver.ParentId(metadata), json);
        var patches = new List<VersionPatch>();
        foreach (Guid id in metadata.ActivePatchIds)
        {
            var patch = JsonSerializer.Deserialize<VersionPatch>(await InstanceManager.ReadBytesAsync(
                InstanceManager.Child(paths.PatchDirectory, id.ToString("N") + ".json"), 16 * 1024 * 1024, token), InstanceManager.JsonOptions)
                ?? throw new LoaderStateException("patch 内容为空。");
            if (patch.PatchId != id || patch.Json is null) throw new LoaderStateException("patch 标识不一致。");
            patches.Add(patch);
        }
        var loaders = patches.Where(x => x.Kind == VersionPatchKind.Loader).ToArray();
        if ((metadata.InstalledLoader is null && loaders.Length != 0) ||
            (metadata.InstalledLoader is not null && (loaders.Length != 1 || loaders[0].PatchId != metadata.LoaderPatchId ||
                loaders[0].Loader != metadata.InstalledLoader)))
            throw new LoaderStateException("加载器元数据与 patch 归属不一致。");
        var effective = _versions.Compose(baseVersion, patches, metadata.DirectoryName);
        var expected = _versions.ToInherited(baseVersion, effective, VersionResolver.ParentId(metadata));
        var standalone = _versions.ToStandalone(effective);
        var current = await InstanceManager.ReadObjectAsync(paths.VersionJsonFile, token);
        if (!JsonNode.DeepEquals(current, expected.Json) && !JsonNode.DeepEquals(current, standalone.Json))
            throw new LoaderStateException("版本 JSON 包含未登记的内容，请先处理受管文件冲突。");
        return (baseVersion, patches);
    }

    private async Task<(CompatibilityReport Report, PreparedTarget? Target)> InspectAsync(string minecraftVersion,
        LoaderSelection target, string? javaOverride, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target);
        InstanceManager.ValidateName(minecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Version);
        if (!Enum.IsDefined(target.Kind) || !Regex.IsMatch(target.Version, @"\A[A-Za-z0-9][A-Za-z0-9._+\-]{0,180}\z"))
            throw new ArgumentException("加载器类型或版本不合法。");
        try
        {
            var manifest = await _versions.GetManifestAsync(token);
            var entry = manifest.Versions.FirstOrDefault(x => x.Id == minecraftVersion)
                ?? throw new LoaderCompatibilityException("Minecraft 版本不在官方清单中。");
            var vanilla = await _versions.FetchVersionJsonAsync(entry, token);
            JsonObject profile;
            ForgePackage? forge = null;
            string? java = null;
            if (target.Kind == LoaderKind.Fabric)
            {
                var uri = new Uri($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(target.Version)}/profile/json");
                profile = await _fetcher.ReadAsync(uri, 16 * 1024 * 1024, VersionResolver.ParseObject, token);
                if (VersionResolver.Text(profile, "inheritsFrom") != minecraftVersion)
                    throw new LoaderCompatibilityException("Fabric profile 的 Minecraft 版本不一致。");
                if (!(profile["libraries"] as JsonArray ?? new()).OfType<JsonObject>()
                    .Any(x => x["name"]?.GetValue<string>() == "net.fabricmc:fabric-loader:" + target.Version))
                    throw new LoaderCompatibilityException("Fabric profile 的加载器版本不一致。");
            }
            else
            {
                forge = await GetForgePackageAsync(minecraftVersion, target, token);
                profile = forge.Value.Version;
                if (forge.Value.ProcessorFormat)
                {
                    int minimum = Math.Max(vanilla.Json["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8,
                        profile["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8);
                    java = await _findJava(javaOverride, minimum, token);
                }
            }
            return (new(CompatibilityStatus.Compatible, Array.Empty<CompatibilityIssue>()), new(vanilla, profile, forge, java));
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return (Failure(CompatibilityStatus.Incompatible, "LoaderVersionMissing", "目标加载器版本不存在或不支持该 Minecraft 版本。"), null);
        }
        catch (Exception error) when (error is LoaderCompatibilityException or VersionResolutionException or InvalidDataException or JsonException)
        {
            return (Failure(CompatibilityStatus.Incompatible, "LoaderIncompatible", error.Message), null);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return (Failure(CompatibilityStatus.Unknown, "CompatibilityUnavailable", error.Message), null);
        }
    }

    private async Task<ForgePackage> GetForgePackageAsync(string minecraftVersion, LoaderSelection target, CancellationToken token)
    {
        bool forge = target.Kind == LoaderKind.Forge;
        bool legacyNeo = !forge && minecraftVersion == "1.20.1";
        string artifact = forge || legacyNeo ? "forge" : "neoforge";
        string version = forge || legacyNeo
            ? target.Version.StartsWith(minecraftVersion + "-", StringComparison.Ordinal) ? target.Version : minecraftVersion + "-" + target.Version
            : target.Version;
        string repository = forge ? "https://maven.minecraftforge.net/" : "https://maven.neoforged.net/releases/";
        string group = forge ? "net/minecraftforge" : "net/neoforged";
        var uri = new Uri($"{repository}{group}/{artifact}/{version}/{artifact}-{version}-installer.jar");
        string checksum = await GetSha1Async(new Uri(uri.AbsoluteUri + ".sha1"), token);
        string cache = InstanceManager.Child(_instances.MinecraftRoot, $".launcher/installers/{checksum}.jar");
        await using var lease = await _instances.LockInstallerAsync(checksum, token);
        if (!File.Exists(InstanceManager.Extended(cache)) || !await HasSha1Async(cache, checksum, token))
            await DownloadAsync(uri, cache, checksum, token, null);
        await using var input = new FileStream(InstanceManager.Extended(cache), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var install = ReadArchiveJson(archive, "install_profile.json");
        // spec 存在时采用处理器格式；spec=0 和 spec=1 均可包含 processors。
        if (install.ContainsKey("spec"))
        {
            var runtime = ParseProcessorProfile(archive, install, minecraftVersion);
            string option = DetectClientOption(archive);
            return new(cache, install, runtime, true, option);
        }
        return new(cache, install, ParseLegacyProfile(archive, install, minecraftVersion), false, null);
    }

    private static JsonObject ParseProcessorProfile(ZipArchive archive, JsonObject install, string minecraftVersion)
    {
        if (install["spec"] is not JsonValue spec || !spec.TryGetValue<int>(out int value) || value < 0)
            throw new InvalidDataException("install_profile.json 的 spec 不合法。");
        if (VersionResolver.Text(install, "minecraft") != minecraftVersion)
            throw new LoaderCompatibilityException("安装器要求的 Minecraft 版本与实例不一致。");
        if ((install["processors"] is not null and not JsonArray) || (install["libraries"] is not null and not JsonArray))
            throw new InvalidDataException("处理器安装描述不完整。");
        var runtime = ReadArchiveJson(archive, VersionResolver.Text(install, "json"));
        if (runtime["inheritsFrom"]?.GetValue<string>() is string parent && parent != minecraftVersion)
            throw new LoaderCompatibilityException("加载器版本描述的父版本不一致。");
        InstanceManager.ValidateName(VersionResolver.Text(runtime, "id"));
        return runtime;
    }

    private static JsonObject ParseLegacyProfile(ZipArchive archive, JsonObject install, string minecraftVersion)
    {
        if (install["install"] is not JsonObject details || install["versionInfo"] is not JsonObject runtime)
            throw new InvalidDataException("安装器缺少 install 或 versionInfo。");
        if (VersionResolver.Text(details, "minecraft") != minecraftVersion)
            throw new LoaderCompatibilityException("安装器要求的 Minecraft 版本与实例不一致。");
        _ = MavenPath(VersionResolver.Text(details, "path"));
        _ = Entry(archive, VersionResolver.Text(details, "filePath"));
        if (runtime["inheritsFrom"]?.GetValue<string>() is string parent && parent != minecraftVersion)
            throw new LoaderCompatibilityException("加载器版本描述的父版本不一致。");
        return (JsonObject)runtime.DeepClone();
    }

    private static string DetectClientOption(ZipArchive archive)
    {
        bool camelCase = false;
        long inspected = 0;
        foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".class", StringComparison.Ordinal) &&
                     x.FullName.Contains("/installer/", StringComparison.Ordinal)))
        {
            inspected += entry.Length;
            if (inspected > 32 * 1024 * 1024) throw new InvalidDataException("安装器入口超过检查上限。");
            string constants = Encoding.Latin1.GetString(ReadEntry(entry, 4 * 1024 * 1024));
            if (constants.Contains("install-client", StringComparison.Ordinal)) return "--install-client";
            camelCase |= constants.Contains("installClient", StringComparison.Ordinal);
        }
        return camelCase ? "--installClient" : throw new LoaderCompatibilityException("安装器不提供无界面的客户端安装入口。");
    }

    private async Task<JsonObject> InstallProcessorFormatAsync(InstanceMetadata metadata, VersionDocument vanilla,
        ForgePackage package, string java, string work, CancellationToken token, Reporter report)
    {
        var paths = _instances.Paths(metadata);
        string clientHash = VersionResolver.Text(vanilla.Json["downloads"]?["client"], "sha1");
        if (!await HasSha1Async(paths.ClientJarFile, clientHash, token))
            throw new DownloadIntegrityException("实例客户端与原版客户端摘要不一致。");
        string versionDirectory = InstanceManager.Child(work, "versions/" + metadata.MinecraftVersionId);
        InstanceManager.EnsureDirectory(versionDirectory);
        await InstanceManager.AtomicBytesAsync(InstanceManager.Child(versionDirectory, metadata.MinecraftVersionId + ".json"),
            InstanceManager.Serialize(vanilla.Json), token);
        await CopyAtomicAsync(paths.ClientJarFile, InstanceManager.Child(versionDirectory, metadata.MinecraftVersionId + ".jar"), token);
        await InstanceManager.AtomicBytesAsync(InstanceManager.Child(work, "launcher_profiles.json"),
            Encoding.UTF8.GetBytes("{\"profiles\":{}}"), token);
        string localInstaller = InstanceManager.Child(work, "installer.jar");
        await CopyAtomicAsync(package.Installer, localInstaller, token);
        report(OperationStage.InstallingLoader, null, "执行加载器安装器");
        await _runJava(java, new[] { "-Djava.awt.headless=true", "-Duser.home=" + InstanceManager.Normal(work),
            "-jar", InstanceManager.Normal(localInstaller), package.ClientOption!, InstanceManager.Normal(work) }, work, token);
        string id = VersionResolver.Text(package.Version, "id");
        string output = InstanceManager.Child(work, $"versions/{id}/{id}.json");
        if (!File.Exists(InstanceManager.Extended(output))) throw new IOException("安装器未生成目标版本描述文件。");
        var result = await InstanceManager.ReadObjectAsync(output, token);
        if (VersionResolver.Text(result, "id") != id || result["inheritsFrom"]?.GetValue<string>() != metadata.MinecraftVersionId)
            throw new InvalidDataException("安装产物的版本标识或继承关系不一致。");
        await EnsureLibrariesAsync(package.Install, work, token, report);
        await PublishInstallerLibrariesAsync(work, token, report);
        return result;
    }

    private async Task PublishInstallerLibrariesAsync(string work, CancellationToken token, Reporter report)
    {
        string libraries = InstanceManager.Child(work, "libraries");
        if (!Directory.Exists(InstanceManager.Extended(libraries))) return;
        InstanceManager.CheckTree(libraries);
        foreach (string file in Directory.EnumerateFiles(InstanceManager.Extended(libraries), "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(libraries, InstanceManager.Normal(file));
            string destination = InstanceManager.Child(_instances.MinecraftRoot, "libraries/" + relative);
            report(OperationStage.InstallingLoader, null, "保存安装产物：" + relative);
            await PublishLibraryAsync(file, destination, relative, null, token);
        }
    }

    private static async Task ExtractLegacyAsync(ForgePackage package, string work, CancellationToken token)
    {
        var details = package.Install["install"]!;
        await using var input = new FileStream(InstanceManager.Extended(package.Installer), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var entry = Entry(archive, VersionResolver.Text(details, "filePath"));
        string destination = InstanceManager.Child(work, "libraries/" + MavenPath(VersionResolver.Text(details, "path")));
        await InstanceManager.AtomicWriteAsync(destination, async (output, ct) =>
        {
            await using var source = entry.Open();
            await source.CopyToAsync(output, ct);
        }, token);
    }

    private async Task EnsureLibrariesAsync(JsonObject version, string work, CancellationToken token, Reporter report)
    {
        var libraries = (version["libraries"] as JsonArray ?? new()).OfType<JsonObject>().Where(UsesWindows).ToArray();
        for (int index = 0; index < libraries.Length; index++)
        {
            var library = libraries[index];
            string name = VersionResolver.Text(library, "name");
            report(OperationStage.Downloading, (double)index / Math.Max(1, libraries.Length), name);
            if (library["downloads"]?["artifact"] is JsonObject || library["natives"] is null)
                await EnsureArtifactAsync(library, library["downloads"]?["artifact"] as JsonObject,
                    MavenPath(name), work, token, report);
            if (library["natives"]?["windows"]?.GetValue<string>() is string classifier)
            {
                classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32", StringComparison.Ordinal);
                var native = library["downloads"]?["classifiers"]?[classifier] as JsonObject;
                string[] coordinate = name.Split('@', 2);
                string[] segments = coordinate[0].Split(':');
                string nativeCoordinate = string.Join(':', segments.Take(3)) + ":" + classifier +
                    (coordinate.Length == 2 ? "@" + coordinate[1] : "");
                await EnsureArtifactAsync(library, native, MavenPath(nativeCoordinate), work, token, report);
            }
        }
    }

    private async Task EnsureArtifactAsync(JsonObject library, JsonObject? artifact, string relative,
        string work, CancellationToken token, Reporter report)
    {
        relative = artifact?["path"]?.GetValue<string>() ?? relative;
        string source = InstanceManager.Child(work, "libraries/" + relative);
        string destination = InstanceManager.Child(_instances.MinecraftRoot, "libraries/" + relative);
        string? sha1 = artifact?["sha1"]?.GetValue<string>();
        if (sha1 is not null) VersionResolver.ValidateSha1(sha1);
        if (File.Exists(InstanceManager.Extended(destination)) && sha1 is not null && await HasSha1Async(destination, sha1, token)) return;
        if (!File.Exists(InstanceManager.Extended(source)))
        {
            string? address = artifact?["url"]?.GetValue<string>();
            if (address == "") throw new FileNotFoundException("安装器未生成依赖库：" + relative);
            if (address is null)
            {
                string repository = library["url"]?.GetValue<string>() ?? DefaultRepository(VersionResolver.Text(library, "name"));
                if (!repository.EndsWith('/')) repository += "/";
                address = new Uri(Https(repository), relative.Replace('\\', '/')).AbsoluteUri;
            }
            var uri = Https(address);
            if (sha1 is null)
            {
                try { sha1 = await GetSha1Async(new Uri(uri.AbsoluteUri + ".sha1"), token); }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
            }
            await DownloadAsync(uri, source, sha1, token, (bytes, total) =>
                report(OperationStage.Downloading, null, relative, bytes, total));
        }
        if (sha1 is not null && !await HasSha1Async(source, sha1, token)) throw new DownloadIntegrityException("依赖库摘要不一致：" + relative);
        await PublishLibraryAsync(source, destination, relative, sha1, token);
    }

    private async Task PublishLibraryAsync(string source, string destination, string relative, string? sha1, CancellationToken token)
    {
        if (source.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) ValidateJar(source);
        await using var lease = await _instances.LockLibrariesAsync(token);
        if (File.Exists(InstanceManager.Extended(destination)))
        {
            if (await InstanceManager.Sha256FileAsync(source, token) == await InstanceManager.Sha256FileAsync(destination, token)) return;
            if (sha1 is null) throw new DownloadIntegrityException("共享库存在内容冲突：" + relative);
        }
        await CopyAtomicAsync(source, destination, token);
    }

    private Task DownloadAsync(Uri uri, string destination, string? sha1, CancellationToken token, Action<long, long?>? progress) =>
        _fetcher.DownloadAsync(uri, destination, sha1, null, token, progress);

    private async Task<string> GetSha1Async(Uri uri, CancellationToken token)
    {
        return await _fetcher.ReadAsync(uri, 4096, bytes =>
        {
            string text = Encoding.ASCII.GetString(bytes).Trim();
            string value = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            VersionResolver.ValidateSha1(value);
            return value.ToLowerInvariant();
        }, token);
    }

    private static async Task<bool> HasSha1Async(string path, string expected, CancellationToken token)
    {
        VersionResolver.ValidateSha1(expected);
        InstanceManager.CheckNoReparse(path);
        await using var input = new FileStream(InstanceManager.Extended(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA1.HashDataAsync(input, token)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static Task CopyAtomicAsync(string source, string destination, CancellationToken token) =>
        InstanceManager.AtomicWriteAsync(destination, async (output, ct) =>
        {
            InstanceManager.CheckNoReparse(source);
            await using var input = new FileStream(InstanceManager.Extended(source), FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, ct);
        }, token);

    private static JsonObject ReadArchiveJson(ZipArchive archive, string name) => VersionResolver.ParseObject(ReadEntry(Entry(archive, name), 16 * 1024 * 1024));

    private static ZipArchiveEntry Entry(ZipArchive archive, string name)
    {
        string normalized = name.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(x => x is "" or "." or ".." || x.Contains(':')))
            throw new InvalidDataException("安装器内路径不合法。");
        return archive.GetEntry(normalized) ?? throw new InvalidDataException("安装器内文件不存在：" + normalized);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length > maximum) throw new InvalidDataException("安装器内文件超过大小上限。");
        using var input = entry.Open();
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("安装器内文件超过大小上限。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static void ValidateJar(string path)
    {
        InstanceManager.CheckNoReparse(path);
        using var input = new FileStream(InstanceManager.Extended(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0) throw new InvalidDataException("JAR 文件为空。");
    }

    private static string MavenPath(string coordinate)
    {
        string[] extension = coordinate.Split('@');
        string[] parts = extension[0].Split(':');
        if (extension.Length > 2 || parts.Length is < 3 or > 4) throw new InvalidDataException("Maven 坐标不合法。");
        foreach (string part in parts) InstanceManager.ValidateName(part);
        string suffix = extension.Length == 2 ? extension[1] : "jar";
        InstanceManager.ValidateName(suffix);
        string group = string.Join('/', parts[0].Split('.').Select(x => { InstanceManager.ValidateName(x); return x; }));
        return $"{group}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{(parts.Length == 4 ? "-" + parts[3] : "")}.{suffix}";
    }

    private static bool UsesWindows(JsonObject library)
    {
        if (library["clientreq"] is JsonValue client && client.TryGetValue<bool>(out bool required) && !required) return false;
        if (library["rules"] is not JsonArray rules) return true;
        bool allowed = false;
        foreach (JsonNode? rule in rules)
        {
            bool applies = true;
            if (rule?["os"] is JsonObject os)
            {
                if (os["name"]?.GetValue<string>() is string name && name != "windows") applies = false;
                if (os["arch"]?.GetValue<string>() is string arch)
                {
                    string actual = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
                    {
                        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
                        System.Runtime.InteropServices.Architecture.X86 => "x86",
                        System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
                        _ => "unsupported"
                    };
                    applies &= Regex.IsMatch(actual, arch, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                if (os["version"]?.GetValue<string>() is string version)
                    applies &= Regex.IsMatch(Environment.OSVersion.Version.ToString(), version, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            if (rule?["features"] is JsonObject) return true;
            if (applies) allowed = rule?["action"]?.GetValue<string>() == "allow";
        }
        return allowed;
    }

    private static string DefaultRepository(string coordinate) => coordinate.Split(':')[0] switch
    {
        "net.fabricmc" => "https://maven.fabricmc.net/",
        "net.minecraftforge" => "https://maven.minecraftforge.net/",
        "net.neoforged" => "https://maven.neoforged.net/releases/",
        _ => "https://libraries.minecraft.net/"
    };

    private static Uri Https(string address)
    {
        var uri = new Uri(address, UriKind.Absolute);
        if (uri.Host.Equals("files.minecraftforge.net", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/maven/", StringComparison.Ordinal))
            uri = new Uri("https://maven.minecraftforge.net/" + uri.AbsolutePath[7..]);
        else if (uri.Scheme == Uri.UriSchemeHttp) uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        VersionResolver.RequireHttps(uri);
        return uri;
    }

    private static async Task<string> FindJavaAsync(string? configured, int minimum, CancellationToken token)
    {
        var candidates = new List<string>();
        bool explicitPath = !string.IsNullOrWhiteSpace(configured);
        if (explicitPath) candidates.Add(configured!);
        else
        {
            if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home) candidates.Add(Path.Combine(home, "bin", "java.exe"));
            foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                candidates.Add(Path.Combine(path.Trim().Trim('"'), "java.exe"));
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            foreach (string keyName in new[] { @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\Java Development Kit" })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(keyName);
                if (key is null) continue;
                foreach (string version in key.GetSubKeyNames())
                {
                    using var installation = key.OpenSubKey(version);
                    if (installation?.GetValue("JavaHome") is string javaHome) candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
                }
            }
        }
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string path = InstanceManager.Normal(candidate);
                InstanceManager.CheckNoReparse(path);
                if (!path.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(InstanceManager.Extended(path))) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                string output = await RunJavaAsync(path, new[] { "-version" }, Path.GetDirectoryName(path)!, timeout.Token);
                var match = Regex.Match(output, "(?:version\\s+\"|openjdk\\s+|java\\s+)(\\d+)(?:\\.(\\d+))?", RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                int major = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (major == 1) major = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (major >= minimum) return path;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ArgumentException or InstanceValidationException) { }
        }
        throw new LoaderCompatibilityException($"没有可执行安装器的 Java；需要 Java {minimum} 或更高版本。");
    }

    private static async Task<string> RunJavaAsync(string java, IEnumerable<string> arguments, string work, CancellationToken token)
    {
        var start = new ProcessStartInfo(InstanceManager.Normal(java))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = InstanceManager.Normal(work),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (string key in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH" }) start.Environment.Remove(key);
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Java 进程未启动。");
        var output = new StringBuilder();
        async Task Drain(StreamReader reader)
        {
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
                lock (output) { if (output.Length < 131072) output.Append(buffer, 0, Math.Min(count, 131072 - output.Length)); }
        }
        Task stdout = Drain(process.StandardOutput), stderr = Drain(process.StandardError);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException($"Java 进程退出码为 {process.ExitCode}。\n{output}");
        return output.ToString();
    }

    private void RequireClient(InstanceMetadata metadata)
    {
        var paths = _instances.Paths(metadata);
        if (!File.Exists(InstanceManager.Extended(paths.ClientJarFile)) || !File.Exists(InstanceManager.Extended(paths.VersionJsonFile)))
            throw new LoaderStateException("请先完成 Minecraft 客户端下载。");
    }

    private static CompatibilityReport Failure(CompatibilityStatus status, string code, string message) =>
        new(status, new[] { new CompatibilityIssue(code, message, true) });

    private static void RequireCompatible(CompatibilityReport report)
    {
        if (report.Status == CompatibilityStatus.Compatible && !report.Issues.Any(x => x.IsBlocking)) return;
        var error = new LoaderCompatibilityException(string.Join("；", report.Issues.Select(x => x.Message)));
        error.Data["CompatibilityReport"] = report;
        throw error;
    }

    private static LoaderState State(InstanceMetadata metadata) =>
        new(metadata.Id, metadata.Revision, metadata.InstalledLoader, metadata.LoaderPatchId);

    private static void ValidateContext(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.OperationId == Guid.Empty) throw new ArgumentException("操作标识为空。");
        context.CancellationToken.ThrowIfCancellationRequested();
    }

    private void Log(string message, bool warning = false)
    {
        if (warning) Trace.TraceWarning(message); else Trace.TraceInformation(message);
        try { _log?.Invoke(message); }
        catch (Exception error) { Trace.TraceWarning("日志回调失败：" + error.Message); }
    }
}
