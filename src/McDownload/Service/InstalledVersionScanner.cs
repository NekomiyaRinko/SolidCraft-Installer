using McDownload.i18n;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McDownload.Model;

namespace McDownload.Service;

public sealed class InstalledVersionScanner(InstanceManager instances)
{
    public async Task<IReadOnlyList<InstalledVersionInfo>> ScanAsync(CancellationToken token)
    {
        string versions = InstanceManager.Child(instances.MinecraftRoot, "versions");
        var result = new List<InstalledVersionInfo>();
        foreach (string entry in Directory.EnumerateDirectories(InstanceManager.Extended(versions)))
        {
            token.ThrowIfCancellationRequested();
            string directory = InstanceManager.Normal(entry);
            string name = Path.GetFileName(directory);
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                string metadataFile = InstanceManager.Child(directory, "instance.json");
                if (File.Exists(InstanceManager.Extended(metadataFile)))
                {
                    var raw = JsonSerializer.Deserialize<InstanceMetadata>(await InstanceManager.ReadBytesAsync(metadataFile,
                        4 * 1024 * 1024, token), InstanceManager.JsonOptions) ?? throw new JsonException(Text.Get("InstalledVersionScanner", "MetadataEmpty"));
                    if (raw.Id.Value == Guid.Empty) throw new InstanceValidationException(Text.Get("InstalledVersionScanner", "InstanceIdEmpty"));
                    await using var lease = await instances.LockInstanceAsync(raw.Id, token);
                    var managed = await instances.ReadLockedAsync(directory, token);
                    result.Add(new(managed.Editable.DisplayName, directory, managed.MinecraftVersionId,
                        Describe(managed.InstalledLoader), managed, null));
                    continue;
                }
                string jsonPath = InstanceManager.Child(directory, name + ".json");
                if (!File.Exists(InstanceManager.Extended(jsonPath))) continue;
                var documents = new List<JsonObject>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string current = name;
                string? warning = null;
                while (true)
                {
                    if (visited.Count >= 64 || !visited.Add(current))
                    { warning = Text.Get("InstalledVersionScanner", "InheritanceCycleOrDepth"); break; }
                    string parentFile = InstanceManager.Child(versions, current + "/" + current + ".json");
                    if (!File.Exists(InstanceManager.Extended(parentFile)))
                    { warning = Text.Format("InstalledVersionScanner", "ParentMissing", current); break; }
                    var json = await InstanceManager.ReadObjectAsync(parentFile, token);
                    documents.Add(json);
                    if (json["inheritsFrom"]?.GetValue<string>() is not { Length: > 0 } parent) break;
                    InstanceManager.ValidateName(parent);
                    current = parent;
                }
                var detected = Detect(documents);
                result.Add(new(name, directory, detected.Minecraft ?? current, Describe(detected.Loader), null, warning));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or InstanceValidationException or InstanceConflictException or InvalidOperationException or ArgumentException)
            {
                result.Add(new(name, directory, Text.Get("InstalledVersionScanner", "Unrecognized"), Text.Get("InstalledVersionScanner", "LoaderUnavailable"), null, Text.Format("InstalledVersionScanner", "ReadFailed", ex.Message)));
            }
        }
        var duplicates = result.Where(x => x.Metadata is not null).GroupBy(x => x.Metadata!.Id).Where(x => x.Count() > 1)
            .Select(x => x.Key).ToHashSet();
        return result.Select(x => x.Metadata is not null && duplicates.Contains(x.Metadata.Id)
            ? x with { Metadata = null, Warning = Text.Get("InstalledVersionScanner", "DuplicateInstanceId") } : x)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Describe(LoaderSelection? loader) => loader is null ? Text.Get("Common", "NoLoaderInstalled") : Text.Format("Common", "InstalledLoader", DisplayNames.Loader(loader.Kind), loader.Version);

