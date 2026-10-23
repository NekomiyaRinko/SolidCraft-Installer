using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownload.Model;

namespace McDownload.Service;

public sealed class ModpackArchiveReader
{
    public async Task<ModpackDefinition> ReadAsync(string archivePath, CancellationToken token)
    {
        await using var input = Open(archivePath);
        return await ReadAsync(input, Path.GetFileName(archivePath), token);
    }

    internal static FileStream Open(string path)
    {
        path = InstanceManager.Normal(path);
        InstanceManager.CheckNoReparse(path);
        var input = new FileStream(InstanceManager.Extended(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > ModpackSafety.MaximumFileBytes) { input.Dispose(); throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "ArchiveTooLarge")); }
        return input;
    }

    internal async Task<ModpackDefinition> ReadAsync(FileStream input, string sourceName, CancellationToken token)
    {
        input.Position = 0;
        string digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, true);
        ValidateEntries(archive, token);
        var modrinth = archive.GetEntry("modrinth.index.json");
        var curseforge = archive.GetEntry("manifest.json");
        if ((modrinth is null) == (curseforge is null)) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "UnknownFormat"));
        var manifest = await ReadJsonAsync((modrinth ?? curseforge)!, token);
        var pack = modrinth is not null ? ReadModrinth(manifest) : ReadCurseForge(manifest);
        var files = pack.Files.ToList();
        string overrides = modrinth is not null ? "overrides" : Text(manifest, "overrides");
        ModpackSafety.RelativePath(overrides);
        foreach (string layer in modrinth is not null ? new[] { overrides, "client-overrides" } : new[] { overrides })
        {
            foreach (var entry in archive.Entries.Where(x => x.FullName.StartsWith(layer + "/", StringComparison.Ordinal) && x.Name.Length > 0))
            {
                token.ThrowIfCancellationRequested();
                string path = entry.FullName[(layer.Length + 1)..];
                ModpackSafety.ValidateTarget(path);
                await using var content = entry.Open();
                string sha256 = await ModpackSafety.CopyVerifiedAsync(content, Stream.Null, entry.Length, new(), token);
                files.Add(new("archive:" + entry.FullName, path, entry.Length, new(Sha256: sha256),
                    System.Array.Empty<Uri>(), ArchiveEntry: entry.FullName));
            }
        }
        ValidateTargets(files);
        return pack with { Files = files, ArchiveSha256 = digest, SourceFileName = sourceName };
    }

    private static void ValidateEntries(ZipArchive archive, CancellationToken token)
    {
        if (archive.Entries.Count > ModpackSafety.MaximumEntries) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "TooManyEntries"));
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            bool directory = entry.FullName.EndsWith('/');
            string path = directory ? entry.FullName[..^1] : entry.FullName;
            ModpackSafety.RelativePath(path);
            int type = (entry.ExternalAttributes >> 16) & 0xf000;
            if (type != 0 && type != 0x8000 && type != 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "SpecialFileUnsupported"));
            if (!entries.TryAdd(path, directory)) throw new InvalidDataException(McDownload.i18n.Text.Format("ModpackArchiveReader", "DuplicatePath", path));
            if (entry.Length > ModpackSafety.MaximumFileBytes || entry.Length < 0 || (total += entry.Length) > ModpackSafety.MaximumPackBytes)
                throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "ExpandedArchiveTooLarge"));
        }
        CheckAncestors(entries);
    }

    private static void CheckAncestors(IReadOnlyDictionary<string, bool> entries)
    {
        foreach (string path in entries.Keys)
            for (int split = path.LastIndexOf('/'); split > 0; split = path.LastIndexOf('/', split - 1))
                if (entries.TryGetValue(path[..split], out bool directory) && !directory)
                    throw new InvalidDataException(McDownload.i18n.Text.Format("ModpackArchiveReader", "FileDirectoryConflict", path));
    }

    internal static void ValidateTargets(IEnumerable<ModpackFile> files)
    {
        var targets = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files.Where(x => x.Path is not null))
        {
            ModpackSafety.ValidateFile(file);
            total += file.Size!.Value;
            if (total > ModpackSafety.MaximumPackBytes) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "PackTooLarge"));
            targets[file.Path!] = false;
        }
        CheckAncestors(targets);
    }

    private static async Task<JsonObject> ReadJsonAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "ManifestTooLarge"));
        await using var stream = entry.Open();
        using var memory = new MemoryStream();
        await ModpackSafety.CopyVerifiedAsync(stream, memory, entry.Length, new(), token);
        ReadOnlyMemory<byte> bytes = memory.ToArray();
        if (bytes.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        using var document = JsonDocument.Parse(bytes);
        CheckJsonKeys(document.RootElement);
        return JsonNode.Parse(bytes.Span) as JsonObject ?? throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "ManifestObjectRequired"));
    }

    private static void CheckJsonKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "DuplicateManifestField"));
                CheckJsonKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckJsonKeys(item);
    }

    private static ModpackDefinition ReadModrinth(JsonObject json)
    {
        if (json["formatVersion"]?.GetValue<int>() != 1 || Text(json, "game") != "minecraft")
            throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "UnsupportedModrinthFormat"));
        var dependencies = json["dependencies"] as JsonObject ?? throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "DependenciesMissing"));
        string minecraft = Text(dependencies, "minecraft"); InstanceManager.ValidateName(minecraft);
        var warnings = new List<string>();
        LoaderSelection? loader = null;
        foreach (var dependency in dependencies.Where(x => x.Key != "minecraft"))
        {
            var parsed = Loader(dependency.Key, dependency.Value?.GetValue<string>() ?? "");
            if (loader is not null) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "MultipleLoaders"));
            loader = parsed;
        }
        var files = new List<ModpackFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Array(json, "files"))
        {
            string path = Text(item, "path"); ModpackSafety.ValidateTarget(path);
            if (!seen.Add(path)) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "DuplicateFilePath"));
            string client = item?["env"]?["client"]?.GetValue<string>() ?? "required";
            if (client is not ("required" or "optional" or "unsupported")) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "UnknownClientRequirement"));
            string? server = item?["env"]?["server"]?.GetValue<string>();
            if (server is not (null or "required" or "optional" or "unsupported")) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "UnknownServerRequirement"));
            var hashes = new ModpackHashes(Text(item?["hashes"], "sha1"), Text(item?["hashes"], "sha512"));
            var downloads = Array(item, "downloads").Select(x => new Uri(x!.GetValue<string>(), UriKind.Absolute)).ToArray();
            if (downloads.Length == 0) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "DownloadAddressMissing"));
            var file = new ModpackFile("file:" + path, path, item?["fileSize"]?.GetValue<long>(), hashes, downloads, client == "optional");
            ModpackSafety.ValidateFile(file);
            if (client != "unsupported") files.Add(file);
        }
        return new(ModpackFormat.Modrinth, Text(json, "name"), Text(json, "versionId"), json["summary"]?.GetValue<string>(),
            minecraft, loader, files, warnings, "", "");
    }

    private static ModpackDefinition ReadCurseForge(JsonObject json)
    {
        if (Text(json, "manifestType") != "minecraftModpack" || json["manifestVersion"]?.GetValue<int>() != 1)
            throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "UnsupportedCurseForgeFormat"));
        string minecraft = Text(json["minecraft"], "version"); InstanceManager.ValidateName(minecraft);
        var loaders = Array(json["minecraft"], "modLoaders").ToArray();
        var primary = loaders.Where(x => x?["primary"]?.GetValue<bool>() == true).ToArray();
        if (primary.Length > 1 || loaders.Length > 1 && primary.Length != 1)
            throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "PrimaryLoaderUnknown"));
        LoaderSelection? loader = null;
        if ((primary.FirstOrDefault() ?? loaders.FirstOrDefault()) is { } selected)
        {
            string id = Text(selected, "id"); int split = id.IndexOf('-');
            if (split <= 0) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "InvalidLoaderId"));
            loader = Loader(id[..split], id[(split + 1)..]);
        }
        var files = new List<ModpackFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Array(json, "files"))
        {
            long project = item?["projectID"]?.GetValue<long>() ?? 0, fileId = item?["fileID"]?.GetValue<long>() ?? 0;
            if (project <= 0 || fileId <= 0) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "InvalidCurseForgeFileId"));
            string key = $"curseforge:{project}:{fileId}";
            if (!seen.Add(key)) throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "DuplicateCurseForgeFile"));
            files.Add(new(key, null, null, new(), System.Array.Empty<Uri>(),
                item?["required"]?.GetValue<bool>() == false, new(project, fileId)));
        }
        return new(ModpackFormat.CurseForge, Text(json, "name"), Text(json, "version"), json["author"]?.GetValue<string>(),
            minecraft, loader, files, System.Array.Empty<string>(), "", "");
    }

    private static LoaderSelection Loader(string name, string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 180 || version.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '+' or '-')))
            throw new InvalidDataException(McDownload.i18n.Text.Get("ModpackArchiveReader", "InvalidLoaderVersion"));
        return new(name switch
        {
            "forge" => LoaderKind.Forge, "fabric-loader" or "fabric" => LoaderKind.Fabric,
            "neoforge" => LoaderKind.NeoForge,
            _ => throw new InvalidDataException(McDownload.i18n.Text.Format("ModpackArchiveReader", "UnsupportedDependency", name))
        }, version);
    }

    internal static string Text(JsonNode? node, string key) => node?[key]?.GetValue<string>() is { Length: > 0 } text
        ? text : throw new InvalidDataException(McDownload.i18n.Text.Format("ModpackArchiveReader", "FieldMissing", key));
    private static JsonArray Array(JsonNode? node, string key) => node?[key] as JsonArray
        ?? throw new InvalidDataException(McDownload.i18n.Text.Format("ModpackArchiveReader", "ArrayFieldMissing", key));
}
