using McDownload.i18n;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using McDownload.Model;

namespace McDownload.Service;

public sealed class ModpackImportService : IModpackImporter
{
    private readonly InstanceManager _instances;
    private readonly VersionResolver _versions;
    private readonly GameDownloadService _games;
    private readonly ILoaderInstaller _loaders;
    private readonly ICurseForgeFileResolver _curseforge;
    private readonly ModpackFileDownloader _downloads;
    private readonly ModpackArchiveReader _reader = new();

    public static IReadOnlyList<(OperationStep Kind, string Title)> Steps { get; } = new[]
    {
        (OperationStep.Preparing, Text.Get("ModpackImportService", "CheckPack")), (OperationStep.Client, Text.Get("Common", "DownloadClient")),
        (OperationStep.Libraries, Text.Get("Common", "DownloadLibraries")), (OperationStep.Assets, Text.Get("Common", "DownloadAssets")),
        (OperationStep.Loader, Text.Get("ModpackImportService", "InstallLoader")), (OperationStep.ModpackFiles, Text.Get("ModpackImportService", "DownloadPackFiles")),
        (OperationStep.Overrides, Text.Get("ModpackImportService", "ApplyOverrides")), (OperationStep.Saving, Text.Get("ModpackImportService", "VerifyPack"))
    };

    public ModpackImportService(InstanceManager instances, VersionResolver versions, GameDownloadService games,
        ILoaderInstaller loaders, ICurseForgeFileResolver curseforge, ModpackFileDownloader downloads)
    {
        _instances = instances; _versions = versions; _games = games;
        _loaders = loaders; _curseforge = curseforge; _downloads = downloads;
    }

    public Task<ModpackDefinition> PreviewAsync(string archivePath, CancellationToken token) => _reader.ReadAsync(archivePath, token);