    private static (string? Minecraft, LoaderSelection? Loader) Detect(IReadOnlyList<JsonObject> documents)
    {
        string? minecraft = null;
        LoaderSelection? loader = null;
        foreach (var json in documents)
        {
            minecraft ??= json["clientVersion"]?.GetValue<string>();
            var arguments = ReadGameArguments(json);
            minecraft ??= ArgumentValue(arguments, "--fml.mcVersion");
            string? neoVersion = ArgumentValue(arguments, "--fml.neoForgeVersion");
            string? forgeVersion = ArgumentValue(arguments, "--fml.forgeVersion");
            if (loader is null && neoVersion is { Length: > 0 }) loader = new(LoaderKind.NeoForge, neoVersion);
            if (loader is null && forgeVersion is { Length: > 0 })
                loader = new(ArgumentValue(arguments, "--fml.forgeGroup") == "net.neoforged" ? LoaderKind.NeoForge : LoaderKind.Forge, forgeVersion);
            if (json["patches"] is JsonArray patches)
                foreach (var patch in patches)
                {
                    if (patch?["id"]?.GetValue<string>() == "game") minecraft ??= patch?["version"]?.GetValue<string>();
                    if (loader is null && patch?["version"]?.GetValue<string>() is { Length: > 0 } patchVersion)
                    {
                        LoaderKind? kind = patch?["id"]?.GetValue<string>() switch
                        {
                            "forge" => LoaderKind.Forge, "neoforge" => LoaderKind.NeoForge,
                            "fabric" => LoaderKind.Fabric, _ => null
                        };
                        if (kind is not null) loader = new(kind.Value, patchVersion);
                    }
                }
            var libraries = (json["libraries"] as JsonArray ?? new()).OfType<JsonObject>();
            foreach (var library in libraries)
            {
                string[] coordinate = (library["name"]?.GetValue<string>() ?? "").Split(':');
                if (coordinate.Length < 3) continue;
                string version = coordinate[2];
                if (coordinate[0] == "net.fabricmc" && coordinate[1] == "intermediary") minecraft ??= version;
                if (loader is not null) continue;
                if (coordinate[0] == "net.fabricmc" && coordinate[1] == "fabric-loader") loader = new(LoaderKind.Fabric, version);
                else if (coordinate[0] is "net.minecraftforge" or "net.neoforged" && coordinate[1] is "forge" or "neoforge")
                {
                    var kind = coordinate[0] == "net.minecraftforge" ? LoaderKind.Forge : LoaderKind.NeoForge;
                    if (coordinate[1] == "forge" && version.IndexOf('-') is > 0 and var split)
                    { minecraft ??= version[..split]; version = version[(split + 1)..]; }
                    loader = new(kind, version);
                }
            }
            string? parent = json["inheritsFrom"]?.GetValue<string>();
            if (parent is not null && Regex.IsMatch(parent, @"\A(?:1\.\d+(?:\.\d+)?(?:-(?:pre|rc)\d+)?|\d{2}w\d{2}[a-z]|\d{2}\.\d+(?:\.\d+)?(?:-(?:snapshot|pre|rc)-\d+)?)\z")) minecraft ??= parent;
            if (parent is null && json["id"]?.GetValue<string>() is { } id) minecraft ??= id;
        }
        return (minecraft, loader);
    }

    private static IReadOnlyList<string> ReadGameArguments(JsonObject json)
    {
        var result = new List<string>();
        if (json["arguments"]?["game"] is JsonArray game)
            foreach (var argument in game)
            {
                if (argument is JsonValue value && value.TryGetValue<string>(out var text)) result.Add(text);
                else if (argument is JsonObject conditional)
                {
                    if (conditional["value"] is JsonValue single && single.TryGetValue<string>(out var item)) result.Add(item);
                    else if (conditional["value"] is JsonArray values)
                        foreach (var entry in values.OfType<JsonValue>())
                            if (entry.TryGetValue<string>(out var part)) result.Add(part);
                }
            }
        if (json["minecraftArguments"]?.GetValue<string>() is { } legacy)
            foreach (Match match in Regex.Matches(legacy, "\"([^\"]*)\"|'([^']*)'|([^\\s]+)"))
                result.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
        return result;
    }

    private static string? ArgumentValue(IReadOnlyList<string> arguments, string key)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].StartsWith(key + "=", StringComparison.Ordinal)) return arguments[i][(key.Length + 1)..];
            if (arguments[i] == key && i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal)) return arguments[i + 1];
        }
        return null;
    }
}
