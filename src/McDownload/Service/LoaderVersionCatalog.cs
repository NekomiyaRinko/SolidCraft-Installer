using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using McDownload.Model;

namespace McDownload.Service;

public sealed class LoaderVersionCatalog(HmclFetchTask fetcher)
{
    private readonly Dictionary<(string Minecraft, LoaderKind Kind), (IReadOnlyList<LoaderVersionEntry> Versions, DateTimeOffset Time)> _cache = new();

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetVersionsAsync(string minecraft, LoaderKind kind,
        CancellationToken token, bool forceRefresh = false)
    {
        InstanceManager.ValidateName(minecraft);
        var key = (minecraft, kind);
        token.ThrowIfCancellationRequested();
        if (!forceRefresh && _cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(5)) return cached.Versions;
        string mc = Uri.EscapeDataString(minecraft);
        IReadOnlyList<LoaderVersionEntry> result = kind switch
        {
            LoaderKind.Fabric => await fetcher.ReadAsync(new Uri($"https://meta.fabricmc.net/v2/versions/loader/{mc}"),
                16 * 1024 * 1024, ParseFabric, token),
            LoaderKind.Forge => await ReadForgeAsync(minecraft, token),
            LoaderKind.NeoForge => await ReadNeoForgeAsync(minecraft, token),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        result = result.DistinctBy(x => x.Version).OrderByDescending(x => x.Version, VersionComparer.Instance).ToArray();
        token.ThrowIfCancellationRequested();
        _cache[key] = (result, DateTimeOffset.UtcNow);
        return result;
    }

    private async Task<IReadOnlyList<LoaderVersionEntry>> ReadForgeAsync(string minecraft, CancellationToken token)
    {
        // BMCLAPI 的列表结构与 Forge 官方不同，分别解析并保留官方版本号中的分支后缀。
        var uri = new Uri("https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json");
        return await fetcher.ReadAsync(uri, 16 * 1024 * 1024, bytes =>
        {
            JsonNode? node = JsonNode.Parse(bytes);
            if (node is JsonArray mirror)
                return (IReadOnlyList<LoaderVersionEntry>)mirror.OfType<JsonObject>()
                    .Where(x => x["mcversion"]?.GetValue<string>() == minecraft &&
                        (x["files"] as JsonArray)?.Any(f => f?["category"]?.GetValue<string>() == "installer" && f?["format"]?.GetValue<string>() == "jar") == true)
                    .Select(x => Entry(VersionResolver.Text(x, "version") +
                        (x["branch"]?.GetValue<string>() is { Length: > 0 } branch ? "-" + branch : ""))).ToArray();
            if (node is not JsonObject official) throw new InvalidDataException("Forge 版本列表不合法。");
            return (official[minecraft] as JsonArray ?? new JsonArray()).Select(x =>
            {
                string version = x!.GetValue<string>();
                return Entry(version.StartsWith(minecraft + "-", StringComparison.Ordinal) ? version[(minecraft.Length + 1)..] : version);
            }).ToArray();
        }, token, new Uri($"https://bmclapi2.bangbang93.com/forge/minecraft/{Uri.EscapeDataString(minecraft)}"));
    }

    private async Task<IReadOnlyList<LoaderVersionEntry>> ReadNeoForgeAsync(string minecraft, CancellationToken token)
    {
        bool legacy = minecraft == "1.20.1";
        string artifact = legacy ? "forge" : "neoforge";
        return await fetcher.ReadAsync(new Uri($"https://maven.neoforged.net/releases/net/neoforged/{artifact}/maven-metadata.xml"),
            8 * 1024 * 1024, bytes =>
            {
                using var input = new MemoryStream(bytes);
                using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var versions = XDocument.Load(reader).Descendants("version").Select(x => x.Value);
                return (IReadOnlyList<LoaderVersionEntry>)versions.Where(v => SupportsNeoForge(minecraft, v))
                    .Select(v => Entry(legacy ? v[(minecraft.Length + 1)..] : v)).ToArray();
            }, token);
    }

    public static bool SupportsNeoForge(string minecraft, string version)
    {
        if (minecraft == "1.20.1") return version.StartsWith("1.20.1-", StringComparison.Ordinal);
        var mc = minecraft.Split('.');
        var neo = version.Split('.', '-', '+');
        if (mc.Length >= 2 && mc[0] == "1" && mc.Skip(1).All(x => int.TryParse(x, out _)))
            return neo.Length >= 3 && neo[0] == mc[1] && neo[1] == (mc.Length == 3 ? mc[2] : "0");
        // 26.1 起使用 <年>.<发布序号>.<补丁>.<构建>；快照通过 +snapshot/pre/rc 后缀区分。
        if (mc.Length >= 2 && int.TryParse(mc[0], out int year) && year >= 26)
        {
            string[] parts = minecraft.Split('-', 2);
            string[] numbers = parts[0].Split('.');
            string prefix = numbers[0] + "." + numbers[1] + "." + (numbers.Length == 3 ? numbers[2] : "0") + ".";
            if (!version.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string? snapshot = version.Contains('+') ? version.Split('+', 2)[1] : null;
            return parts.Length == 2 ? snapshot == parts[1] : snapshot is null;
        }
        return false;
    }

    private static IReadOnlyList<LoaderVersionEntry> ParseFabric(byte[] bytes)
    {
        if (JsonNode.Parse(bytes) is not JsonArray array) throw new InvalidDataException("Fabric 版本列表不合法。");
        return array.Select(x => new LoaderVersionEntry(VersionResolver.Text(x?["loader"], "version"),
            x?["loader"]?["stable"]?.GetValue<bool>() ?? false)).ToArray();
    }

    private static LoaderVersionEntry Entry(string version) => new(version,
        !Regex.IsMatch(version, @"(?:alpha|beta|snapshot|(?:^|[-.+])(?:pre|rc)(?:[-.\d]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private sealed class VersionComparer : IComparer<string>
    {
        public static VersionComparer Instance { get; } = new();
        public int Compare(string? x, string? y)
        {
            string[] a = Regex.Split(x ?? "", @"(\d+)"), b = Regex.Split(y ?? "", @"(\d+)");
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                int c = long.TryParse(a[i], out long av) && long.TryParse(b[i], out long bv)
                    ? av.CompareTo(bv) : StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
