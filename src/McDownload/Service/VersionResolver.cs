using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownload.Model;

namespace McDownload.Service;

public sealed class VersionResolver : IVersionResolver
{
    private readonly InstanceManager _instances;
    private readonly HttpClient _http;
    private readonly Uri _manifestUri;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private MinecraftVersionManifest? _manifest;
    private DateTimeOffset _manifestTime;

    public VersionResolver(InstanceManager instances, HttpClient http, Uri? manifestUri = null)
    {
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _manifestUri = manifestUri ?? new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        RequireHttps(_manifestUri);
    }

    public async Task<MinecraftVersionManifest> GetManifestAsync(CancellationToken cancellationToken)
    {
        await _manifestGate.WaitAsync(cancellationToken);
        try
        {
            if (_manifest is not null && DateTimeOffset.UtcNow - _manifestTime < TimeSpan.FromMinutes(5)) return _manifest;
            var root = ParseObject(await HttpBytesAsync(_http, _manifestUri, 16 * 1024 * 1024, cancellationToken));
            var versions = new List<MinecraftVersionEntry>();
            foreach (JsonNode? entry in root["versions"] as JsonArray ?? throw Error("Manifest", "版本清单缺少 versions。"))
            {
                string id = Text(entry, "id");
                InstanceManager.ValidateName(id);
                var channel = Text(entry, "type") switch
                {
                    "release" => MinecraftVersionChannel.Release,
                    "snapshot" => MinecraftVersionChannel.Snapshot,
                    "old_beta" => MinecraftVersionChannel.OldBeta,
                    "old_alpha" => MinecraftVersionChannel.OldAlpha,
                    _ => throw Error("Manifest", "版本类型不受支持。")
                };
                var uri = new Uri(Text(entry, "url"), UriKind.Absolute);
                RequireHttps(uri);
                string sha1 = Text(entry, "sha1");
                ValidateSha1(sha1);
                versions.Add(new(id, channel, uri, sha1));
            }
            if (versions.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != versions.Count)
                throw Error("Manifest", "版本清单包含重复标识。");
            _manifest = new(Text(root["latest"], "release"), Text(root["latest"], "snapshot"), versions.AsReadOnly());
            _manifestTime = DateTimeOffset.UtcNow;
            return _manifest;
        }
        finally { _manifestGate.Release(); }
    }

