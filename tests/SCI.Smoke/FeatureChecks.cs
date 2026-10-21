using System.Net;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownload.Model;
using McDownload.Service;
using McDownload.ViewModel;

internal static class FeatureChecks
{
    public sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        public bool MirrorFails { get; set; }
        public bool CorruptMirror { get; set; }
        public string? MirrorManifest { get; set; }
        public bool MirrorLoaderInvalid { get; set; }
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

        var installed = await model.State.Instances.CreateAsync(new("Downloaded", "1.20.1", IsolationPolicy.PerInstance, new("Downloaded", null, null)), default);
        before = handler.Requests.Count;
        var game = await model.State.Games.InstallAsync(installed.Id,
            model.Download.MinecraftVersions.Single(), new(Guid.NewGuid(), null, default));
        var paths = await model.State.Instances.ResolvePathsAsync(game.Id, default);
        check(File.Exists(paths.ClientJarFile) && File.Exists(paths.VersionJsonFile) && File.Exists(paths.BaseVersionJsonFile), "game download publishes managed files");
        check(handler.Requests.Skip(before).All(x => x.Host == "bmclapi2.bangbang93.com"), "game metadata/client/libraries/assets use mirror");
        check(File.Exists(Path.Combine(paths.AssetsDirectory, "objects", sha1[..2].ToLowerInvariant(), sha1.ToLowerInvariant())), "game resource cache populated");

        string versionsRoot = Path.Combine(root, "versions");
        foreach (var item in new Dictionary<string, string>
        {
            ["1.20.1"] = """{"id":"1.20.1","mainClass":"net.minecraft.Main"}""",
            ["ExternalFabric"] = """{"id":"ExternalFabric","inheritsFrom":"1.20.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"},{"name":"net.fabricmc:intermediary:1.20.1"}]}""",
            ["ExternalForge"] = """{"id":"ExternalForge","inheritsFrom":"1.20.1","libraries":[{"name":"net.minecraftforge:forge:1.20.1-47.3.0"}]}""",
            ["ExternalNeo"] = """{"id":"ExternalNeo","inheritsFrom":"1.21.1","libraries":[{"name":"net.neoforged:neoforge:21.1.100"}]}""",
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
        check(model.Instances.Instances.Single(x => x.Name == "Broken").Details.Contains("读取失败") && model.Instances.Instances.Any(x => x.Name == "Cycle"), "bad JSON and cyclic inheritance isolated");
        model.Instances.SelectedInstance = external;
        await model.Instances.ManageLoaderCommand.ExecuteAsync(null);
        await model.Instances.DeleteCommand.ExecuteAsync(null);
        await model.Instances.ConfirmDeleteCommand.ExecuteAsync(null);
        check(!model.Instances.IsDeleteConfirmationVisible && model.Download.TargetInstance?.DirectoryName != "ExternalFabric", "external management commands do nothing");
        check(unmanagedBefore.All(x => File.Exists(x.Key) && File.ReadAllBytes(x.Key).SequenceEqual(x.Value)) &&
            !File.Exists(Path.Combine(external.Version.DirectoryPath, "instance.json")), "discovery never rewrites external files");

        // 设置只写测试目录，不触碰用户的全局配置。
        var settings = new SettingsViewModel(model.State, Path.Combine(root, "settings-test.json"))
        { MinecraftRoot = root, SelectedDownloadSource = "官方" };
        await settings.SaveCommand.ExecuteAsync(null);
        check(settings.Message.Contains("已保存") && model.Download.TargetInstance is null && model.Instances.SelectedInstance is null, "root/source reconfigure clears stale targets");
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
