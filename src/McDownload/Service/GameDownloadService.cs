using McDownload.i18n;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McDownload.Model;

namespace McDownload.Service;

public sealed class GameDownloadService(InstanceManager instances, VersionResolver versions, HmclFetchTask fetcher,
    int maxConcurrency = 8)
{
    private readonly GameFileDownloader _files = new(fetcher, maxConcurrency);

    public async Task<InstanceMetadata> InstallAsync(InstanceId id, MinecraftVersionEntry entry, OperationContext context)
    {
        var token = context.CancellationToken;
        string directory = await instances.FindDirectoryAsync(id, token);
        await using var lease = await instances.LockInstanceAsync(id, token);
        var metadata = await instances.ReadLockedAsync(directory, token);
        if (metadata.MinecraftVersionId != entry.Id) throw new InvalidOperationException(Text.Get("GameDownloadService", "VersionMismatch"));
        if (metadata.InstalledLoader is not null || metadata.ActivePatchIds.Count > 0)
            throw new InvalidOperationException(Text.Get("GameDownloadService", "RemoveLoaderFirst"));
        var paths = instances.Paths(metadata);
        var vanilla = await versions.FetchVersionJsonAsync(entry, token);
        var resolved = await versions.ResolveLockedAsync(vanilla, metadata, new(id, 64, true), token);
        var json = resolved.EffectiveDocument.Json;
        long sequence = 0;
        void Report(OperationStep step, string name, double? fraction = null, OperationStage stage = OperationStage.Downloading) => context.Progress?.Report(new(context.OperationId, id,
            Interlocked.Increment(ref sequence), stage, null, fraction, 0, null, name, step));
        Report(OperationStep.Client, Text.Get("GameDownloadService", "DownloadingClient"), 0);
        await FetchAsync(json["downloads"]?["client"] as JsonObject ?? throw new InvalidDataException(Text.Get("GameDownloadService", "ClientMissing")), paths.ClientJarFile, token,
            (bytes, total) => Report(OperationStep.Client, Text.Get("GameDownloadService", "DownloadingClient"), total is > 0 ? (double)bytes / total.Value : null));
        Report(OperationStep.Client, Text.Get("GameDownloadService", "ClientCompleted"), 1);
        Report(OperationStep.Libraries, Text.Get("GameDownloadService", "DownloadingLibraries"), 0);
        if (json["libraries"] is JsonArray libraries)
        {
            var applicableLibraries = libraries.OfType<JsonObject>().Where(IsWindows).ToArray();
            await using var libraryLease = await instances.LockLibrariesAsync(token);
            var files = new List<GameDownloadFile>();
            foreach (var library in applicableLibraries)
            {
                if (library["downloads"]?["artifact"] is JsonObject artifact)
                    files.Add(Describe(artifact, InstanceManager.Child(paths.LibrariesDirectory, VersionResolver.Text(artifact, "path"))));
                if (library["natives"]?["windows"]?.GetValue<string>() is string classifier)
                {
                    classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                    var native = library["downloads"]?["classifiers"]?[classifier] as JsonObject
                        ?? throw new InvalidDataException(Text.Get("GameDownloadService", "NativeMissing"));
                    files.Add(Describe(native, InstanceManager.Child(paths.LibrariesDirectory, VersionResolver.Text(native, "path"))));
                }
            }
            await _files.DownloadAsync(files, token,
                (count, total) =>
                {
                    if (count % 10 == 0 || count == total) Report(OperationStep.Libraries,
                        Text.Format("GameDownloadService", "LibraryProgress", count, total), (double)count / Math.Max(1, total));
                },
                (count, total) =>
                {
                    if (count % 10 == 0 || count == total) Report(OperationStep.Libraries,
                        Text.Format("GameDownloadService", "VerifyingLibraries", count, total), stage: OperationStage.Verifying);
                });
        }
        Report(OperationStep.Libraries, Text.Get("GameDownloadService", "LibrariesCompleted"), 1);
        Report(OperationStep.Assets, Text.Get("GameDownloadService", "DownloadingAssets"), 0);
        if (json["assetIndex"] is JsonObject assetIndex)
        {
            string indexId = VersionResolver.Text(assetIndex, "id"); InstanceManager.ValidateName(indexId);
            string indexPath = InstanceManager.Child(paths.AssetsDirectory, "indexes/" + indexId + ".json");
            Report(OperationStep.Assets, Text.Get("GameDownloadService", "DownloadingAssetIndex"), 0);
            await using var assetsLease = await instances.LockLibrariesAsync(token);
            await FetchAsync(assetIndex, indexPath, token);
            var index = await InstanceManager.ReadObjectAsync(indexPath, token);
            var objects = index["objects"] as JsonObject ?? throw new InvalidDataException(Text.Get("GameDownloadService", "AssetIndexInvalid"));
            var entries = objects.ToArray();
            var files = entries.Select(item =>
            {
                string hash = VersionResolver.Text(item.Value, "hash"); VersionResolver.ValidateSha1(hash);
                string relative = hash[..2] + "/" + hash;
                return new GameDownloadFile(new Uri("https://resources.download.minecraft.net/" + relative),
                    InstanceManager.Child(paths.AssetsDirectory, "objects/" + relative), hash, item.Value?["size"]?.GetValue<long>());
            }).ToArray();
            await _files.DownloadAsync(files, token,
                (count, total) =>
                {
                    if (count % 50 == 0 || count == total) Report(OperationStep.Assets,
                        Text.Format("GameDownloadService", "AssetProgress", count, total), (double)count / Math.Max(1, total));
                },
                (count, total) =>
                {
                    if (count % 50 == 0 || count == total) Report(OperationStep.Assets,
                        Text.Format("GameDownloadService", "VerifyingAssets", count, total), stage: OperationStage.Verifying);
                });
            bool virtualAssets = index["virtual"]?.GetValue<bool>() == true;
            bool mapToResources = index["map_to_resources"]?.GetValue<bool>() == true;
            var copies = PlanAssetCopies(entries, paths.AssetsDirectory, paths.GameDirectory, indexId, virtualAssets, mapToResources);
            int completed = 0;
            if (virtualAssets || mapToResources)
                await fetcher.ForEachAsync(copies, maxConcurrency, async (copy, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                await CopyAsync(copy.Source, copy.Target, cancellation);
                int count = Interlocked.Increment(ref completed);
                if (count % 50 == 0 || count == copies.Count) Report(OperationStep.Assets, Text.Format("GameDownloadService", "AssetProgress", count, copies.Count), (double)count / Math.Max(1, copies.Count));
            }, token);
        }
        Report(OperationStep.Assets, Text.Get("GameDownloadService", "AssetsCompleted"), 1);
        var baseJson = (JsonObject)json.DeepClone();
        baseJson["id"] = VersionResolver.ParentId(metadata); baseJson.Remove("inheritsFrom"); baseJson.Remove("jar");
        var baseVersion = new VersionDocument(VersionResolver.ParentId(metadata), baseJson);
        var effective = versions.Compose(baseVersion, Array.Empty<VersionPatch>(), metadata.DirectoryName);
        Report(OperationStep.Saving, Text.Get("GameDownloadService", "SavingVersion"), 0);
        var result = await instances.CommitLockedAsync(paths.GameDirectory, metadata, new()
        {
            [".launcher/base.json"] = InstanceManager.Serialize(baseJson),
            [metadata.DirectoryName + ".json"] = versions.SerializeStandalone(effective)
        }, token);
        Report(OperationStep.Saving, Text.Get("GameDownloadService", "VersionSaved"), 1);
        return result;
    }
    private async Task FetchAsync(JsonObject descriptor, string path, CancellationToken token, Action<long, long?>? progress = null)
    {
        var file = Describe(descriptor, path);
        VersionResolver.ValidateSha1(file.Sha1);
        if (await GameFileDownloader.VerifyAsync(file, token)) return;
        await fetcher.DownloadAsync(file.Uri, path, file.Sha1, file.Size, token, progress);
    }
    private static GameDownloadFile Describe(JsonObject descriptor, string path) => new(
        new Uri(VersionResolver.Text(descriptor, "url")), path, VersionResolver.Text(descriptor, "sha1"), descriptor["size"]?.GetValue<long>());
    internal static IReadOnlyCollection<(string Source, string Target)> PlanAssetCopies(
        IReadOnlyCollection<KeyValuePair<string, JsonNode?>> entries, string assetsDirectory, string gameDirectory,
        string indexId, bool virtualAssets, bool mapToResources)
    {
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            string hash = VersionResolver.Text(item.Value, "hash"); VersionResolver.ValidateSha1(hash);
            string source = InstanceManager.Child(assetsDirectory, "objects/" + hash[..2] + "/" + hash);
            void Add(string target)
            {
                if (copies.TryGetValue(target, out var previous) && !previous.Equals(source, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Text.Get("GameDownloadService", "ConflictingFile"));
                copies.TryAdd(target, source);
            }
            if (virtualAssets) Add(InstanceManager.Child(assetsDirectory, "virtual/" + indexId + "/" + item.Key));
            if (mapToResources) Add(InstanceManager.Child(gameDirectory, "resources/" + item.Key));
        }
        return copies.Select(x => (x.Value, x.Key)).ToArray();
    }
    private static Task CopyAsync(string source, string target, CancellationToken token) => InstanceManager.AtomicWriteAsync(target, async (output, ct) =>
    {
        await using var input = new FileStream(InstanceManager.Extended(source), FileMode.Open, FileAccess.Read, FileShare.Read);
        await input.CopyToAsync(output, ct);
    }, token);
    private static bool IsWindows(JsonObject library)
    {
        if (library["rules"] is not JsonArray rules) return true;
        bool allowed = false;
        foreach (var rule in rules)
        {
            var os = rule?["os"];
            if (os?["name"]?.GetValue<string>() is string name && name != "windows") continue;
            string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            { System.Runtime.InteropServices.Architecture.X86 => "x86", System.Runtime.InteropServices.Architecture.Arm64 => "aarch64", _ => "x86_64" };
            if (os?["arch"]?.GetValue<string>() is string pattern && !Regex.IsMatch(arch, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) continue;
            if (os?["version"]?.GetValue<string>() is string version && !Regex.IsMatch(Environment.OSVersion.Version.ToString(), version, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) continue;
            allowed = rule?["action"]?.GetValue<string>() == "allow";
        }
        return allowed;
    }
}