    public async Task<VersionDocument> FetchVersionJsonAsync(MinecraftVersionEntry version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        var manifest = await GetManifestAsync(cancellationToken);
        var canonical = manifest.Versions.FirstOrDefault(x => x.Id == version.Id)
            ?? throw Error("VersionMissing", "官方版本清单中不存在该版本。");
        if (canonical.JsonUri != version.JsonUri || !canonical.Sha1.Equals(version.Sha1, StringComparison.OrdinalIgnoreCase))
            throw Error("Manifest", "版本地址或摘要与官方清单不一致。");
        byte[] bytes = await HttpBytesAsync(_http, canonical.JsonUri, 16 * 1024 * 1024, cancellationToken);
        if (!Convert.ToHexString(SHA1.HashData(bytes)).Equals(canonical.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new DownloadIntegrityException("版本 JSON 的 SHA-1 校验失败。");
        var json = ParseObject(bytes);
        if (Text(json, "id") != canonical.Id) throw Error("VersionId", "版本 JSON 标识不一致。");
        return new(canonical.Id, json);
    }

    public async Task<VersionDocument> ReadInstanceVersionJsonAsync(InstanceId instanceId, CancellationToken cancellationToken)
    {
        string directory = await _instances.FindDirectoryAsync(instanceId, cancellationToken);
        await using var lease = await _instances.LockInstanceAsync(instanceId, cancellationToken);
        var metadata = await _instances.ReadLockedAsync(directory, cancellationToken);
        var json = await InstanceManager.ReadObjectAsync(_instances.Paths(metadata).VersionJsonFile, cancellationToken);
        if (Text(json, "id") != metadata.DirectoryName) throw Error("VersionId", "实例版本标识与目录不一致。");
        return new(metadata.DirectoryName, json);
    }

    public async Task<ResolvedVersion> ResolveInheritanceAsync(VersionDocument leaf, InheritanceResolutionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        ArgumentNullException.ThrowIfNull(options);
        string directory = await _instances.FindDirectoryAsync(options.InstanceId, cancellationToken);
        await using var lease = await _instances.LockInstanceAsync(options.InstanceId, cancellationToken);
        var metadata = await _instances.ReadLockedAsync(directory, cancellationToken);
        return await ResolveLockedAsync(leaf, metadata, options, cancellationToken);
    }

    internal async Task<ResolvedVersion> ResolveLockedAsync(VersionDocument leaf, InstanceMetadata metadata,
        InheritanceResolutionOptions options, CancellationToken token)
    {
        if (options.MaxDepth is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.InstanceId != metadata.Id) throw Error("Scope", "继承解析实例不一致。");
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<VersionDocument>();
        var paths = _instances.Paths(metadata);

        async Task<VersionDocument> Visit(VersionDocument current, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth >= options.MaxDepth) throw Error("DepthLimit", "版本继承深度超限。");
            InstanceManager.ValidateName(current.Id);
            if (Text(current.Json, "id") != current.Id) throw Error("VersionId", "文档标识与 JSON 标识不一致。");
            if (!visiting.Add(current.Id)) throw Error("Cycle", "版本继承存在循环。");
            VersionDocument effective = new(current.Id, (JsonObject)current.Json.DeepClone());
            if (current.Json["inheritsFrom"] is JsonNode parentNode)
            {
                string parentId = parentNode.GetValue<string>();
                InstanceManager.ValidateName(parentId);
                VersionDocument parent;
                string local = parentId == ParentId(metadata)
                    ? paths.BaseVersionJsonFile
                    : InstanceManager.Child(paths.GameDirectory, $".launcher/parents/{parentId}.json");
                if (File.Exists(InstanceManager.Extended(local)))
                {
                    var json = await InstanceManager.ReadObjectAsync(local, token);
                    if (Text(json, "id") != parentId) throw Error("VersionId", "父版本快照标识不一致。");
                    parent = new(parentId, json);
                }
                else
                {
                    if (!options.AllowOfficialDownload) throw Error("ParentMissing", $"父版本不存在：{parentId}");
                    var manifest = await GetManifestAsync(token);
                    var entry = manifest.Versions.FirstOrDefault(x => x.Id == parentId)
                        ?? throw Error("ParentMissing", $"父版本不存在：{parentId}");
                    parent = await FetchVersionJsonAsync(entry, token);
                }
                effective = Merge(await Visit(parent, depth + 1), current);
            }
            effective.Json.Remove("inheritsFrom");
            chain.Add(new(current.Id, (JsonObject)current.Json.DeepClone()));
            visiting.Remove(current.Id);
            return effective;
        }

        return new(await Visit(leaf, 0), chain.AsReadOnly());
    }

    public VersionDocument Merge(VersionDocument parent, VersionDocument child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        var json = MergeObject(parent.Json, child.Json, "");
        json["id"] = child.Id;
        if (json["arguments"] is JsonObject && json["minecraftArguments"] is not null)
            throw Error("Arguments", "版本同时包含结构化参数与 minecraftArguments。");
        return new(child.Id, json);
    }

    public VersionDocument Compose(VersionDocument resolvedBase, IReadOnlyList<VersionPatch> activePatches, string outputVersionId)
    {
        ArgumentNullException.ThrowIfNull(resolvedBase);
        ArgumentNullException.ThrowIfNull(activePatches);
        InstanceManager.ValidateName(outputVersionId);
        if (resolvedBase.Json["inheritsFrom"] is not null) throw Error("Base", "基础版本尚未完成继承解析。");
        if (activePatches.Select(x => x.PatchId).Distinct().Count() != activePatches.Count ||
            activePatches.Count(x => x.Kind == VersionPatchKind.Loader) > 1)
            throw Error("Patch", "生效 patch 集合不合法。");
        VersionDocument result = new(resolvedBase.Id, (JsonObject)resolvedBase.Json.DeepClone());
        foreach (var patch in activePatches.OrderBy(x => x.Order).ThenBy(x => x.PatchId))
        {
            if (patch.PatchId == Guid.Empty || (patch.Kind == VersionPatchKind.Loader) != (patch.Loader is not null))
                throw Error("Patch", "patch 归属不合法。");
            var json = (JsonObject)patch.Json.DeepClone();
            json.Remove("inheritsFrom");
            json.Remove("jar");
            result = Merge(result, new(result.Id, json));
        }
        result.Json["id"] = outputVersionId;
        result.Json["jar"] = outputVersionId;
        result.Json.Remove("inheritsFrom");
        return new(outputVersionId, result.Json);
    }

    // 保存子层差量，继承解析不会重复追加原版参数。
    internal VersionDocument ToInherited(VersionDocument resolvedBase, VersionDocument effective, string parentId)
    {
        var json = Difference(resolvedBase.Json, effective.Json, "");
        json["id"] = effective.Id;
        json["jar"] = effective.Id;
        json["inheritsFrom"] = parentId;
        var check = Merge(resolvedBase, new(effective.Id, json));
        check.Json.Remove("inheritsFrom");
        if (!JsonNode.DeepEquals(check.Json, effective.Json)) throw Error("Patch", "版本内容不能表示为继承子层。");
        return new(effective.Id, json);
    }

    private static JsonObject MergeObject(JsonObject parent, JsonObject child, string path)
    {
        var result = (JsonObject)parent.DeepClone();
        foreach (var item in child)
        {
            string next = path.Length == 0 ? item.Key : path + "." + item.Key;
            if (next == "libraries" && item.Value is JsonArray libraries)
                result[item.Key] = MergeLibraries(result[item.Key] as JsonArray, libraries);
            else if (next is "arguments.game" or "arguments.jvm" && item.Value is JsonArray arguments)
            {
                var merged = result[item.Key] is JsonArray existing ? (JsonArray)existing.DeepClone() : new JsonArray();
                foreach (var argument in arguments) merged.Add(argument?.DeepClone());
                result[item.Key] = merged;
            }
            else if (result[item.Key] is JsonObject a && item.Value is JsonObject b)
                result[item.Key] = MergeObject(a, b, next);
            else result[item.Key] = item.Value?.DeepClone();
        }
        return result;
    }

    private static JsonArray MergeLibraries(JsonArray? parent, JsonArray child)
    {
        var result = new JsonArray();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonNode? library in (parent ?? new JsonArray()).Concat(child))
        {
            if (library is not JsonObject item) throw Error("Library", "依赖库条目必须是对象。");
            string key = LibraryKey(item);
            if (indexes.TryGetValue(key, out int index)) result[index] = item.DeepClone();
            else { indexes.Add(key, result.Count); result.Add(item.DeepClone()); }
        }
        return result;
    }

