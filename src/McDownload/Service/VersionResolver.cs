using McDownload.i18n;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownload.Model;

namespace McDownload.Service;

public sealed class VersionResolver : IVersionResolver
{
    private readonly InstanceManager _instances;
    private readonly HmclFetchTask _fetcher;
    private readonly Uri _manifestUri;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private MinecraftVersionManifest? _manifest;
    private DateTimeOffset _manifestTime;

    public VersionResolver(InstanceManager instances, HmclFetchTask fetcher, Uri? manifestUri = null)
    {
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _manifestUri = manifestUri ?? new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        RequireHttps(_manifestUri);
    }

    public async Task<MinecraftVersionManifest> GetManifestAsync(CancellationToken cancellationToken)
    {
        await _manifestGate.WaitAsync(cancellationToken);
        try
        {
            if (_manifest is not null && DateTimeOffset.UtcNow - _manifestTime < TimeSpan.FromMinutes(5)) return _manifest;
            _manifest = await _fetcher.ReadAsync(_manifestUri, 16 * 1024 * 1024, ParseManifest, cancellationToken);
            _manifestTime = DateTimeOffset.UtcNow;
            return _manifest;
        }
        finally { _manifestGate.Release(); }
    }

    private static MinecraftVersionManifest ParseManifest(byte[] bytes)
    {
        try
        {
            var root = ParseObject(bytes);
            var versions = new List<MinecraftVersionEntry>();
            foreach (JsonNode? entry in root["versions"] as JsonArray ?? throw Error("Manifest", McDownload.i18n.Text.Get("VersionResolver", "ManifestVersionsMissing")))
            {
                string id = Text(entry, "id");
                InstanceManager.ValidateName(id);
                var channel = Text(entry, "type") switch
                {
                    "release" => MinecraftVersionChannel.Release,
                    "snapshot" => MinecraftVersionChannel.Snapshot,
                    "old_beta" => MinecraftVersionChannel.OldBeta,
                    "old_alpha" => MinecraftVersionChannel.OldAlpha,
                    _ => throw Error("Manifest", McDownload.i18n.Text.Get("VersionResolver", "UnsupportedVersionType"))
                };
                var uri = new Uri(Text(entry, "url"), UriKind.Absolute);
                RequireHttps(uri);
                string sha1 = Text(entry, "sha1");
                ValidateSha1(sha1);
                versions.Add(new(id, channel, uri, sha1));
            }
            if (versions.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != versions.Count)
                throw Error("Manifest", McDownload.i18n.Text.Get("VersionResolver", "DuplicateVersionId"));
            string release = Text(root["latest"], "release"), snapshot = Text(root["latest"], "snapshot");
            if (!versions.Any(x => x.Id == release && x.Channel == MinecraftVersionChannel.Release) ||
                !versions.Any(x => x.Id == snapshot)) throw Error("Manifest", McDownload.i18n.Text.Get("VersionResolver", "LatestVersionMissing"));
            return new(release, snapshot, versions.AsReadOnly());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or InstanceValidationException)
        { throw new VersionResolutionException(McDownload.i18n.Text.Get("VersionResolver", "InvalidManifest"), ex); }
    }

