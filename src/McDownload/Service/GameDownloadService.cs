using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McDownload.Model;

namespace McDownload.Service;

public sealed class GameDownloadService(InstanceManager instances, VersionResolver versions, HmclFetchTask fetcher)
{
    public async Task<InstanceMetadata> InstallAsync(InstanceId id, MinecraftVersionEntry entry, OperationContext context)
    {
        var token = context.CancellationToken;
        string directory = await instances.FindDirectoryAsync(id, token);
        await using var lease = await instances.LockInstanceAsync(id, token);
        var metadata = await instances.ReadLockedAsync(directory, token);
        if (metadata.MinecraftVersionId != entry.Id) throw new InvalidOperationException("实例游戏版本不一致。");
        if (metadata.InstalledLoader is not null || metadata.ActivePatchIds.Count > 0)
            throw new InvalidOperationException("请先删除加载器，再下载原版游戏。");
        var paths = instances.Paths(metadata);
        var vanilla = await versions.FetchVersionJsonAsync(entry, token);
        var resolved = await versions.ResolveLockedAsync(vanilla, metadata, new(id, 64, true), token);
        var json = resolved.EffectiveDocument.Json;
        long sequence = 0;
        void Report(OperationStep step, string name, double? fraction = null) => context.Progress?.Report(new(context.OperationId, id,
            Interlocked.Increment(ref sequence), OperationStage.Downloading, null, fraction, 0, null, name, step));
        Report(OperationStep.Client, "正在下载客户端", 0);
        await FetchAsync(json["downloads"]?["client"] as JsonObject ?? throw new InvalidDataException("客户端信息缺失。"), paths.ClientJarFile, token,
            (bytes, total) => Report(OperationStep.Client, "正在下载客户端", total is > 0 ? (double)bytes / total.Value : null));
        Report(OperationStep.Client, "客户端已完成", 1);
        Report(OperationStep.Libraries, "正在下载依赖库", 0);
        if (json["libraries"] is JsonArray libraries)
        {
            var applicableLibraries = libraries.OfType<JsonObject>().Where(IsWindows).ToArray();
            await using var libraryLease = await instances.LockLibrariesAsync(token);
            int completed = 0;
            await fetcher.ForEachAsync(applicableLibraries, 8, async (library, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                Report(OperationStep.Libraries, "下载依赖库：" + library["name"]?.GetValue<string>(),
                    (double)Volatile.Read(ref completed) / Math.Max(1, applicableLibraries.Length));
                if (library["downloads"]?["artifact"] is JsonObject artifact)
                    await FetchAsync(artifact, InstanceManager.Child(paths.LibrariesDirectory, VersionResolver.Text(artifact, "path")), cancellation);
                if (library["natives"]?["windows"]?.GetValue<string>() is string classifier)
                {
                    classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                    var native = library["downloads"]?["classifiers"]?[classifier] as JsonObject
                        ?? throw new InvalidDataException("Windows native 信息缺失。");
                    await FetchAsync(native, InstanceManager.Child(paths.LibrariesDirectory, VersionResolver.Text(native, "path")), cancellation);
                }
                int count = Interlocked.Increment(ref completed);
                if (count % 10 == 0 || count == applicableLibraries.Length) Report(OperationStep.Libraries, $"依赖库 {count}/{applicableLibraries.Length}", (double)count / Math.Max(1, applicableLibraries.Length));
            }, token);
        }
        Report(OperationStep.Libraries, "依赖库已完成", 1);
        Report(OperationStep.Assets, "正在下载资源文件", 0);
        if (json["assetIndex"] is JsonObject assetIndex)
        {
            string indexId = VersionResolver.Text(assetIndex, "id"); InstanceManager.ValidateName(indexId);
            string indexPath = InstanceManager.Child(paths.AssetsDirectory, "indexes/" + indexId + ".json");
            Report(OperationStep.Assets, "下载资源索引", 0);
            await using (var assetsLease = await instances.LockLibrariesAsync(token))
                await FetchAsync(assetIndex, indexPath, token);
            var index = await InstanceManager.ReadObjectAsync(indexPath, token);
            var objects = index["objects"] as JsonObject ?? throw new InvalidDataException("资源索引内容不合法。");
            var entries = objects.ToArray();
            int completed = 0;
            await fetcher.ForEachAsync(entries, 8, async (item, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                string hash = VersionResolver.Text(item.Value, "hash"); VersionResolver.ValidateSha1(hash);
                string relative = hash[..2] + "/" + hash;
                string objectPath = InstanceManager.Child(paths.AssetsDirectory, "objects/" + relative);
                await FetchAsync(new JsonObject { ["url"] = "https://resources.download.minecraft.net/" + relative,
                    ["sha1"] = hash, ["size"] = item.Value?["size"]?.DeepClone() }, objectPath, cancellation);
                if (index["virtual"]?.GetValue<bool>() == true)
                    await CopyAsync(objectPath, InstanceManager.Child(paths.AssetsDirectory, "virtual/" + indexId + "/" + item.Key), cancellation);
                if (index["map_to_resources"]?.GetValue<bool>() == true)
                    await CopyAsync(objectPath, InstanceManager.Child(paths.GameDirectory, "resources/" + item.Key), cancellation);
                int count = Interlocked.Increment(ref completed);
                if (count % 50 == 0 || count == entries.Length) Report(OperationStep.Assets, $"游戏资源 {count}/{entries.Length}", (double)count / Math.Max(1, entries.Length));
            }, token);
        }
        Report(OperationStep.Assets, "资源文件已完成", 1);
        var baseJson = (JsonObject)json.DeepClone();
        baseJson["id"] = VersionResolver.ParentId(metadata); baseJson.Remove("inheritsFrom"); baseJson.Remove("jar");
        var baseVersion = new VersionDocument(VersionResolver.ParentId(metadata), baseJson);
        var effective = versions.Compose(baseVersion, Array.Empty<VersionPatch>(), metadata.DirectoryName);
        Report(OperationStep.Saving, "正在保存版本信息", 0);
        var result = await instances.CommitLockedAsync(paths.GameDirectory, metadata, new()
        {
            [".launcher/base.json"] = InstanceManager.Serialize(baseJson),
            [metadata.DirectoryName + ".json"] = versions.SerializeStandalone(effective)
        }, token);
        Report(OperationStep.Saving, "版本信息已保存", 1);
        return result;
    }
    private async Task FetchAsync(JsonObject descriptor, string path, CancellationToken token, Action<long, long?>? progress = null)
    {
        string hash = VersionResolver.Text(descriptor, "sha1"); VersionResolver.ValidateSha1(hash);
        if (File.Exists(InstanceManager.Extended(path)))
        {
            await using var cached = new FileStream(InstanceManager.Extended(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(await SHA1.HashDataAsync(cached, token)).Equals(hash, StringComparison.OrdinalIgnoreCase)) return;
        }
        var uri = new Uri(VersionResolver.Text(descriptor, "url")); VersionResolver.RequireHttps(uri);
        await fetcher.DownloadAsync(uri, path, hash, descriptor["size"]?.GetValue<long>(), token, progress);
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