    internal static string LibraryKey(JsonObject library)
    {
        string name = Text(library, "name");
        string[] extension = name.Split('@');
        string[] parts = extension[0].Split(':');
        if (extension.Length > 2 || parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace))
            throw Error("Library", "Maven 坐标不合法。");
        return string.Join(':', parts[0], parts[1], parts.Length == 4 ? parts[3] : "", extension.Length == 2 ? extension[1] : "jar")
            + "|" + Canonical(library["rules"]) + "|" + Canonical(library["natives"]);
    }

    private static string Canonical(JsonNode? node)
    {
        if (node is JsonObject obj)
            return "{" + string.Join(',', obj.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value))) + "}";
        if (node is JsonArray array) return "[" + string.Join(',', array.Select(Canonical)) + "]";
        return node?.ToJsonString() ?? "null";
    }

    private static JsonObject Difference(JsonObject parent, JsonObject effective, string path)
    {
        var result = new JsonObject();
        foreach (var item in effective)
        {
            if (item.Key == "inheritsFrom" && path.Length == 0) continue;
            parent.TryGetPropertyValue(item.Key, out var previous);
            if (JsonNode.DeepEquals(previous, item.Value)) continue;
            string next = path.Length == 0 ? item.Key : path + "." + item.Key;
            if (next == "libraries" && item.Value is JsonArray libraries)
            {
                var originals = (previous as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .GroupBy(LibraryKey).ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
                var delta = new JsonArray();
                foreach (var library in libraries.OfType<JsonObject>())
                    if (!originals.TryGetValue(LibraryKey(library), out var original) || !JsonNode.DeepEquals(original, library))
                        delta.Add(library.DeepClone());
                if (delta.Count > 0) result[item.Key] = delta;
            }
            else if (next is "arguments.game" or "arguments.jvm" && item.Value is JsonArray arguments)
            {
                var prefix = previous as JsonArray ?? new JsonArray();
                if (arguments.Count < prefix.Count || prefix.Where((value, index) => !JsonNode.DeepEquals(value, arguments[index])).Any())
                    throw Error("Arguments", "子层参数不保留父层参数前缀。");
                var delta = new JsonArray();
                foreach (var argument in arguments.Skip(prefix.Count)) delta.Add(argument?.DeepClone());
                if (delta.Count > 0) result[item.Key] = delta;
            }
            else if (previous is JsonObject a && item.Value is JsonObject b)
            {
                var delta = Difference(a, b, next);
                if (delta.Count > 0) result[item.Key] = delta;
            }
            else result[item.Key] = item.Value?.DeepClone();
        }
        return result;
    }

    internal static string Text(JsonNode? node, string property) =>
        node?[property]?.GetValue<string>() is { Length: > 0 } value ? value : throw Error("Json", $"JSON 字段缺失：{property}");

    internal static string ParentId(InstanceMetadata metadata) => metadata.DirectoryName == metadata.MinecraftVersionId
        ? metadata.MinecraftVersionId + ".base." + metadata.Id.Value.ToString("N")
        : metadata.MinecraftVersionId;

    internal static JsonObject ParseObject(byte[] bytes)
    {
        try { return JsonNode.Parse(bytes) as JsonObject ?? throw Error("Json", "JSON 根节点必须是对象。"); }
        catch (JsonException error) { throw new VersionResolutionException("JSON 格式不合法。", error); }
    }

    internal static void ValidateSha1(string value)
    {
        if (value.Length != 40 || !value.All(Uri.IsHexDigit)) throw new DownloadIntegrityException("SHA-1 摘要格式不合法。");
    }

    internal static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("下载地址必须是无凭证的 HTTPS 地址。");
    }

    internal static async Task<byte[]> HttpBytesAsync(HttpClient http, Uri uri, int maximum, CancellationToken token)
    {
        RequireHttps(uri);
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is Uri finalUri) RequireHttps(finalUri);
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("响应超过大小上限。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("响应超过大小上限。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static VersionResolutionException Error(string code, string message)
    {
        var error = new VersionResolutionException(message);
        error.Data["Code"] = code;
        return error;
    }
}
