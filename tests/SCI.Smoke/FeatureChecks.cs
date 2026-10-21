using System.Net;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownload.Model;
using McDownload.Service;
using McDownload.ViewModel;

internal static class FeatureChecks
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindingContext, ref Guid iid, out nint item);

    private static bool CanResolveShellItem(string path)
    {
        Guid iid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        int result = SHCreateItemFromParsingName(InstanceManager.Normal(path), nint.Zero, ref iid, out nint item);
        if (item != nint.Zero) Marshal.Release(item);
        return result >= 0;
    }

    public sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        public bool MirrorFails { get; set; }
        public bool CorruptMirror { get; set; }
        public string? MirrorManifest { get; set; }
        public bool MirrorLoaderInvalid { get; set; }
        public byte[]? ForgeInstaller { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override { get; set; }
        public static byte[] Client { get; } = Encoding.UTF8.GetBytes("offline-client-fixture");
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        public static JsonObject Descriptor(string uri, byte[] content) => new()
        { ["url"] = uri, ["sha1"] = Hash(content), ["size"] = content.Length };
        private static JsonObject Artifact
        {
            get
            {
                var descriptor = Descriptor("https://libraries.minecraft.net/example/test.jar", Client);
                descriptor["path"] = "example/test.jar";
                return descriptor;
            }
        }
        private static byte[] Index => JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        { ["objects"] = new JsonObject { ["example.txt"] = new JsonObject { ["hash"] = Hash(Client), ["size"] = Client.Length } } });
        private static byte[] Version => JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["id"] = "1.20.1", ["mainClass"] = "net.minecraft.Main", ["type"] = "release",
            ["downloads"] = new JsonObject { ["client"] = Descriptor("https://piston-data.mojang.com/client.jar", Client) },
            ["libraries"] = new JsonArray(new JsonObject
            { ["name"] = "example:test:1", ["downloads"] = new JsonObject { ["artifact"] = Artifact } }),
            ["assetIndex"] = new JsonObject { ["id"] = "fixture", ["url"] = "https://piston-meta.mojang.com/index.json", ["sha1"] = Hash(Index), ["size"] = Index.Length }
        });
        public static HttpResponseMessage Response(string text, string type = "application/json") => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, type) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            cancellationToken.ThrowIfCancellationRequested();
            if (Override is not null) return await Override(request, cancellationToken);
            bool mirror = request.RequestUri!.Host == "bmclapi2.bangbang93.com";
            if (mirror && MirrorFails) return new(HttpStatusCode.ServiceUnavailable);
            string path = request.RequestUri.AbsolutePath;
            if (ForgeInstaller is not null && path.EndsWith("forge-1.20.1-47.3.0-installer.jar.sha1", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent(Convert.ToHexString(SHA1.HashData(ForgeInstaller))) };
            if (ForgeInstaller is not null && path.EndsWith("forge-1.20.1-47.3.0-installer.jar", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ForgeInstaller) };
            if (mirror && MirrorManifest is not null && path.EndsWith("version_manifest_v2.json")) return Response(MirrorManifest);
            if (path.EndsWith("version_manifest_v2.json")) return Response(new JsonObject
            {
                ["latest"] = new JsonObject { ["release"] = "1.20.1", ["snapshot"] = "24w01a" },
                ["versions"] = new JsonArray(
                    new JsonObject { ["id"] = "24w01a", ["type"] = "snapshot", ["url"] = "https://piston-meta.mojang.com/snapshot.json", ["sha1"] = new string('0', 40) },
                    new JsonObject { ["id"] = "1.20.1", ["type"] = "release", ["url"] = "https://piston-meta.mojang.com/version.json", ["sha1"] = Hash(Version) })
            }.ToJsonString());
            if (path.EndsWith("version.json")) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Version) };
            if (path.EndsWith("index.json")) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Index) };
            if (path.Contains("/versions/loader/")) return Response("""
                [{"loader":{"version":"0.16.9","stable":true}},{"loader":{"version":"0.17.0-beta.1","stable":false}}]
                """);
            if (path.StartsWith("/forge/minecraft/")) return mirror && MirrorLoaderInvalid ? Response("null") : Response("""
                [{"mcversion":"1.20.1","version":"47.3.0","files":[{"category":"installer","format":"jar"}]},
                 {"mcversion":"1.20.1","version":"47.4.0-beta","files":[{"category":"installer","format":"jar"}]},
                 {"mcversion":"1.20.1","version":"47.5.0","branch":"1.20.1","files":[{"category":"installer","format":"jar"}]}]
                """);
            if (path.EndsWith("maven-metadata.json")) return Response("""
                {"1.20.1":["1.20.1-47.3.0","1.20.1-47.4.0-beta","1.20.1-47.5.0-1.20.1"]}
                """);
            if (path.EndsWith("maven-metadata.xml")) return Response("""
                <metadata><versioning><versions><version>21.1.90</version><version>21.1.100</version><version>21.1.101-beta</version><version>21.2.0-beta</version><version>20.4.170</version><version>1.20.1-47.1.106</version></versions></versioning></metadata>
                """, "application/xml");
            if (path.EndsWith(".jar") || path.StartsWith("/assets/") || request.RequestUri.Host == "resources.download.minecraft.net")
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(mirror && CorruptMirror ? Enumerable.Repeat((byte)'x', Client.Length).ToArray() : Client) };
            return new(HttpStatusCode.NotFound);
        }
    }

    private const string ForgeVersionId = "1.20.1-forge-47.3.0";

    private static JsonObject ForgeRuntimeProfile() => new()
    {
        ["id"] = ForgeVersionId, ["inheritsFrom"] = "1.20.1",
        ["mainClass"] = "net.minecraftforge.bootstrap.ForgeBootstrap",
        ["arguments"] = new JsonObject { ["game"] = new JsonArray { JsonValue.Create("--launchTarget"), JsonValue.Create("forge_client") } },
        ["libraries"] = new JsonArray()
    };

    private static byte[] CreateForgeInstaller()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "install_profile.json", Encoding.UTF8.GetBytes("""
                {"spec":0,"minecraft":"1.20.1","json":"version.json","processors":[],"libraries":[]}
                """));
            WriteEntry(archive, "version.json", JsonSerializer.SerializeToUtf8Bytes(ForgeRuntimeProfile()));
            WriteEntry(archive, "net/minecraftforge/installer/TestInstaller.class", Encoding.ASCII.GetBytes("install-client"));
        }
        return stream.ToArray();
    }

    private static byte[] CreateProcessorInstaller(string minecraftVersion, JsonObject profile, JsonArray libraries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "install_profile.json", JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["spec"] = 1,
                ["minecraft"] = minecraftVersion,
                ["json"] = "version.json",
                ["processors"] = new JsonArray(),
                ["libraries"] = libraries.DeepClone()
            }));
            WriteEntry(archive, "version.json", JsonSerializer.SerializeToUtf8Bytes(profile));
            WriteEntry(archive, "net/minecraftforge/installer/Fixture.class", Encoding.ASCII.GetBytes("install-client"));
        }
        return stream.ToArray();
    }

    private static byte[] CreateFixtureJar(string content = "fixture")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            WriteEntry(archive, "fixture.txt", Encoding.UTF8.GetBytes(content));
        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        using var output = archive.CreateEntry(name).Open();
        output.Write(content);
    }

    private static async Task<string> RunFixtureForgeInstaller(string java, IEnumerable<string> arguments,
        string work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!arguments.Contains("--install-client", StringComparer.Ordinal))
            throw new InvalidOperationException("Forge fixture received an unexpected installer option.");
        string output = Path.Combine(work, "versions", ForgeVersionId);
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(output, ForgeVersionId + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(ForgeRuntimeProfile()), token);
        return string.Empty;
    }

    public static async Task RunAsync(MainWindowViewModel model, string root, Handler handler, Action<bool, string> check)
    {
        check(SettingsViewModel.DefaultMinecraftRoot == Path.Combine(AppContext.BaseDirectory, ".minecraft"), "EXE-relative default root");
        string oldDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCI", "Minecraft");
        check(SettingsViewModel.ResolveMinecraftRoot(new Dictionary<string, string> { ["root"] = oldDefault }) == SettingsViewModel.DefaultMinecraftRoot, "migrate old default without moving files");
        check(SettingsViewModel.ResolveMinecraftRoot(new Dictionary<string, string> { ["root"] = root, ["rootMode"] = "custom" }) == root, "preserve custom root");
        check(SettingsViewModel.ResolveMinecraftRoot(new Dictionary<string, string> { ["root"] = root, ["rootMode"] = "portable" }) == SettingsViewModel.DefaultMinecraftRoot, "portable setting follows EXE");

        var mappings = new Dictionary<string, string>
        {
            ["https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"] = "/mc/game/version_manifest_v2.json",
            ["https://piston-data.mojang.com/v1/objects/hash/client.jar"] = "/v1/objects/hash/client.jar",
            ["https://resources.download.minecraft.net/ab/hash"] = "/assets/ab/hash",
            ["https://libraries.minecraft.net/a/b.jar"] = "/libraries/a/b.jar",
            ["https://maven.minecraftforge.net/a/b.jar"] = "/maven/a/b.jar",
            ["https://maven.fabricmc.net/a/b.jar"] = "/maven/a/b.jar",
            ["https://meta.fabricmc.net/v2/versions/loader/1.20.1"] = "/fabric-meta/v2/versions/loader/1.20.1",
            ["https://maven.neoforged.net/releases/a/b.jar"] = "/maven/a/b.jar"
        };
        foreach (var item in mappings) check(HmclDownloadProvider.MirrorUri(new(item.Key))?.AbsolutePath == item.Value, "mirror map " + item.Key);
        check(HmclDownloadProvider.MirrorUri(new("https://example.com/a.jar")) is null, "unknown hosts not rewritten");
        using var http = new HttpClient(handler, false);
        var mirror = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Bmclapi));
        var official = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Official));
        foreach (string malformed in new[] { "{}", "{\"versions\":{}}", "{\"versions\":[],\"latest\":{\"release\":\"missing\",\"snapshot\":\"missing\"}}" })
        {
            handler.MirrorManifest = malformed;
            int start = handler.Requests.Count;
            var manifest = await new VersionResolver(model.State.Instances, mirror).GetManifestAsync(default);
            check(manifest.LatestReleaseId == "1.20.1" && handler.Requests.Skip(start).Any(x => x.Host == "piston-meta.mojang.com"), "malformed mirror manifest falls back: " + malformed);
        }
        handler.MirrorManifest = null;
        var forge = await new LoaderVersionCatalog(mirror).GetVersionsAsync("1.20.1", LoaderKind.Forge, default);
        check(forge.Count == 3 && forge.Any(x => x.Version == "47.5.0-1.20.1") && forge.Count(x => !x.IsStable) == 1, "Forge channels and branch");
        handler.MirrorFails = true;
        int retryStart = handler.Requests.Count;
        var fallback = await new LoaderVersionCatalog(mirror).GetVersionsAsync("1.20.1", LoaderKind.Forge, default);
        check(fallback.SequenceEqual(forge), "Forge official fallback preserves version IDs");
        check(handler.Requests.Skip(retryStart).TakeWhile(x => x.Host == "bmclapi2.bangbang93.com").Count() == 6,
            "HMCL candidate retries a transient response five times before fallback");
        handler.MirrorFails = false;
        handler.MirrorLoaderInvalid = true;
        int invalidStart = handler.Requests.Count;
        var malformedLoaderList = await new LoaderVersionCatalog(mirror).GetVersionsAsync("1.20.1", LoaderKind.Forge, default);
        check(malformedLoaderList.SequenceEqual(forge) && handler.Requests.Skip(invalidStart).Select(x => x.Host)
            .SequenceEqual(new[] { "bmclapi2.bangbang93.com", "files.minecraftforge.net" }),
            "invalid mirror loader data falls back to official metadata");
        handler.MirrorLoaderInvalid = false;
        var neo = await new LoaderVersionCatalog(mirror).GetVersionsAsync("1.21.1", LoaderKind.NeoForge, default);
        check(neo.Select(x => x.Version).SequenceEqual(new[] { "21.1.101-beta", "21.1.100", "21.1.90" }), "NeoForge MC matching, channels, numeric sort");
        var legacyNeo = await new LoaderVersionCatalog(official).GetVersionsAsync("1.20.1", LoaderKind.NeoForge, default);
        check(legacyNeo.Single().Version == "47.1.106", "NeoForge 1.20.1 uses legacy artifact");
        check(!LoaderVersionCatalog.SupportsNeoForge("1.21.1", "21.10.1"), "MC prefix collision rejected");
        check(LoaderVersionCatalog.SupportsNeoForge("26.1", "26.1.0.5") && !LoaderVersionCatalog.SupportsNeoForge("26.1", "26.1.1.5"), "NeoForge new release mapping");
        check(LoaderVersionCatalog.SupportsNeoForge("26.1-snapshot-1", "26.1.0.0-alpha.1+snapshot-1"), "NeoForge snapshot mapping");

        string binary = Path.Combine(root, "verified.jar");
        byte[] content = Handler.Client;
        string sha1 = Convert.ToHexString(SHA1.HashData(content));
        handler.CorruptMirror = true;
        await mirror.DownloadAsync(new("https://libraries.minecraft.net/a.jar"), binary, sha1, content.Length, default);
        check((await File.ReadAllBytesAsync(binary)).SequenceEqual(content), "bad mirror SHA falls back to official atomically");
        handler.CorruptMirror = false;
        handler.Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Enumerable.Repeat((byte)'x', content.Length).ToArray()) });
        bool integrityRejected = false;
        try { await mirror.DownloadAsync(new("https://libraries.minecraft.net/a.jar"), binary, sha1, content.Length, default); }
        catch (DownloadIntegrityException) { integrityRejected = true; }
        check(integrityRejected && (await File.ReadAllBytesAsync(binary)).SequenceEqual(content), "both sources invalid preserves existing file");
        handler.Override = null;
        int before = handler.Requests.Count;
        await official.DownloadAsync(new("https://libraries.minecraft.net/a.jar"), binary, sha1, content.Length, default);
        check(handler.Requests.Skip(before).All(x => x.Host == "libraries.minecraft.net"), "official mode bypasses mirror");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); before = handler.Requests.Count;
            bool stopped = false;
            try { await mirror.ReadAsync(new("https://meta.fabricmc.net/v2/versions/loader/1.20.1"), 100000, bytes => bytes, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            check(stopped && handler.Requests.Count == before, "user cancellation never falls back");
        }
        using (var inFlight = new CancellationTokenSource())
        {
            handler.Override = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Handler.Response("[]"); };
            before = handler.Requests.Count;
            var pending = mirror.ReadAsync(new Uri("https://meta.fabricmc.net/v2/versions/loader/1.20.1"), 100000, bytes => bytes, inFlight.Token);
            inFlight.Cancel();
            bool stopped = false;
            try { await pending; } catch (OperationCanceledException) { stopped = true; }
            check(stopped && handler.Requests.Count == before + 1, "in-flight cancellation does not call official source");
            handler.Override = null;
        }

        await model.Download.RefreshVersionsAsync();
        check(model.Download.MinecraftVersions.Single().Channel == MinecraftVersionChannel.Release, "default MC release filter");
        model.Download.SelectedMinecraftChannel = "测试版";
        check(model.Download.MinecraftVersions.Single().Id == "24w01a", "MC snapshot filter");
        model.Download.SelectedMinecraftChannel = "全部";
        check(model.Download.MinecraftVersions.Count == 2, "MC all filter");
        model.Download.SelectedMinecraftChannel = "正式版";
        var selector = model.Download.NewGameLoader;
        selector.SelectedType = "Fabric"; await selector.PendingRefresh;
        check(selector.Versions.Single().IsStable && selector.SelectedVersion?.Version == "0.16.9", "selected loader auto-loads stable versions");
        selector.SelectedChannel = "测试版";
        check(selector.Versions.Single().Version == "0.17.0-beta.1", "loader test filter");
        selector.SelectedChannel = "全部";
        check(selector.Versions.Count == 2, "loader all filter");
        selector.SelectedType = "Forge"; await selector.PendingRefresh;
        check(selector.Versions.All(x => x.Version.StartsWith("47.")), "switch loader replaces list");
        before = handler.Requests.Count;
        await selector.ReloadCommand.ExecuteAsync(null);
        check(handler.Requests.Count > before, "manual loader refresh bypasses cache");
        check(model.Download.InstanceLoader.SelectedType == "不安装", "download and instance selectors independent");

        // 返回已取消请求的延迟响应，验证旧结果不能覆盖当前选择。
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Override = (request, ct) => request.RequestUri!.AbsolutePath.Contains("delayed")
            ? delayed.Task : Task.FromResult(Handler.Response("[]"));
        selector.MinecraftVersion = "delayed";
        Task oldRequest = selector.PendingRefresh;
        selector.SelectedType = "不安装"; await selector.PendingRefresh;
        delayed.SetResult(Handler.Response("[]")); await oldRequest;
        check(selector.Versions.Count == 0 && selector.SelectedVersion is null && !selector.IsLoading, "out-of-order loader requests ignored");
        handler.Override = null;

        foreach (var selection in new[]
        {
            new LoaderSelection(LoaderKind.Forge, "47.3.0"),
            new LoaderSelection(LoaderKind.Fabric, "0.16.9"),
            new LoaderSelection(LoaderKind.NeoForge, "21.1.100")
        })
        {
            check(DownloadViewModel.ResolveInstanceName(" \t", "1.21.1", selection) == $"1.21.1-{selection.Kind}-{selection.Version}",
                "blank instance name includes Minecraft, loader and version: " + selection.Kind);
            check(DownloadViewModel.ResolveInstanceName("  My world  ", "1.21.1", selection) == "My world",
                "explicit instance name is preserved: " + selection.Kind);
        }
        selector.MinecraftVersion = model.Download.SelectedMinecraftVersion!.Id;
        await selector.PendingRefresh;
        foreach (string requestedName in new[] { " \t", "  Custom vanilla  " })
        {
            model.Download.InstanceName = requestedName;
            await model.Download.InstallGameCommand.ExecuteAsync(null);
            string expectedName = string.IsNullOrWhiteSpace(requestedName) ? "1.20.1" : requestedName.Trim();
            check(model.Download.TargetInstance is { } named && named.DirectoryName == expectedName &&
                named.Editable.DisplayName == expectedName && File.Exists(Path.Combine(root, "versions", expectedName, expectedName + ".json")),
                "install command uses resolved name for directory and display: " + expectedName);
        }

        var installed = await model.State.Instances.CreateAsync(new("Downloaded", "1.20.1", IsolationPolicy.PerInstance, new("Downloaded", null, null)), default);
        before = handler.Requests.Count;
        var game = await model.State.Games.InstallAsync(installed.Id,
            model.Download.MinecraftVersions.Single(), new(Guid.NewGuid(), null, default));
        var paths = await model.State.Instances.ResolvePathsAsync(game.Id, default);
        check(File.Exists(paths.ClientJarFile) && File.Exists(paths.VersionJsonFile) && File.Exists(paths.BaseVersionJsonFile), "game download publishes managed files");
        check(CanResolveShellItem(InstanceManager.ShellParsingPath(paths.GameDirectory)), "Windows shell accepts the normalized instance delete path");
        var publishedVersion = JsonNode.Parse(await File.ReadAllBytesAsync(paths.VersionJsonFile))!.AsObject();
        check(publishedVersion["id"]?.GetValue<string>() == "Downloaded" && publishedVersion["inheritsFrom"] is null &&
            publishedVersion["mainClass"]?.GetValue<string>() == "net.minecraft.Main" &&
            publishedVersion["downloads"]?["client"] is JsonObject && publishedVersion["libraries"] is JsonArray,
            "vanilla version metadata is standalone for external launchers");
        var forgeBase = new VersionDocument("1.21.1", new JsonObject
        {
            ["id"] = "1.21.1", ["mainClass"] = "net.minecraft.Main",
            ["downloads"] = new JsonObject { ["client"] = new JsonObject { ["url"] = "https://example.invalid/client.jar" } },
            ["assetIndex"] = new JsonObject { ["id"] = "1.21.1" },
            ["javaVersion"] = new JsonObject { ["majorVersion"] = 21 },
            ["arguments"] = new JsonObject { ["game"] = new JsonArray { JsonValue.Create("--username") } },
            ["libraries"] = new JsonArray { new JsonObject { ["name"] = "com.mojang:base:1" } }
        });
        var forgeProfile = new JsonObject
        {
            ["id"] = "Forge", ["inheritsFrom"] = "1.21.1", ["mainClass"] = "net.minecraftforge.bootstrap.ForgeBootstrap",
            ["arguments"] = new JsonObject { ["game"] = new JsonArray { JsonValue.Create("--launchTarget"), JsonValue.Create("forge_client") } },
            ["libraries"] = new JsonArray { new JsonObject { ["name"] = "net.minecraftforge:forge:1.21.1-52.1.20" } }
        };
        var forgePatch = new VersionPatch(Guid.NewGuid(), VersionPatchKind.Loader,
            new(LoaderKind.Forge, "52.1.20"), 1000, forgeProfile);
        var effectiveForge = model.State.Versions.Compose(forgeBase, new[] { forgePatch }, "ForgeInstance");
        string forgeFixture = Path.Combine(root, "standalone-forge.json");
        await File.WriteAllBytesAsync(forgeFixture, model.State.Versions.SerializeStandalone(effectiveForge));
        var publishedForge = JsonNode.Parse(await File.ReadAllBytesAsync(forgeFixture))!.AsObject();
        var forgeArguments = publishedForge["arguments"]?["game"]?.AsArray().Select(x => x?.GetValue<string>()).ToArray();
        var forgeLibraries = (publishedForge["libraries"] as JsonArray)?.OfType<JsonObject>()
            .Select(x => x["name"]?.GetValue<string>()).ToArray();
        check(publishedForge["inheritsFrom"] is null && publishedForge["id"]?.GetValue<string>() == "ForgeInstance" &&
            publishedForge["jar"]?.GetValue<string>() == "ForgeInstance" &&
            publishedForge["mainClass"]?.GetValue<string>() == "net.minecraftforge.bootstrap.ForgeBootstrap" &&
            publishedForge["downloads"]?["client"]?["url"]?.GetValue<string>() == "https://example.invalid/client.jar" &&
            publishedForge["assetIndex"]?["id"]?.GetValue<string>() == "1.21.1" &&
            publishedForge["javaVersion"]?["majorVersion"]?.GetValue<int>() == 21 &&
            forgeArguments?.SequenceEqual(new[] { "--username", "--launchTarget", "forge_client" }) == true &&
            forgeLibraries?.SequenceEqual(new[] { "com.mojang:base:1", "net.minecraftforge:forge:1.21.1-52.1.20" }) == true,
            "Forge version metadata is standalone with merged vanilla and loader fields");
        handler.ForgeInstaller = CreateForgeInstaller();
        var fixtureLoaderInstaller = new LoaderInstaller(model.State.Instances, model.State.Versions, model.State.Fetcher,
            (_, _, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult("fixture-java"); },
            RunFixtureForgeInstaller);
        var forgeInstall = await fixtureLoaderInstaller.InstallAsync(
            new(game.Id, game.Revision, new(LoaderKind.Forge, "47.3.0"), null), new(Guid.NewGuid(), null, default));
        var installedForge = JsonNode.Parse(await File.ReadAllBytesAsync(paths.VersionJsonFile))!.AsObject();
        var installedArguments = installedForge["arguments"]?["game"]?.AsArray().Select(x => x?.GetValue<string>()).ToArray();
        check(forgeInstall.FinalState.InstalledLoader == new LoaderSelection(LoaderKind.Forge, "47.3.0") &&
            installedForge["inheritsFrom"] is null && installedForge["id"]?.GetValue<string>() == "Downloaded" &&
            installedForge["mainClass"]?.GetValue<string>() == "net.minecraftforge.bootstrap.ForgeBootstrap" &&
            installedForge["downloads"]?["client"]?["sha1"]?.GetValue<string>() == Convert.ToHexString(SHA1.HashData(Handler.Client)).ToLowerInvariant() &&
            installedForge["assetIndex"]?["id"]?.GetValue<string>() == "fixture" &&
            installedArguments?.SequenceEqual(new[] { "--launchTarget", "forge_client" }) == true,
            "Forge install commits a standalone merged version JSON");
        check(handler.Requests.Skip(before).All(x => x.Host == "bmclapi2.bangbang93.com"), "game metadata/client/libraries/assets use mirror");

        foreach (var (replacementMinecraftVersion, forgeVersion, forgeMainClass) in new[]
        {
            ("1.19.1", "42.0.9", "cpw.mods.bootstraplauncher.BootstrapLauncher"),
            ("1.20.1", "47.4.28", "cpw.mods.bootstraplauncher.BootstrapLauncher"),
            ("1.21.1", "52.1.20", "net.minecraftforge.bootstrap.ForgeBootstrap")
        })
        {
        string replacementRoot = Path.Combine(root, "forge-to-neoforge-" + replacementMinecraftVersion);
        var replacementManager = new InstanceManager(replacementRoot);
        var replacementFetcher = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Official));
        var replacementVersions = new VersionResolver(replacementManager, replacementFetcher);
        const string neoForgeVersion = "21.1.100";
        string forgeProfileId = $"{replacementMinecraftVersion}-forge-{forgeVersion}";
        const string neoForgeProfileId = "1.21.1-neoforge-21.1.100";
        string universalRelativePath = $"net/neoforged/neoforge/{neoForgeVersion}/neoforge-{neoForgeVersion}-universal.jar";
        string universalUrl = "https://maven.neoforged.net/releases/" + universalRelativePath;
        byte[] universalJar = CreateFixtureJar();
        string universalSha1 = Convert.ToHexString(SHA1.HashData(universalJar)).ToLowerInvariant();
        string vanillaJoptRelativePath = "net/sf/jopt-simple/jopt-simple/5.0.4/jopt-simple-5.0.4.jar";
        string vanillaJoptUrl = "https://libraries.minecraft.net/" + vanillaJoptRelativePath;
        string vanillaJoptSha1 = Convert.ToHexString(SHA1.HashData(universalJar)).ToLowerInvariant();
        string mappingsVersion = replacementMinecraftVersion == "1.20.1" ? "20230612.114412" : "fixture";
        string clientVersion = replacementMinecraftVersion + "-" + mappingsVersion;
        var generatedLibraries = new Dictionary<string, byte[]>
        {
            [$"net/minecraft/client/{clientVersion}/client-{clientVersion}-srg.jar"] = CreateFixtureJar("srg"),
            [$"net/minecraft/client/{clientVersion}/client-{clientVersion}-extra.jar"] = CreateFixtureJar("extra"),
            [$"net/minecraftforge/forge/{replacementMinecraftVersion}-{forgeVersion}/forge-{replacementMinecraftVersion}-{forgeVersion}-client.jar"] = CreateFixtureJar("patched-client")
        };
        var universalLibrary = new JsonObject
        {
            ["name"] = $"net.neoforged:neoforge:{neoForgeVersion}:universal",
            ["downloads"] = new JsonObject
            {
                ["artifact"] = new JsonObject
                {
                    ["path"] = universalRelativePath,
                    ["url"] = universalUrl,
                    ["sha1"] = universalSha1,
                    ["size"] = universalJar.Length
                }
            }
        };
        var incompatibleJoptLibrary = new JsonObject
        {
            ["name"] = "net.sf.jopt-simple:jopt-simple:6.0-alpha-3",
            ["rules"] = new JsonArray(new JsonObject
            {
                ["action"] = "allow",
                ["os"] = new JsonObject { ["name"] = "windows" }
            })
        };
        JsonObject FixtureLibrary(string name, string path)
        {
            var artifact = Handler.Descriptor("https://maven.minecraftforge.net/" + path, universalJar);
            artifact["path"] = path;
            return new JsonObject { ["name"] = name, ["downloads"] = new JsonObject { ["artifact"] = artifact } };
        }
        var runtimeGuava = FixtureLibrary("com.google.guava:guava:31.0.1-jre", "com/google/guava/guava/31.0.1-jre/guava-31.0.1-jre.jar");
        var installerGuava = FixtureLibrary("com.google.guava:guava:20.0", "com/google/guava/guava/20.0/guava-20.0.jar");
        var installerTool = FixtureLibrary("net.minecraftforge:ForgeAutoRenamingTool:0.1.22:all",
            "net/minecraftforge/ForgeAutoRenamingTool/0.1.22/ForgeAutoRenamingTool-0.1.22-all.jar");
        var forgeRuntimeProfile = new JsonObject
        {
            ["id"] = forgeProfileId,
            ["inheritsFrom"] = replacementMinecraftVersion,
            ["mainClass"] = forgeMainClass,
            ["arguments"] = new JsonObject { ["game"] = new JsonArray { "--launchTarget", "forgeclient" } },
            ["libraries"] = new JsonArray((JsonNode)runtimeGuava.DeepClone())
        };
        var neoForgeRuntimeProfile = new JsonObject
        {
            ["id"] = neoForgeProfileId,
            ["inheritsFrom"] = replacementMinecraftVersion,
            ["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher",
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray { "--fml.neoForgeVersion", neoForgeVersion, "--launchTarget", "forgeclient" }
            },
            // The universal artifact is intentionally provided only by install_profile.json.
            ["libraries"] = new JsonArray()
        };
        byte[] vanilla121 = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["id"] = replacementMinecraftVersion,
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["javaVersion"] = new JsonObject { ["majorVersion"] = 21 },
            ["downloads"] = new JsonObject
            {
                ["client"] = new JsonObject
                {
                    ["url"] = $"https://piston-data.mojang.com/client-{replacementMinecraftVersion}.jar",
                    ["sha1"] = Convert.ToHexString(SHA1.HashData(Handler.Client)).ToLowerInvariant(),
                    ["size"] = Handler.Client.Length
                }
            },
            ["arguments"] = new JsonObject { ["game"] = new JsonArray(), ["jvm"] = new JsonArray() },
            ["libraries"] = new JsonArray(new JsonObject
            {
                ["name"] = "net.sf.jopt-simple:jopt-simple:5.0.4",
                ["downloads"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        ["path"] = vanillaJoptRelativePath,
                        ["url"] = vanillaJoptUrl,
                        ["sha1"] = vanillaJoptSha1,
                        ["size"] = universalJar.Length
                    }
                }
            })
        });
        byte[] forgeReplacementInstaller = CreateProcessorInstaller(replacementMinecraftVersion, forgeRuntimeProfile,
            new JsonArray((JsonNode)incompatibleJoptLibrary.DeepClone(), installerGuava.DeepClone(), installerTool.DeepClone()));
        byte[] neoForgeReplacementInstaller = CreateProcessorInstaller(replacementMinecraftVersion, neoForgeRuntimeProfile,
            new JsonArray((JsonNode)universalLibrary.DeepClone()));
        byte[] vanilla121Sha = SHA1.HashData(vanilla121);
        byte[] replacementManifest = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["latest"] = new JsonObject { ["release"] = replacementMinecraftVersion, ["snapshot"] = "24w01a" },
            ["versions"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = replacementMinecraftVersion,
                    ["type"] = "release",
                    ["url"] = $"https://piston-meta.mojang.com/version-{replacementMinecraftVersion}.json",
                    ["sha1"] = Convert.ToHexString(vanilla121Sha).ToLowerInvariant()
                },
                new JsonObject
                {
                    ["id"] = "24w01a",
                    ["type"] = "snapshot",
                    ["url"] = $"https://piston-meta.mojang.com/version-{replacementMinecraftVersion}.json",
                    ["sha1"] = Convert.ToHexString(vanilla121Sha).ToLowerInvariant()
                })
        });
        handler.Override = (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
            if (path.EndsWith("version_manifest_v2.json", StringComparison.Ordinal)) return Task.FromResult(Bytes(replacementManifest));
            if (path.EndsWith($"version-{replacementMinecraftVersion}.json", StringComparison.Ordinal)) return Task.FromResult(Bytes(vanilla121));
            if (path.EndsWith($"forge-{replacementMinecraftVersion}-{forgeVersion}-installer.jar.sha1", StringComparison.Ordinal))
                return Task.FromResult(Text(Convert.ToHexString(SHA1.HashData(forgeReplacementInstaller))));
            if (path.EndsWith($"forge-{replacementMinecraftVersion}-{forgeVersion}-installer.jar", StringComparison.Ordinal))
                return Task.FromResult(Bytes(forgeReplacementInstaller));
            if (path.EndsWith("neoforge-21.1.100-installer.jar.sha1", StringComparison.Ordinal))
                return Task.FromResult(Text(Convert.ToHexString(SHA1.HashData(neoForgeReplacementInstaller))));
            if (path.EndsWith("neoforge-21.1.100-installer.jar", StringComparison.Ordinal))
                return Task.FromResult(Bytes(neoForgeReplacementInstaller));
            if (path.EndsWith("neoforge-21.1.100-universal.jar", StringComparison.Ordinal)) return Task.FromResult(Bytes(universalJar));
            if (path.EndsWith("jopt-simple-5.0.4.jar", StringComparison.Ordinal) ||
                path.EndsWith("jopt-simple-6.0-alpha-3.jar", StringComparison.Ordinal)) return Task.FromResult(Bytes(universalJar));
            if (path.EndsWith("jopt-simple-6.0-alpha-3.jar.sha1", StringComparison.Ordinal))
                return Task.FromResult(Text(Convert.ToHexString(SHA1.HashData(universalJar))));
            if (path.Contains("/com/google/guava/guava/", StringComparison.Ordinal) ||
                path.EndsWith("ForgeAutoRenamingTool-0.1.22-all.jar", StringComparison.Ordinal)) return Task.FromResult(Bytes(universalJar));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };
        try
        {
            var replacementMetadata = await replacementManager.CreateAsync(new("ForgeToNeo", replacementMinecraftVersion,
                IsolationPolicy.PerInstance, new("ForgeToNeo", null, null)), default);
            var replacementPaths = await replacementManager.ResolvePathsAsync(replacementMetadata.Id, default);
            await File.WriteAllBytesAsync(replacementPaths.ClientJarFile, Handler.Client);
            var vanillaBaseJson = (JsonObject)JsonNode.Parse(vanilla121)!.AsObject().DeepClone();
            string parentId = VersionResolver.ParentId(replacementMetadata);
            vanillaBaseJson["id"] = parentId;
            var vanillaBase = new VersionDocument(parentId, vanillaBaseJson);
            var baseOnly = replacementVersions.Compose(vanillaBase, Array.Empty<VersionPatch>(), replacementMetadata.DirectoryName);
            replacementMetadata = await replacementManager.CommitLockedAsync(replacementPaths.GameDirectory, replacementMetadata,
                new()
                {
                    [".launcher/base.json"] = InstanceManager.Serialize(vanillaBaseJson),
                    [replacementMetadata.DirectoryName + ".json"] = replacementVersions.SerializeStandalone(baseOnly)
                }, default);

            string? installerWork = null;
            bool corruptGeneratedJar = false;
            var replacementInstaller = new LoaderInstaller(replacementManager, replacementVersions, replacementFetcher,
                (_, _, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult("fixture-java"); },
                async (_, arguments, work, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!arguments.Contains("--install-client", StringComparer.Ordinal))
                        throw new InvalidOperationException("Replacement fixture received an unexpected installer option.");
                    installerWork = work;
                    using var archive = ZipFile.OpenRead(Path.Combine(work, "installer.jar"));
                    using var reader = new StreamReader(archive.GetEntry("version.json")!.Open());
                    JsonObject runtime = JsonNode.Parse(await reader.ReadToEndAsync(token))!.AsObject();
                    string id = runtime["id"]!.GetValue<string>();
                    string output = Path.Combine(work, "versions", id);
                    Directory.CreateDirectory(output);
                    await File.WriteAllBytesAsync(Path.Combine(output, id + ".json"),
                        JsonSerializer.SerializeToUtf8Bytes(runtime), token);
                    foreach (var generated in generatedLibraries)
                    {
                        string path = Path.Combine(work, "libraries", generated.Key.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllBytesAsync(path, generated.Value, token);
                    }
                    if (corruptGeneratedJar)
                    {
                        string path = Path.Combine(work, "libraries", "invalid", "broken.jar");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("not a jar"), token);
                    }
                    return string.Empty;
                });
            var replacementForgeInstall = await replacementInstaller.InstallAsync(
                new(replacementMetadata.Id, replacementMetadata.Revision, new(LoaderKind.Forge, forgeVersion), null),
                new(Guid.NewGuid(), null, default));
            var replacementForgeJson = JsonNode.Parse(await File.ReadAllBytesAsync(replacementPaths.VersionJsonFile))!.AsObject();
            var forgeJoptLibraries = (replacementForgeJson["libraries"] as JsonArray)?.OfType<JsonObject>()
                .Select(x => x["name"]?.GetValue<string>())
                .Where(x => x?.StartsWith("net.sf.jopt-simple:jopt-simple:", StringComparison.Ordinal) == true).ToArray();
            string vanillaJoptFile = Path.Combine(replacementRoot, "libraries", vanillaJoptRelativePath.Replace('/', Path.DirectorySeparatorChar));
            check(forgeJoptLibraries?.SequenceEqual(new[] { "net.sf.jopt-simple:jopt-simple:5.0.4" }) == true &&
                File.Exists(vanillaJoptFile) && (await File.ReadAllBytesAsync(vanillaJoptFile)).SequenceEqual(universalJar),
                "Forge keeps Minecraft's compatible jopt.simple module instead of installer alpha");
            var publishedLibraries = replacementForgeJson["libraries"]!.AsArray().OfType<JsonObject>()
                .Select(x => x["name"]!.GetValue<string>()).ToArray();
            check(replacementForgeJson["mainClass"]?.GetValue<string>() == forgeMainClass &&
                publishedLibraries.Contains("com.google.guava:guava:31.0.1-jre") &&
                !publishedLibraries.Contains("com.google.guava:guava:20.0") &&
                !publishedLibraries.Contains("net.minecraftforge:ForgeAutoRenamingTool:0.1.22:all") &&
                File.Exists(Path.Combine(replacementRoot, "libraries", "net/sf/jopt-simple/jopt-simple/6.0-alpha-3/jopt-simple-6.0-alpha-3.jar")) &&
                File.Exists(Path.Combine(replacementRoot, "libraries", "net/minecraftforge/ForgeAutoRenamingTool/0.1.22/ForgeAutoRenamingTool-0.1.22-all.jar")),
                "installer tools are retained on disk without polluting runtime libraries: " + replacementMinecraftVersion);
            bool generatedFilesMatch = true;
            foreach (var generated in generatedLibraries)
            {
                string file = Path.Combine(replacementRoot, "libraries", generated.Key.Replace('/', Path.DirectorySeparatorChar));
                generatedFilesMatch &= File.Exists(file) && (await File.ReadAllBytesAsync(file)).SequenceEqual(generated.Value);
            }
            check(generatedFilesMatch && installerWork is not null && !Directory.Exists(installerWork) &&
                generatedLibraries.Keys.All(x => !replacementForgeJson.ToJsonString().Contains(x, StringComparison.Ordinal)) &&
                (await File.ReadAllBytesAsync(replacementPaths.ClientJarFile)).SequenceEqual(Handler.Client),
                "processor-only srg, extra and patched client survive work cleanup without changing the classpath or vanilla jar: " + replacementMinecraftVersion);
            if (replacementMinecraftVersion != "1.21.1") continue;
            var replacementPlan = await replacementInstaller.PrepareReplacementAsync(
                new(replacementMetadata.Id, replacementForgeInstall.FinalState.Revision, new(LoaderKind.NeoForge, neoForgeVersion), null), default);
            var installedNeoForge = await replacementInstaller.ReplaceAsync(replacementPlan,
                new(replacementPlan.PlanId, true), new(Guid.NewGuid(), null, default));
            var neoForgeJson = JsonNode.Parse(await File.ReadAllBytesAsync(replacementPaths.VersionJsonFile))!.AsObject();
            var neoForgeLibraries = (neoForgeJson["libraries"] as JsonArray)?.OfType<JsonObject>()
                .Select(x => x["name"]?.GetValue<string>()).ToArray();
            string universalFile = Path.Combine(replacementRoot, "libraries", universalRelativePath.Replace('/', Path.DirectorySeparatorChar));
            check(installedNeoForge.FinalState.InstalledLoader == new LoaderSelection(LoaderKind.NeoForge, neoForgeVersion) &&
                neoForgeJson["inheritsFrom"] is null && neoForgeJson["id"]?.GetValue<string>() == "ForgeToNeo" &&
                neoForgeLibraries?.Contains($"net.neoforged:neoforge:{neoForgeVersion}:universal") == false &&
                File.Exists(universalFile) && (await File.ReadAllBytesAsync(universalFile)).SequenceEqual(universalJar),
                "Forge-to-NeoForge replacement retains the installer-only universal file outside the runtime classpath");
            var removedNeoForge = await replacementInstaller.RemoveAsync(new(replacementMetadata.Id,
                installedNeoForge.FinalState.Revision, installedNeoForge.FinalState.LoaderPatchId!.Value), new(Guid.NewGuid(), null, default));
            corruptGeneratedJar = true;
            bool invalidOutputRejected = false;
            try
            {
                await replacementInstaller.InstallAsync(new(replacementMetadata.Id, removedNeoForge.FinalState.Revision,
                    new(LoaderKind.Forge, forgeVersion), null), new(Guid.NewGuid(), null, default));
            }
            catch (LoaderOperationFailedException error) when (error.InnerException is InvalidDataException) { invalidOutputRejected = true; }
            var afterInvalidOutput = await replacementManager.GetAsync(replacementMetadata.Id, default);
            check(invalidOutputRejected && afterInvalidOutput.Revision == removedNeoForge.FinalState.Revision &&
                afterInvalidOutput.InstalledLoader is null && !File.Exists(Path.Combine(replacementRoot, "libraries", "invalid", "broken.jar")),
                "malformed processor-only JAR cannot be published or committed as an installed loader");
        }
        finally { handler.Override = null; }
        }
        check(File.Exists(Path.Combine(paths.AssetsDirectory, "objects", sha1[..2].ToLowerInvariant(), sha1.ToLowerInvariant())), "game resource cache populated");

        string versionsRoot = Path.Combine(root, "versions");
        foreach (var item in new Dictionary<string, string>
        {
            ["1.20.1"] = """{"id":"1.20.1","mainClass":"net.minecraft.Main"}""",
            ["ExternalFabric"] = """{"id":"ExternalFabric","inheritsFrom":"1.20.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"},{"name":"net.fabricmc:intermediary:1.20.1"}]}""",
            ["ExternalForge"] = """{"id":"ExternalForge","inheritsFrom":"1.20.1","libraries":[{"name":"net.minecraftforge:forge:1.20.1-47.3.0"}]}""",
            ["ExternalNeo"] = """{"id":"ExternalNeo","inheritsFrom":"1.21.1","libraries":[{"name":"net.neoforged:neoforge:21.1.100"}]}""",
            ["ExternalForgeArgs"] = """{"id":"ExternalForgeArgs","arguments":{"game":["--fml.forgeVersion","52.1.20","--fml.mcVersion","1.21.1"]},"libraries":[{"name":"net.minecraftforge:fmlloader:1.21.1-52.1.20"}]}""",
            ["ExternalNeoArgs"] = """{"id":"ExternalNeoArgs","inheritsFrom":"1.21.1","arguments":{"game":["--fml.neoForgeVersion","21.1.100","--fml.mcVersion","1.21.1"]},"libraries":[]}""",
            ["ExternalLegacyNeo"] = """{"id":"ExternalLegacyNeo","minecraftArguments":"--fml.forgeVersion 47.1.106 --fml.mcVersion 1.20.1 --fml.forgeGroup net.neoforged","libraries":[]}""",
            ["ExternalConditionalForge"] = """{"id":"ExternalConditionalForge","arguments":{"game":[{"rules":[{"action":"allow"}],"value":["--fml.forgeVersion=47.3.0","--fml.mcVersion=1.20.1"]}]}}""",
            ["ExternalPatchNeo"] = """{"id":"ExternalPatchNeo","patches":[{"id":"game","version":"1.21.1"},{"id":"neoforge","version":"21.1.100"}]}""",
            ["ExternalInheritedForge"] = """{"id":"ExternalInheritedForge","inheritsFrom":"ExternalForgeArgs"}""",
            ["ExternalVanilla"] = """{"id":"ExternalVanilla","clientVersion":"1.21.1","arguments":{"game":["--username","Player"]},"libraries":[{"name":"cpw.mods:modlauncher:11.0.4"}]}""",
            ["Broken"] = "{broken",
            ["Cycle"] = """{"id":"Cycle","inheritsFrom":"Cycle"}"""
        })
        {
            Directory.CreateDirectory(Path.Combine(versionsRoot, item.Key));
            await File.WriteAllTextAsync(Path.Combine(versionsRoot, item.Key, item.Key + ".json"), item.Value);
        }
        var unmanagedBefore = Directory.GetFiles(versionsRoot, "*.json", SearchOption.AllDirectories)
            .Where(x => x.Contains("External") || x.EndsWith("1.20.1.json")).ToDictionary(x => x, File.ReadAllBytes);
        await model.Instances.RefreshAsync();
        var external = model.Instances.Instances.Single(x => x.Name == "ExternalFabric");
        check(!external.CanManage && external.MinecraftVersion == "1.20.1" && external.Loader == "Fabric · 0.16.9", "standard Fabric recognized read-only");
        check(model.Instances.Instances.Single(x => x.Name == "ExternalForge").Loader == "Forge · 47.3.0", "standard Forge recognized");
        check(model.Instances.Instances.Single(x => x.Name == "ExternalNeo").Loader == "NeoForge · 21.1.100", "standard NeoForge recognized with missing parent");
        foreach (var (name, minecraft, loader) in new[]
        {
            ("ExternalForgeArgs", "1.21.1", "Forge · 52.1.20"),
            ("ExternalNeoArgs", "1.21.1", "NeoForge · 21.1.100"),
            ("ExternalLegacyNeo", "1.20.1", "NeoForge · 47.1.106"),
            ("ExternalConditionalForge", "1.20.1", "Forge · 47.3.0"),
            ("ExternalPatchNeo", "1.21.1", "NeoForge · 21.1.100"),
            ("ExternalInheritedForge", "1.21.1", "Forge · 52.1.20"),
            ("ExternalVanilla", "1.21.1", "未安装加载器")
        })
        {
            var found = model.Instances.Instances.Single(x => x.Name == name);
            check(found.MinecraftVersion == minecraft && found.Loader == loader && !found.CanManage,
                "external metadata detection remains read-only: " + name);
        }
        check(model.Instances.Instances.Single(x => x.Name == "Broken").Details.Contains("读取失败") && model.Instances.Instances.Any(x => x.Name == "Cycle"), "bad JSON and cyclic inheritance isolated");
        model.Instances.SelectedInstance = external;
        await model.Instances.ManageLoaderCommand.ExecuteAsync(null);
        await model.Instances.DeleteCommand.ExecuteAsync(null);
        await model.Instances.ConfirmDeleteCommand.ExecuteAsync(null);
        check(!model.Instances.IsDeleteConfirmationVisible && model.Download.TargetInstance?.DirectoryName != "ExternalFabric", "external management commands do nothing");
        check(unmanagedBefore.All(x => File.Exists(x.Key) && File.ReadAllBytes(x.Key).SequenceEqual(x.Value)) &&
            !File.Exists(Path.Combine(external.Version.DirectoryPath, "instance.json")), "discovery never rewrites external files");

        string deleteRoot = Path.Combine(root, "delete-flow");
        string? recycleTarget = null;
        var deleteManager = new InstanceManager(deleteRoot, null, (path, token) =>
        {
            token.ThrowIfCancellationRequested();
            recycleTarget = path;
            return Task.CompletedTask;
        });
        var deleteInstance = await deleteManager.CreateAsync(new("Disposable", "1.20.1", IsolationPolicy.PerInstance,
            new("Disposable", null, null)), default);
        string deleteDirectory = Path.Combine(deleteRoot, "versions", "Disposable");
        var deleteRequest = new InstanceDeleteRequest(deleteInstance.Id, deleteInstance.Revision,
            InstanceDeleteMode.RecycleInstanceDirectory, false);
        bool confirmationRequired = false;
        try { await deleteManager.DeleteAsync(deleteRequest, default); }
        catch (UserConfirmationRequiredException) { confirmationRequired = true; }
        await deleteManager.DeleteAsync(deleteRequest with { UserDataDeletionConfirmed = true }, default);
        check(confirmationRequired && recycleTarget == InstanceManager.ShellParsingPath(deleteDirectory) &&
            Directory.Exists(InstanceManager.Extended(deleteDirectory)), "confirmed instance deletion dispatches the normalized path without touching user data");

        // 设置只写测试目录，不触碰用户的全局配置。
        var settings = new SettingsViewModel(model.State, Path.Combine(root, "settings-test.json"))
        { MinecraftRoot = root, SelectedDownloadSource = "官方" };
        await settings.SaveCommand.ExecuteAsync(null);
        check(settings.Message == "已自动保存" && model.Download.TargetInstance is null && model.Instances.SelectedInstance is null, "root/source reconfigure clears stale targets");
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "settings-test.json")));
        check(saved.RootElement.GetProperty("source").GetString() == "Official" && saved.RootElement.GetProperty("rootMode").GetString() == "custom", "source and root mode persisted");
        model.State.Configure(root);
        await model.Instances.RefreshAsync();
        model.Download.SelectedMinecraftChannel = "正式版";
        await model.Download.RefreshVersionsAsync();
        selector.SelectedType = "Fabric"; await selector.PendingRefresh;
        selector.SelectedChannel = "正式版";
    }

    public static async Task<int> OnlineAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var source = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Bmclapi, Console.WriteLine));
            var manifest = await source.ReadAsync(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"),
                16 * 1024 * 1024, bytes => JsonNode.Parse(bytes)!.AsObject(), default);
            Console.WriteLine("Minecraft manifest: " + manifest["versions"]!.AsArray().Count + " versions");
            var catalog = new LoaderVersionCatalog(source);
            foreach (var (mc, kind) in new[] { ("1.20.1", LoaderKind.Fabric), ("1.20.1", LoaderKind.Forge), ("1.21.1", LoaderKind.NeoForge) })
            {
                var versions = await catalog.GetVersionsAsync(mc, kind, default);
                if (versions.Count == 0) throw new Exception(kind + " empty response");
                Console.WriteLine($"{kind} / {mc}: {versions.Count} versions, {versions.Count(x => x.IsStable)} stable, first {versions[0].Version}");
            }
            Console.WriteLine("PASS online metadata checks; no installers or game binaries downloaded.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
