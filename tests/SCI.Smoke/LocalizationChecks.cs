using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using McDownload.i18n;
using McDownload.Model;
using McDownload.ViewModel;
using McDownload.Service;
using System.Collections.Concurrent;
using System.Reflection;

internal static class LocalizationChecks
{
    private sealed class FixtureResources : ResourceManager
    {
        public override string? GetString(string name, CultureInfo? culture) => name switch
        {
            "Forge" => "fixture-forge-label",
            "Fabric" => "fixture-fabric-label",
            "NeoForge" => "fixture-neoforge-label",
            "Modrinth" => "fixture-modrinth-label",
            "CurseForge" => "fixture-curseforge-label",
            _ => new ResourceManager("McDownload.i18n.Common", typeof(Text).Assembly).GetString(name, culture)
        };
    }

    public static void Run(string root, Action<bool, string> check)
    {
        var assembly = typeof(Text).Assembly;
        var resources = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (string name in assembly.GetManifestResourceNames().Where(x => x.StartsWith("McDownload.i18n.") && x.EndsWith(".resources")))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new ResourceReader(stream);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var entries = reader.GetEnumerator();
            while (entries.MoveNext())
            {
                string key = (string)entries.Key, value = (string)entries.Value!;
                check(Regex.IsMatch(key, @"\A[A-Z][A-Za-z0-9]*\z") && value.Length > 0, "resource has a semantic key and nonempty text: " + name + "." + key);
                values.Add(key, value);
            }
            resources.Add(name["McDownload.i18n.".Length..^".resources".Length], values);
        }
        check(resources.Count >= 40 && resources.Sum(x => x.Value.Count) >= 570, "all UI and backend text groups are embedded");
        var uiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en-US", "fr-FR" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
                foreach (var group in resources)
                    foreach (var entry in group.Value)
                        check(Text.Get(group.Key, entry.Key) == entry.Value, "compiled resource and neutral fallback: " + locale + "/" + group.Key + "." + entry.Key);
            }
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            check(Text.Format("QueuedTask", "ProgressPercent", 12.5) == string.Format(CultureInfo.CurrentCulture, Text.Get("QueuedTask", "ProgressPercent"), 12.5),
                "number format follows current culture");
            var timestamp = new DateTime(2026, 10, 23, 12, 34, 56);
            CultureInfo.CurrentCulture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
            CultureInfo.CurrentCulture.DateTimeFormat.TimeSeparator = ".";
            check(Text.Format("Workspace", "TimestampedLog", timestamp, "fixture") == "12.34.56  fixture",
                "formatting honors a culture-specific time separator rather than invariant culture");
            check(Text.Format("Common", "InstalledLoader", null, "fixture") == " · fixture", "formatting preserves null interpolation semantics");
            check((string)new LocExtension("Common", "Completed").ProvideValue(null!) == Text.Get("Common", "Completed"), "XAML markup extension uses the shared resource lookup");
            bool missing = false;
            try { Text.Get("Common", "MissingFixtureKey"); } catch (MissingManifestResourceException) { missing = true; }
            check(missing, "missing resource keys fail instead of silently rendering empty UI");
        }
        finally { CultureInfo.CurrentUICulture = uiCulture; CultureInfo.CurrentCulture = culture; }

        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/McDownload"));
        check(Directory.Exists(source), "localization source audit directory exists");
        foreach (var path in Directory.GetFiles(Path.Combine(source, "i18n"), "*.resx"))
        {
            string group = Path.GetFileNameWithoutExtension(path);
            var data = XDocument.Load(path).Root!.Elements("data").ToArray();
            check(data.Length == resources[group].Count && data.Select(x => (string)x.Attribute("name")!).Distinct().Count() == data.Length,
                "resource source has no duplicate or missing compiled keys: " + group);
            foreach (var entry in data)
                check(resources[group][(string)entry.Attribute("name")!] == entry.Element("value")!.Value,
                    "source text exactly matches compiled resource: " + group + "." + (string)entry.Attribute("name")!);
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories).Where(x => !x.Contains("\\obj\\") && !x.Contains("\\bin\\")))
        {
            string code = File.ReadAllText(path);
            string nonCommentLines = string.Join('\n', code.Split('\n').Where(x => !x.TrimStart().StartsWith("//")));
            check(!Regex.IsMatch(nonCommentLines, "[\\u3400-\\u9fff]"), "no hardcoded Chinese UI text outside resources: " + Path.GetFileName(path));
            check(!Regex.IsMatch(code, @"Text\.Format\([^;\r\n]*,\s*\w+\.Kind\s*[,)]"),
                "UI templates do not render raw loader enum names: " + Path.GetFileName(path));
            check(!code.Contains("Definition?.Format.ToString()"), "pack format label does not bypass resources: " + Path.GetFileName(path));
            foreach (Match match in Regex.Matches(code, "(?:Text|McDownload\\.i18n\\.Text)\\.(?:Get|Format)\\(\"(?<group>[^\"]+)\",\\s*\"(?<key>[^\"]+)\""))
                Reference(match.Groups["group"].Value, match.Groups["key"].Value);
        }
        foreach (string path in Directory.GetFiles(Path.Combine(source, "View"), "*.xaml"))
        {
            var xml = XDocument.Load(path);
            foreach (var attribute in xml.Descendants().Attributes().Where(x => x.Name.NamespaceName != "http://schemas.microsoft.com/winfx/2006/xaml" &&
                x.Name.LocalName is "Text" or "Content" or "Header" or "Title" or "ToolTip" or "AutomationProperties.Name"))
                check(attribute.Value.Length == 0 || attribute.Value.StartsWith('{'), "visible XAML attribute references a resource or binding: " + Path.GetFileName(path) + "/" + attribute.Name);
            foreach (var text in xml.DescendantNodes().OfType<XText>().Where(x => !string.IsNullOrWhiteSpace(x.Value) &&
                x.Parent?.Name.LocalName is not ("Boolean" or "Double" or "Int32")))
                check(false, "unexpected hardcoded XAML text node: " + Path.GetFileName(path));
            foreach (Match match in Regex.Matches(File.ReadAllText(path), @"\{i18n:Loc (?<group>\w+), (?<key>\w+)\}"))
                Reference(match.Groups["group"].Value, match.Groups["key"].Value);
        }
        check(resources.SelectMany(g => g.Value.Keys.Select(k => g.Key + "." + k)).All(used.Contains), "every text entry is referenced by production code or XAML");
        var managers = (ConcurrentDictionary<string, ResourceManager>)typeof(Text).GetField("Managers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var originalCommon = managers["Common"];
        managers["Common"] = new FixtureResources();
        try
        {
            using var state = new WorkspaceViewModel(diagnostics: new DiagnosticLog(Path.Combine(root, "i18n-logs")));
            var settings = new SettingsViewModel(state);
            var preview = new ModpackImportViewModel(state, settings);
            var selection = new LoaderSelectionViewModel(state);
            foreach (var kind in Enum.GetValues<LoaderKind>())
            {
                string label = Text.Get("Common", kind.ToString());
                check(label.StartsWith("fixture-"), "isolated replacement changes resource text independently of enums: " + kind);
                check(DisplayNames.Loader(kind) == label && selection.LoaderTypes.Contains(label), "all loader labels map to common resources: " + kind);
                selection.SelectedType = label;
                var entry = new LoaderVersionEntry("fixture", true);
                selection.Versions.Add(entry); selection.SelectedVersion = entry;
                check(selection.GetSelection()?.Kind == kind, "localized loader label maps back to machine enum: " + kind);
                foreach (var format in Enum.GetValues<ModpackFormat>())
                {
                    preview.Definition = new(format, "fixture", "1", null, "1.20.1", new(kind, "fixture"),
                        Array.Empty<ModpackFile>(), Array.Empty<string>(), new string('0', 64), "fixture.zip");
                    check(preview.PackFormat == Text.Get("Common", format.ToString()) && preview.Loader == Text.Format("ModpackImport", "LoaderVersion", label, "fixture"),
                        "preview renders pack and loader resource names: " + format + "/" + kind);
                }
            }
        }
        finally { managers["Common"] = originalCommon; }
        void Reference(string group, string key)
        {
            check(resources.TryGetValue(group, out var values) && values.ContainsKey(key), "resource reference resolves: " + group + "." + key);
            used.Add(group + "." + key);
        }
    }
}