    public async Task<VersionDocument> FetchVersionJsonAsync(MinecraftVersionEntry version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        var manifest = await GetManifestAsync(cancellationToken);
        var canonical = manifest.Versions.FirstOrDefault(x => x.Id == version.Id)
            ?? throw Error("VersionMissing", McDownload.i18n.Text.Get("VersionResolver", "VersionMissing"));
        if (canonical.JsonUri != version.JsonUri || !canonical.Sha1.Equals(version.Sha1, StringComparison.OrdinalIgnoreCase))
            throw Error("Manifest", McDownload.i18n.Text.Get("VersionResolver", "VersionAddressMismatch"));
        var json = await _fetcher.ReadAsync(canonical.JsonUri, 16 * 1024 * 1024, bytes =>
        {
            if (!Convert.ToHexString(SHA1.HashData(bytes)).Equals(canonical.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new DownloadIntegrityException(McDownload.i18n.Text.Get("VersionResolver", "VersionHashMismatch"));
            var document = ParseObject(bytes);
            if (Text(document, "id") != canonical.Id) throw Error("VersionId", McDownload.i18n.Text.Get("VersionResolver", "VersionIdMismatch"));
            return document;
        }, cancellationToken);
        return new(canonical.Id, json);
    }

    public async Task<VersionDocument> ReadInstanceVersionJsonAsync(InstanceId instanceId, CancellationToken cancellationToken)
    {
        string directory = await _instances.FindDirectoryAsync(instanceId, cancellationToken);
        await using var lease = await _instances.LockInstanceAsync(instanceId, cancellationToken);
        var metadata = await _instances.ReadLockedAsync(directory, cancellationToken);
        var json = await InstanceManager.ReadObjectAsync(_instances.Paths(metadata).VersionJsonFile, cancellationToken);
        if (Text(json, "id") != metadata.DirectoryName) throw Error("VersionId", McDownload.i18n.Text.Get("VersionResolver", "InstanceDirectoryMismatch"));
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
        if (options.InstanceId != metadata.Id) throw Error("Scope", McDownload.i18n.Text.Get("VersionResolver", "InstanceScopeMismatch"));
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<VersionDocument>();
        var paths = _instances.Paths(metadata);

        async Task<VersionDocument> Visit(VersionDocument current, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth >= options.MaxDepth) throw Error("DepthLimit", McDownload.i18n.Text.Get("VersionResolver", "DepthLimit"));
            InstanceManager.ValidateName(current.Id);
            if (Text(current.Json, "id") != current.Id) throw Error("VersionId", McDownload.i18n.Text.Get("VersionResolver", "DocumentIdMismatch"));
            if (!visiting.Add(current.Id)) throw Error("Cycle", McDownload.i18n.Text.Get("VersionResolver", "InheritanceCycle"));
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
                    if (Text(json, "id") != parentId) throw Error("VersionId", McDownload.i18n.Text.Get("VersionResolver", "ParentSnapshotIdMismatch"));
                    parent = new(parentId, json);
                }
                else
                {
                    if (!options.AllowOfficialDownload) throw Error("ParentMissing", McDownload.i18n.Text.Format("VersionResolver", "ParentMissing", parentId));
                    var manifest = await GetManifestAsync(token);
                    var entry = manifest.Versions.FirstOrDefault(x => x.Id == parentId)
                        ?? throw Error("ParentMissing", McDownload.i18n.Text.Format("VersionResolver", "ParentMissing", parentId));
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
            throw Error("Arguments", McDownload.i18n.Text.Get("VersionResolver", "ConflictingArguments"));
        return new(child.Id, json);
    }

    public VersionDocument Compose(VersionDocument resolvedBase, IReadOnlyList<VersionPatch> activePatches, string outputVersionId)
    {
        ArgumentNullException.ThrowIfNull(resolvedBase);
        ArgumentNullException.ThrowIfNull(activePatches);
        InstanceManager.ValidateName(outputVersionId);
        if (resolvedBase.Json["inheritsFrom"] is not null) throw Error("Base", McDownload.i18n.Text.Get("VersionResolver", "UnresolvedBase"));
        if (activePatches.Select(x => x.PatchId).Distinct().Count() != activePatches.Count ||
            activePatches.Count(x => x.Kind == VersionPatchKind.Loader) > 1)
            throw Error("Patch", McDownload.i18n.Text.Get("VersionResolver", "InvalidActivePatches"));
        VersionDocument result = new(resolvedBase.Id, (JsonObject)resolvedBase.Json.DeepClone());
        foreach (var patch in activePatches.OrderBy(x => x.Order).ThenBy(x => x.PatchId))
        {
            if (patch.PatchId == Guid.Empty || (patch.Kind == VersionPatchKind.Loader) != (patch.Loader is not null))
                throw Error("Patch", McDownload.i18n.Text.Get("VersionResolver", "InvalidPatchOwnership"));
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

    internal VersionDocument ToStandalone(VersionDocument effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var json = (JsonObject)effective.Json.DeepClone();
        json["id"] = effective.Id;
        json["jar"] = effective.Id;
        json.Remove("inheritsFrom");
        return new(effective.Id, json);
    }

    internal byte[] SerializeStandalone(VersionDocument effective) => InstanceManager.Serialize(ToStandalone(effective).Json);

    // 保存子层差量，继承解析不会重复追加原版参数。
    internal VersionDocument ToInherited(VersionDocument resolvedBase, VersionDocument effective, string parentId)
    {
        var json = Difference(resolvedBase.Json, effective.Json, "");
        json["id"] = effective.Id;
        json["jar"] = effective.Id;
        json["inheritsFrom"] = parentId;
        var check = Merge(resolvedBase, new(effective.Id, json));
        check.Json.Remove("inheritsFrom");
        if (!JsonNode.DeepEquals(check.Json, effective.Json)) throw Error("Patch", McDownload.i18n.Text.Get("VersionResolver", "UnrepresentableChild"));
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
            if (library is not JsonObject item) throw Error("Library", McDownload.i18n.Text.Get("VersionResolver", "LibraryObjectRequired"));
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
            throw Error("Library", McDownload.i18n.Text.Get("VersionResolver", "InvalidMavenCoordinate"));
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
                    throw Error("Arguments", McDownload.i18n.Text.Get("VersionResolver", "ChildArgumentPrefixMismatch"));
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
        node?[property]?.GetValue<string>() is { Length: > 0 } value ? value : throw Error("Json", McDownload.i18n.Text.Format("VersionResolver", "JsonFieldMissing", property));

    internal static string ParentId(InstanceMetadata metadata) => metadata.DirectoryName == metadata.MinecraftVersionId
        ? metadata.MinecraftVersionId + ".base." + metadata.Id.Value.ToString("N")
        : metadata.MinecraftVersionId;

    internal static JsonObject ParseObject(byte[] bytes)
    {
        try { return JsonNode.Parse(bytes) as JsonObject ?? throw Error("Json", McDownload.i18n.Text.Get("VersionResolver", "JsonObjectRequired")); }
        catch (JsonException error) { throw new VersionResolutionException(McDownload.i18n.Text.Get("VersionResolver", "InvalidJson"), error); }
    }

    internal static void ValidateSha1(string value)
    {
        if (value.Length != 40 || !value.All(Uri.IsHexDigit)) throw new DownloadIntegrityException(McDownload.i18n.Text.Get("VersionResolver", "InvalidSha1"));
    }

    internal static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException(McDownload.i18n.Text.Get("VersionResolver", "UnsafeDownloadAddress"));
    }

    private static VersionResolutionException Error(string code, string message)
    {
        var error = new VersionResolutionException(message);
        error.Data["Code"] = code;
        return error;
    }
}