    public async Task<ModpackImportResult> ImportAsync(ModpackImportRequest request, OperationContext context)
    {
        ValidateContext(context);
        InstanceManager.ValidateName(request.DirectoryName);
        await using var archive = ModpackArchiveReader.Open(request.ArchivePath);
        var pack = await _reader.ReadAsync(archive, Path.GetFileName(request.ArchivePath), context.CancellationToken);
        if (request.ExpectedArchiveSha256 is { } expected && !pack.ArchiveSha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InstanceConflictException(Text.Get("ModpackImportService", "ArchiveChanged"));
        string[] selected = request.SelectedOptionalFiles?.ToArray() ?? Array.Empty<string>();
        ValidateSelection(pack, selected);
        foreach (var file in pack.Files.Where(x => x.Path is not null)) ModpackSafety.ValidateTarget(file.Path!, request.DirectoryName);
        var resolved = new Dictionary<string, ModpackFile>();
        var missing = new List<MissingModpackFile>();
        var references = pack.Files.Where(x => x.CurseForge is not null && (!x.Optional || selected.Contains(x.Key))).ToArray();
        foreach (var file in references)
        {
            context.Progress?.Report(new(context.OperationId, default, resolved.Count + 1, OperationStage.Downloading,
                null, (double)resolved.Count / references.Length, 0, null,
                Text.Format("ModpackImportService", "PrecheckingFiles", resolved.Count + 1, references.Length), OperationStep.Preparing));
            var resolution = await _curseforge.ResolveAsync(file, context.CancellationToken);
            if (resolution.File is not { } descriptor)
                throw new InvalidOperationException(Text.Format("ModpackImportService", "PrecheckFailed", file.Key, resolution.Missing?.Reason));
            if (descriptor.Key != file.Key || descriptor.CurseForge != file.CurseForge || descriptor.ArchiveEntry is not null)
                throw new InvalidDataException(Text.Get("ModpackImportService", "ResolvedFileMismatch"));
            ModpackSafety.ValidateFile(descriptor);
            ModpackSafety.ValidateTarget(descriptor.Path!, request.DirectoryName);
            resolved[file.Key] = descriptor;
            if (resolution.Missing is { } issue) missing.Add(issue);
        }
        var metadata = await _instances.CreateAsync(new(request.DirectoryName, pack.MinecraftVersion, IsolationPolicy.PerInstance,
            new(pack.Name, pack.Description, request.JavaExecutable)), context.CancellationToken);
        var state = new ModpackImportState(1, metadata.Id, metadata.DirectoryName, InstanceManager.Normal(request.ArchivePath),
            pack.ArchiveSha256, pack.Name, pack.Version, selected, ModpackImportStatus.Importing, false, false,
            resolved, new Dictionary<string, string>(), missing.ToArray());
        // 创建后即记录恢复入口；取消不能留下没有导入记录的实例。
        await SaveAsync(metadata, state);
        return await ExecuteAsync(metadata, state, pack, archive, null, context);
    }

    public async Task CheckAccessAsync(ModpackDefinition pack, IReadOnlyList<string> selectedOptionalFiles, CancellationToken token)
    {
        var file = pack.Files.FirstOrDefault(x => x.CurseForge is not null && (!x.Optional || selectedOptionalFiles.Contains(x.Key)));
        if (file is null) return;
        var resolution = await _curseforge.ResolveAsync(file, token);
        if (resolution.File is null)
            throw new InvalidOperationException(Text.Format("ModpackImportService", "PrecheckFailed", file.Key, resolution.Missing?.Reason));
    }

    public async Task<ModpackImportResult> ResumeAsync(ModpackResumeRequest request, OperationContext context)
    {
        ValidateContext(context);
        var metadata = await _instances.GetAsync(request.InstanceId, context.CancellationToken);
        var state = await GetStateAsync(request.InstanceId, context.CancellationToken)
            ?? throw new InvalidOperationException(Text.Get("ModpackImportService", "ImportRecordMissing"));
        await using var archive = ModpackArchiveReader.Open(request.ArchivePath ?? state.ArchivePath);
        var pack = await _reader.ReadAsync(archive, Path.GetFileName(request.ArchivePath ?? state.ArchivePath), context.CancellationToken);
        if (!pack.ArchiveSha256.Equals(state.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InstanceConflictException(Text.Get("ModpackImportService", "ResumeArchiveMismatch"));
        ValidateSelection(pack, state.SelectedOptionalFiles);
        if (pack.MinecraftVersion != metadata.MinecraftVersionId || state.Name != pack.Name || state.Version != pack.Version)
            throw new InstanceConflictException(Text.Get("ModpackImportService", "ImportRecordMismatch"));
        return await ExecuteAsync(metadata, state with { ArchivePath = InstanceManager.Normal(request.ArchivePath ?? state.ArchivePath) },
            pack, archive, request.LocalFiles, context);
    }

    public async Task<ModpackImportState?> GetStateAsync(InstanceId id, CancellationToken token)
    {
        var metadata = await _instances.GetAsync(id, token);
        var paths = _instances.Paths(metadata);
        string file = StatePath(paths);
        if (!File.Exists(file)) return null;
        var state = JsonSerializer.Deserialize<ModpackImportState>(await InstanceManager.ReadBytesAsync(file, 32 * 1024 * 1024, token), InstanceManager.JsonOptions)
            ?? throw new InvalidDataException(Text.Get("ModpackImportService", "ImportRecordEmpty"));
        if (state.FormatVersion != 1 || state.InstanceId != id || state.DirectoryName != metadata.DirectoryName ||
            !Enum.IsDefined(state.Status) || state.SelectedOptionalFiles is null || state.ResolvedFiles is null ||
            state.WrittenFiles is null || state.MissingFiles is null)
            throw new InvalidDataException(Text.Get("ModpackImportService", "InvalidImportRecord"));
        ModpackSafety.ValidateHashes(new(Sha256: state.ArchiveSha256));
        foreach (var written in state.WrittenFiles)
        {
            ModpackSafety.ValidateTarget(written.Key, metadata.DirectoryName);
            ModpackSafety.ValidateHashes(new(Sha256: written.Value));
        }
        if (state.PendingWrite is { } pending)
        {
            ModpackSafety.ValidateTarget(pending.Path, metadata.DirectoryName);
            ModpackSafety.ValidateHashes(new(Sha256: pending.Sha256));
        }
        return state;
    }

    private async Task<ModpackImportResult> ExecuteAsync(InstanceMetadata metadata, ModpackImportState initial,
        ModpackDefinition pack, FileStream archiveStream, IReadOnlyDictionary<string, string>? localFiles, OperationContext context)
    {
        var token = context.CancellationToken;
        var paths = _instances.Paths(metadata);
        string lockPath = InstanceManager.Child(paths.GameDirectory, ".launcher/modpack/import.lock");
        InstanceManager.EnsureDirectory(Path.GetDirectoryName(lockPath)!);
        await using var operationLease = new FileStream(InstanceManager.Extended(lockPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // 在导入锁内重新读取，防止两个恢复请求使用同一个过期快照。
        var state = (await GetStateAsync(metadata.Id, token))! with { ArchivePath = initial.ArchivePath };
        var resolved = new Dictionary<string, ModpackFile>(state.ResolvedFiles, StringComparer.Ordinal);
        var written = new Dictionary<string, string>(state.WrittenFiles, StringComparer.OrdinalIgnoreCase);
        var missing = state.MissingFiles.ToList();
        void SetMissing(MissingModpackFile issue)
        {
            missing.RemoveAll(x => x.Key == issue.Key);
            missing.Add(issue);
        }
        long sequence = 0;
        void Report(OperationStep step, string title, double? fraction = null) => context.Progress?.Report(new(context.OperationId,
            metadata.Id, Interlocked.Increment(ref sequence), OperationStage.Downloading, null, fraction, 0, null, title, step));
        async Task Persist() => await SaveAsync(metadata, state with { ResolvedFiles = resolved, WrittenFiles = written,
            MissingFiles = missing.ToArray() });
        try
        {
            state = state with { Status = ModpackImportStatus.Importing, Error = null };
            await Persist();
            Report(OperationStep.Preparing, Text.Get("ModpackImportService", "ResolvingFiles"), 0);
            var optionalKeys = state.SelectedOptionalFiles.ToHashSet(StringComparer.Ordinal);
            var selected = pack.Files.Where(x => !x.Optional || optionalKeys.Contains(x.Key)).ToArray();
            var curseforgeKeys = selected.Where(x => x.CurseForge is not null).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            if (localFiles is not null && localFiles.Keys.Any(x => !curseforgeKeys.Contains(x)))
                throw new ArgumentException(Text.Get("ModpackImportService", "InvalidLocalFiles"));
            var files = new List<ModpackFile>();
            foreach (var file in selected)
            {
                token.ThrowIfCancellationRequested();
                if (file.CurseForge is null) { files.Add(file); continue; }
                if (!resolved.TryGetValue(file.Key, out var descriptor))
                {
                    var resolution = await _curseforge.ResolveAsync(file, token);
                    if (resolution.File is null)
                    {
                        SetMissing(resolution.Missing ?? new(file.Key, Text.Get("ModpackImportService", "FileResolutionFailed")));
                        throw new InvalidOperationException(Text.Format("ModpackImportService", "PrecheckFailed", file.Key, resolution.Missing?.Reason));
                    }
                    descriptor = resolution.File;
                    if (descriptor.Key != file.Key || descriptor.CurseForge != file.CurseForge || descriptor.ArchiveEntry is not null)
                        throw new InvalidDataException(Text.Get("ModpackImportService", "ResolvedFileMismatch"));
                    resolved[file.Key] = descriptor;
                    if (resolution.Missing is { } issue) SetMissing(issue);
                    else missing.RemoveAll(x => x.Key == file.Key);
                }
                ModpackSafety.ValidateFile(descriptor);
                if (descriptor.CurseForge != file.CurseForge || descriptor.Key != file.Key || descriptor.ArchiveEntry is not null)
                    throw new InvalidDataException(Text.Get("ModpackImportService", "SavedFileMismatch"));
                files.Add(descriptor);
            }
            var remotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                ModpackSafety.ValidateTarget(file.Path!, metadata.DirectoryName);
                if (file.ArchiveEntry is null && !remotePaths.Add(file.Path!)) throw new InvalidDataException(Text.Get("ModpackImportService", "DuplicateTargetPath"));
            }
            ModpackArchiveReader.ValidateTargets(files);
            var effective = files.GroupBy(x => x.Path!, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()).ToArray();
            var effectiveKeys = effective.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            missing.RemoveAll(x => resolved.TryGetValue(x.Key, out var descriptor) && descriptor.Path is not null && !effectiveKeys.Contains(x.Key));
            var effectivePaths = effective.Select(x => x.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (written.Keys.Any(x => !effectivePaths.Contains(x)) || state.PendingWrite is { } p && !effectivePaths.Contains(p.Path))
                throw new InstanceConflictException(Text.Get("ModpackImportService", "WrittenFileMismatch"));
            if (state.PendingWrite is { } recovery)
            {
                await using var lease = await _instances.LockInstanceAsync(metadata.Id, token);
                string target = InstanceManager.Child(paths.GameDirectory, recovery.Path);
                if (File.Exists(target))
                {
                    if (await InstanceManager.Sha256FileAsync(target, token) != recovery.Sha256)
                        throw new InstanceConflictException(Text.Format("ModpackImportService", "RecoveryFileConflict", recovery.Path));
                    written[recovery.Path] = recovery.Sha256;
                }
                state = state with { PendingWrite = null };
            }
            await Persist();
            Report(OperationStep.Preparing, Text.Get("ModpackImportService", "PackChecked"), 1);

            if (!state.GameInstalled)
            {
                if (metadata.InstalledLoader is not null) throw new InstanceConflictException(Text.Get("ModpackImportService", "UnexpectedLoader"));
                var manifest = await _versions.GetManifestAsync(token);
                var version = manifest.Versions.FirstOrDefault(x => x.Id == pack.MinecraftVersion)
                    ?? throw new InvalidDataException(Text.Get("ModpackImportService", "MinecraftVersionMissing"));
                metadata = await _games.InstallAsync(metadata.Id, version, context with
                {
                    Progress = new MappedProgress(context.Progress, p => p.Step == OperationStep.Saving
                        ? p with { Step = OperationStep.Assets, StageFraction = 1 } : p)
                });
                state = state with { GameInstalled = true };
                await Persist();
            }
            else
            {
                if (!File.Exists(paths.ClientJarFile) || !File.Exists(paths.BaseVersionJsonFile) || !File.Exists(paths.VersionJsonFile))
                    throw new InstanceConflictException(Text.Get("ModpackImportService", "CompletedGameFilesMissing"));
                Report(OperationStep.Assets, Text.Get("ModpackImportService", "GameInstalled"), 1);
            }
            metadata = await _instances.GetAsync(metadata.Id, token);
            if (metadata.InstalledLoader is not null && metadata.InstalledLoader != pack.Loader)
                throw new InstanceConflictException(Text.Get("ModpackImportService", "LoaderMismatch"));
            if (pack.Loader is { } loader)
            {
                if (metadata.InstalledLoader is null)
                {
                    if (state.LoaderInstalled) throw new InstanceConflictException(Text.Get("ModpackImportService", "LoaderRemoved"));
                    await _loaders.InstallAsync(new(metadata.Id, metadata.Revision, loader, metadata.Editable.JavaExecutable), context with
                    {
                        Progress = new MappedProgress(context.Progress, p => p with { Step = OperationStep.Loader,
                            StageFraction = p.Stage == OperationStage.Completed ? 1 : p.StageFraction })
                    });
                    metadata = await _instances.GetAsync(metadata.Id, token);
                }
                await _loaders.GetStateAsync(metadata.Id, token);
            }
            state = state with { LoaderInstalled = true };
            await Persist();
            Report(OperationStep.Loader, Text.Get("ModpackImportService", "LoaderCompleted"), 1);

            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, true);
            int downloadCount = effective.Count(x => x.ArchiveEntry is null);
            int overrideCount = effective.Length - downloadCount;
            int downloadsDone = 0, overridesDone = 0;
            void ReportFile(ModpackFile file)
            {
                bool download = file.ArchiveEntry is null;
                int done = download ? ++downloadsDone : ++overridesDone;
                Report(download ? OperationStep.ModpackFiles : OperationStep.Overrides, Text.Format("ModpackImportService", "FileVerified", file.Path),
                    (double)done / Math.Max(1, download ? downloadCount : overrideCount));
            }
            foreach (var file in effective.OrderBy(x => x.ArchiveEntry is null ? 0 : 1))
            {
                token.ThrowIfCancellationRequested();
                var step = file.ArchiveEntry is null ? OperationStep.ModpackFiles : OperationStep.Overrides;
                Report(step, Text.Format("ModpackImportService", "InstallingFile", file.Path), null);
                string target = InstanceManager.Child(paths.GameDirectory, file.Path!);
                await using var lease = await _instances.LockInstanceAsync(metadata.Id, token);
                string? current = File.Exists(target) ? await InstanceManager.Sha256FileAsync(target, token) : null;
                if (state.PendingWrite is { } pending && pending.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) &&
                    current == pending.Sha256)
                {
                    written[file.Path!] = current;
                    state = state with { PendingWrite = null };
                    await Persist();
                }
                if (written.TryGetValue(file.Path!, out string? installedHash))
                {
                    if (current != installedHash) throw new InstanceConflictException(Text.Format("ModpackImportService", "ModifiedFileConflict", file.Path));
                    missing.RemoveAll(x => x.Key == file.Key);
                    ReportFile(file);
                    continue;
                }
                if (current is not null) throw new InstanceConflictException(Text.Format("ModpackImportService", "ExistingFileConflict", file.Path));
                if (file.Downloads.Count == 0 && file.ArchiveEntry is null && (localFiles is null || !localFiles.ContainsKey(file.Key)))
                {
                    if (!missing.Any(x => x.Key == file.Key)) missing.Add(new(file.Key, Text.Get("ModpackImportService", "ManualFileRequired")));
                    continue;
                }
                string cache = InstanceManager.Child(paths.GameDirectory, ".launcher/modpack/payload.tmp");
                string digest;
                try
                {
                    string? local = null;
                    localFiles?.TryGetValue(file.Key, out local);
                    if (file.ArchiveEntry is { } entryName)
                    {
                        var entry = archive.GetEntry(entryName) ?? throw new InvalidDataException(Text.Get("ModpackImportService", "OverrideMissing"));
                        await using var input = entry.Open();
                        digest = await StageAsync(input, cache, file, token);
                    }
                    else if (local is not null)
                    {
                        await using var input = ModpackArchiveReader.Open(local);
                        digest = await StageAsync(input, cache, file, token);
                    }
                    else digest = await _downloads.DownloadAsync(file, cache, token);
                    state = state with { PendingWrite = new(file.Path!, digest) };
                    await Persist();
                    InstanceManager.EnsureDirectory(Path.GetDirectoryName(target)!);
                    InstanceManager.CheckNoReparse(target);
                    token.ThrowIfCancellationRequested();
                    File.Move(InstanceManager.Extended(cache), InstanceManager.Extended(target), false);
                    written[file.Path!] = digest;
                    state = state with { PendingWrite = null };
                    await Persist();
                    missing.RemoveAll(x => x.Key == file.Key);
                }
                finally
                {
                    if (File.Exists(cache)) { InstanceManager.CheckNoReparse(cache); File.Delete(cache); }
                }
                ReportFile(file);
            }
            Report(OperationStep.Saving, Text.Get("ModpackImportService", "VerifyingResult"), 0);
            await using (var lease = await _instances.LockInstanceAsync(metadata.Id, token))
            {
                var baseVersion = await InstanceManager.ReadObjectAsync(paths.BaseVersionJsonFile, token);
                string clientHash = VersionResolver.Text(baseVersion["downloads"]?["client"], "sha1");
                VersionResolver.ValidateSha1(clientHash);
                await using (var client = ModpackArchiveReader.Open(paths.ClientJarFile))
                    if (!Convert.ToHexString(await SHA1.HashDataAsync(client, token)).Equals(clientHash, StringComparison.OrdinalIgnoreCase))
                        throw new DownloadIntegrityException(Text.Get("ModpackImportService", "ClientHashMismatch"));
                foreach (var item in written)
                {
                    string target = InstanceManager.Child(paths.GameDirectory, item.Key);
                    if (!File.Exists(target) || await InstanceManager.Sha256FileAsync(target, token) != item.Value)
                        throw new InstanceConflictException(Text.Format("ModpackImportService", "FinalFileHashMismatch", item.Key));
                }
                state = state with { Status = missing.Count == 0 ? ModpackImportStatus.Completed : ModpackImportStatus.NeedsFiles,
                    MissingFiles = missing.ToArray(), ResolvedFiles = resolved, WrittenFiles = written };
                await SaveAsync(metadata, state);
            }
            Report(OperationStep.Saving, missing.Count == 0 ? Text.Get("ModpackImportService", "ImportCompleted") : Text.Format("ModpackImportService", "FilesStillRequired", missing.Count), 1);
            return new(await _instances.GetAsync(metadata.Id, token), state);
        }
        catch (Exception error)
        {
            bool canceled = error is OperationCanceledException && token.IsCancellationRequested;
            await SaveAsync(metadata, state with { Status = canceled ? ModpackImportStatus.Canceled : ModpackImportStatus.Failed,
                Error = DiagnosticText.Summary(error), ResolvedFiles = resolved, WrittenFiles = written, MissingFiles = missing.ToArray() });
            throw;
        }
    }

    private static async Task<string> StageAsync(Stream input, string cache, ModpackFile file, CancellationToken token)
    {
        string? digest = null;
        await InstanceManager.AtomicWriteAsync(cache, async (output, ct) =>
            digest = await ModpackSafety.CopyVerifiedAsync(input, output, file.Size!.Value, file.Hashes, ct), token);
        return digest!;
    }

    private static string StatePath(InstancePaths paths) => InstanceManager.Child(paths.GameDirectory, ".launcher/modpack/state.json");
    private Task SaveAsync(InstanceMetadata metadata, ModpackImportState state) =>
        InstanceManager.AtomicBytesAsync(StatePath(_instances.Paths(metadata)), InstanceManager.Serialize(state), CancellationToken.None);

    private static void ValidateSelection(ModpackDefinition pack, IReadOnlyList<string> selected)
    {
        var optional = pack.Files.Where(x => x.Optional).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Count || selected.Any(x => !optional.Contains(x)))
            throw new ArgumentException(Text.Get("ModpackImportService", "InvalidOptionalSelection"));
    }

    private static void ValidateContext(OperationContext context)
    {
        if (context.OperationId == Guid.Empty) throw new ArgumentException(Text.Get("ModpackImportService", "OperationIdEmpty"));
        context.CancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class MappedProgress(IProgress<OperationProgress>? target, Func<OperationProgress, OperationProgress> map)
        : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => target?.Report(map(value));
    }
}
