using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownload.Model;
using McDownload.Service;
using McDownload.ViewModel;

internal static class ModpackChecks
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("modpack-file-fixture");
    private static readonly byte[] Second = Encoding.UTF8.GetBytes("second-file-fixture");
    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));
    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private sealed class Observer(Action<OperationProgress> action) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => action(value);
    }

    private sealed class Downloads : HttpMessageHandler
    {
        public bool Corrupt { get; set; }
        public bool RedirectToLocal { get; set; }
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            if (RedirectToLocal)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://127.0.0.1/private");
                return Task.FromResult(redirect);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("missing")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            byte[] bytes = Corrupt ? Enumerable.Repeat((byte)'x', Payload.Length).ToArray() :
                request.RequestUri.AbsolutePath.EndsWith("second") ? Second : Payload;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class Api : HttpMessageHandler
    {
        public bool Restricted { get; set; }
        public bool InvalidId { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Website { get; set; } = "https://www.curseforge.com/minecraft/mc-mods/fixture";
        public int ClassId { get; set; } = 6;
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (request.Headers.GetValues("x-api-key").Single() != "fixture-key") throw new Exception("missing API authentication");
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
            JsonObject data = request.RequestUri!.AbsolutePath.Contains("/files/") ? new()
            {
                ["id"] = InvalidId ? 999 : 2, ["modId"] = 1, ["fileName"] = "fixture.jar", ["fileLength"] = Payload.Length,
                ["downloadUrl"] = Restricted ? null : "https://edge.forgecdn.net/fixture",
                ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = Sha1(Payload) })
            } : new()
            {
                ["id"] = 1, ["gameId"] = 432, ["classId"] = ClassId, ["allowModDistribution"] = !Restricted,
                ["links"] = new JsonObject { ["websiteUrl"] = Website }
            };
            return Task.FromResult(FeatureChecks.Handler.Response(new JsonObject { ["data"] = data }.ToJsonString()));
        }
    }

    private sealed class NoFiles : ICurseForgeFileResolver
    {
        public Task<CurseForgeFileResolution> ResolveAsync(ModpackFile file, CancellationToken token) =>
            Task.FromResult(new CurseForgeFileResolution(null, new(file.Key, "fixture unavailable")));
    }

    private static JsonObject Modrinth(bool loader = true) => new()
    {
        ["formatVersion"] = 1, ["game"] = "minecraft", ["name"] = "Fixture Pack", ["versionId"] = "v1",
        ["dependencies"] = loader ? new JsonObject { ["minecraft"] = "1.20.1", ["fabric-loader"] = "0.19.3" }
            : new JsonObject { ["minecraft"] = "1.20.1" },
        ["files"] = new JsonArray()
    };

    private static JsonObject IndexFile(string path, byte[]? data = null, string? environment = null, params string[] downloads)
    {
        data ??= Payload;
        var file = new JsonObject
        {
            ["path"] = path, ["fileSize"] = data.Length,
            ["hashes"] = new JsonObject { ["sha1"] = Sha1(data), ["sha512"] = Convert.ToHexString(SHA512.HashData(data)) },
            ["downloads"] = new JsonArray((downloads.Length == 0 ? new[] { "https://cdn.modrinth.com/fixture" } : downloads)
                .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
        };
        if (environment is not null) file["env"] = new JsonObject { ["client"] = environment, ["server"] = "required" };
        return file;
    }

    private static JsonObject CurseForge(bool forge = false) => new()
    {
        ["manifestType"] = "minecraftModpack", ["manifestVersion"] = 1, ["name"] = "CF Fixture", ["version"] = "1",
        ["minecraft"] = new JsonObject { ["version"] = forge ? "1.12.2" : "1.20.1", ["modLoaders"] = forge
            ? new JsonArray(new JsonObject { ["id"] = "forge-14.23.5.2860", ["primary"] = true }) : new JsonArray() },
        ["overrides"] = "custom", ["files"] = new JsonArray(new JsonObject { ["projectID"] = 1, ["fileID"] = 2, ["required"] = true })
    };

    private static string Pack(string root, string name, string manifest, JsonObject json, params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(root, name);
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        Write(zip, manifest, JsonSerializer.SerializeToUtf8Bytes(json));
        foreach (var entry in entries) Write(zip, entry.Name, Encoding.UTF8.GetBytes(entry.Content));
        return path;
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        using var output = zip.CreateEntry(name).Open(); output.Write(data);
    }

    private static async Task RejectAsync(Func<Task> action, Action<bool, string> check, string name)
    {
        bool rejected = false;
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InstanceValidationException or InstanceConflictException or
            ArgumentException or DownloadIntegrityException or IOException or JsonException or InvalidOperationException) { rejected = true; }
        check(rejected, name);
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "modpack-tests"); Directory.CreateDirectory(directory);
        var reader = new ModpackArchiveReader();
        var manifest = Modrinth();
        ((JsonArray)manifest["files"]!).Add(IndexFile("mods/main.jar", downloads: new[] { "https://cdn.modrinth.com/missing", "https://cdn.modrinth.com/fixture" }));
        ((JsonArray)manifest["files"]!).Add(IndexFile("resourcepacks/optional.zip", environment: "optional"));
        ((JsonArray)manifest["files"]!).Add(IndexFile("mods/server.jar", environment: "unsupported"));
        ((JsonArray)manifest["files"]!).Add(IndexFile("config/layer.txt"));
        string pack = Pack(directory, "fixture.mrpack", "modrinth.index.json", manifest,
            ("overrides/config/layer.txt", "common"), ("client-overrides/config/layer.txt", "client"),
            ("server-overrides/config/layer.txt", "server"), ("overrides/mods/memory_repo/test.jar", "nested"));
        var preview = await reader.ReadAsync(pack, default);
        check(preview.Loader == new LoaderSelection(LoaderKind.Fabric, "0.19.3") && preview.Files.Count == 6 &&
            preview.Files.All(x => x.Path != "mods/server.jar") && preview.Files.Any(x => x.Optional), "mrpack versions, client environment and layers");

        foreach (string path in new[] { "../escape", "C:/escape", "/escape", "mods\\escape.jar", "config/CON", "mods/a:stream", ".launcher/evil", "instance.json", "versions/evil" })
        {
            var invalid = Modrinth(false); ((JsonArray)invalid["files"]!).Add(IndexFile(path));
            string archive = Pack(directory, "invalid.mrpack", "modrinth.index.json", invalid);
            await RejectAsync(() => reader.ReadAsync(archive, default), check, "unsafe manifest path rejected " + path);
        }
        string conflict = Pack(directory, "conflict.mrpack", "modrinth.index.json", Modrinth(false),
            ("overrides/config", "file"), ("overrides/config/child", "child"));
        await RejectAsync(() => reader.ReadAsync(conflict, default), check, "ZIP file-directory conflict rejected");
        string duplicate = Pack(directory, "duplicate.mrpack", "modrinth.index.json", Modrinth(false),
            ("overrides/config/a", "one"), ("overrides/config/A", "two"));
        await RejectAsync(() => reader.ReadAsync(duplicate, default), check, "ZIP case-insensitive duplicate rejected");
        string ambiguous = Pack(directory, "ambiguous.zip", "modrinth.index.json", Modrinth(false), ("manifest.json", "{}"));
        await RejectAsync(() => reader.ReadAsync(ambiguous, default), check, "ambiguous archive rejected");
        var quilt = Modrinth(false); quilt["dependencies"]!["quilt-loader"] = "0.1";
        await RejectAsync(() => reader.ReadAsync(Pack(directory, "quilt.zip", "modrinth.index.json", quilt), default), check, "unsupported loader rejected");
        string symlink = Pack(directory, "symlink.zip", "modrinth.index.json", Modrinth(false));
        using (var zip = ZipFile.Open(symlink, ZipArchiveMode.Update))
        { var entry = zip.CreateEntry("overrides/link"); entry.ExternalAttributes = unchecked((int)0xA1FF0000); }
        await RejectAsync(() => reader.ReadAsync(symlink, default), check, "symlink ZIP entry rejected");

        var gameHandler = new FeatureChecks.Handler();
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> custom = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/profile/json")) return Task.FromResult(FeatureChecks.Handler.Response("""
                {"inheritsFrom":"1.20.1","mainClass":"net.fabricmc.loader.Main","libraries":[{"name":"net.fabricmc:fabric-loader:0.19.3","downloads":{"artifact":{"path":"net/fabricmc/fabric-loader/0.19.3/fabric-loader-0.19.3.jar","url":"https://maven.fabricmc.net/fixture.jar","sha1":"HASH"}}}]}
                """.Replace("HASH", Sha1(Jar()))));
            if (request.RequestUri.AbsolutePath == "/fixture.jar") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jar()) });
            if (request.RequestUri.AbsolutePath.EndsWith(".sha1")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            throw new Exception("Unexpected fixture override: " + request.RequestUri);
        };
        // 让游戏元数据仍由原有离线 handler 处理。
        using var gameHttp = new HttpClient(new Routing(gameHandler, custom));
        var instances = new InstanceManager(Path.Combine(directory, "game"));
        var fetcher = new HmclFetchTask(gameHttp, new HmclDownloadProvider(DownloadSource.Official));
        var versions = new VersionResolver(instances, fetcher);
        var games = new GameDownloadService(instances, versions, fetcher);
        var loaders = new LoaderInstaller(instances, versions, fetcher);
        var downloadHandler = new Downloads();
        using var downloads = new ModpackFileDownloader(downloadHandler);
        using var noKey = new CurseForgeFileResolver();
        var importer = new ModpackImportService(instances, versions, games, loaders, noKey, downloads);
        var progress = new List<OperationProgress>();
        OperationContext Context(CancellationToken token = default) => new(Guid.NewGuid(), new Observer(progress.Add), token);
        var result = await importer.ImportAsync(new(pack, "Imported"), Context());
        var paths = await instances.ResolvePathsAsync(result.Instance.Id, default);
        check(result.State.Status == ModpackImportStatus.Completed && result.Instance.InstalledLoader == preview.Loader, "mrpack full backend import with real Fabric installer path");
        check(await File.ReadAllTextAsync(InstanceManager.Child(paths.GameDirectory, "config/layer.txt")) == "client", "client override wins without downloading shadowed file");
        check(File.Exists(InstanceManager.Child(paths.ModsDirectory, "memory_repo/test.jar")) &&
            !File.Exists(InstanceManager.Child(paths.GameDirectory, "resourcepacks/optional.zip")), "nested bundled files retained and optional default off");
        check(downloadHandler.Requests.Count == 2 && progress.FindLastIndex(x => x.Step == OperationStep.Saving) >
            progress.FindLastIndex(x => x.Step == OperationStep.Overrides), "candidate fallback and final saving after overrides");
        int requests = gameHandler.Requests.Count;
        var resumed = await importer.ResumeAsync(new(result.Instance.Id), Context());
        check(resumed.State.Status == ModpackImportStatus.Completed && gameHandler.Requests.Count == requests, "completed import resumes without reinstalling loaded game");
        await File.WriteAllTextAsync(InstanceManager.Child(paths.GameDirectory, "config/layer.txt"), "user-config");
        await RejectAsync(() => importer.ResumeAsync(new(result.Instance.Id), Context()), check, "resume protects user-modified configuration");
        check(await File.ReadAllTextAsync(InstanceManager.Child(paths.GameDirectory, "config/layer.txt")) == "user-config", "user config unchanged after conflict");
        await RejectAsync(() => importer.ImportAsync(new(pack, "Imported"), Context()), check, "existing instance not overwritten");
        await RejectAsync(() => importer.ImportAsync(new(pack, "DigestMismatch", ExpectedArchiveSha256: new string('0', 64)), Context()), check, "preview digest change rejected before instance creation");
        check(!(await instances.ListAsync(default)).Any(x => x.DirectoryName == "DigestMismatch"), "digest rejection has no instance side effect");

        var resumeManifest = Modrinth(false);
        ((JsonArray)resumeManifest["files"]!).Add(IndexFile("mods/first.jar"));
        ((JsonArray)resumeManifest["files"]!).Add(IndexFile("mods/second.jar", Second, downloads: new[] { "https://cdn.modrinth.com/second" }));
        string resumePack = Pack(directory, "resume.mrpack", "modrinth.index.json", resumeManifest, ("overrides/config/default.txt", "default"));
        using var canceled = new CancellationTokenSource();
        var cancelContext = new OperationContext(Guid.NewGuid(), new Observer(p =>
        { if (p.CurrentItem == "文件已校验：mods/first.jar") canceled.Cancel(); }), canceled.Token);
        bool wasCanceled = false;
        try { await importer.ImportAsync(new(resumePack, "Canceled"), cancelContext); }
        catch (OperationCanceledException) { wasCanceled = true; }
        var canceledInstance = (await instances.ListAsync(default)).Single(x => x.DirectoryName == "Canceled");
        check(wasCanceled && (await importer.GetStateAsync(canceledInstance.Id, default))?.Status == ModpackImportStatus.Canceled, "cancellation saved with completed files");
        int firstRequests = downloadHandler.Requests.Count(x => x.AbsolutePath == "/fixture");
        result = await importer.ResumeAsync(new(canceledInstance.Id), Context());
        check(result.State.Status == ModpackImportStatus.Completed && firstRequests == downloadHandler.Requests.Count(x => x.AbsolutePath == "/fixture"), "resume only downloads unfinished files");

        var optional = await importer.ImportAsync(new(pack, "Optional", SelectedOptionalFiles: new[] { "file:resourcepacks/optional.zip" }), Context());
        check(optional.State.WrittenFiles.ContainsKey("resourcepacks/optional.zip"), "selected optional file installed");
        string reserved = Pack(directory, "reserved.mrpack", "modrinth.index.json", Modrinth(false), ("overrides/Reserved.json", "bad"));
        await RejectAsync(() => importer.ImportAsync(new(reserved, "Reserved"), Context()), check, "instance-specific version JSON protected before creation");

        string cfPack = Pack(directory, "cf.zip", "manifest.json", CurseForge(), ("custom/scripts/example.zs", "script"));
        int beforeNoKey = gameHandler.Requests.Count;
        await RejectAsync(() => importer.ImportAsync(new(cfPack, "CFNoKey"), Context()), check, "CurseForge without key fails before creation");
        check(!(await instances.ListAsync(default)).Any(x => x.DirectoryName == "CFNoKey") && gameHandler.Requests.Count == beforeNoKey,
            "missing credential preflight creates no instance and downloads no game files");
        var legacy = await instances.CreateAsync(new("CFLegacy", "1.20.1", IsolationPolicy.PerInstance, new("CFLegacy", null, null)), default);
        var cfDefinition = await reader.ReadAsync(cfPack, default);
        var legacyPaths = await instances.ResolvePathsAsync(legacy.Id, default);
        var legacyState = new ModpackImportState(1, legacy.Id, legacy.DirectoryName, cfPack, cfDefinition.ArchiveSha256,
            cfDefinition.Name, cfDefinition.Version, Array.Empty<string>(), ModpackImportStatus.NeedsFiles, false, false,
            new Dictionary<string, ModpackFile>(), new Dictionary<string, string>(),
            new[] { new MissingModpackFile("curseforge:1:2", "未配置", new Uri("https://www.curseforge.com/minecraft/mc-mods/1/files/2")) });
        await InstanceManager.AtomicBytesAsync(InstanceManager.Child(legacyPaths.GameDirectory, ".launcher/modpack/state.json"), InstanceManager.Serialize(legacyState), default);
        var api = new Api { Restricted = true };
        using var resolver = new CurseForgeFileResolver(() => "fixture-key", api);
        var cfImporter = new ModpackImportService(instances, versions, games, loaders, resolver, downloads);
        result = await cfImporter.ResumeAsync(new(legacy.Id), Context());
        check(result.State.Status == ModpackImportStatus.NeedsFiles && result.State.ResolvedFiles["curseforge:1:2"].Downloads.Count == 0 &&
            result.State.MissingFiles.Single().Website!.AbsoluteUri.EndsWith("/fixture/files/2"), "restricted CF file has verified metadata and manual page");
        var manualPage = result.State.MissingFiles.Single().Website;
        result = await cfImporter.ResumeAsync(new(result.Instance.Id), Context());
        result = await cfImporter.ResumeAsync(new(result.Instance.Id), Context());
        check(result.State.MissingFiles.Single().Website == manualPage, "restricted file download page persists across repeated resumes");
        string local = Path.Combine(directory, "manual.jar");
        await File.WriteAllBytesAsync(local, Enumerable.Repeat((byte)'x', Payload.Length).ToArray());
        await RejectAsync(() => cfImporter.ResumeAsync(new(result.Instance.Id, LocalFiles: new Dictionary<string, string>
            { ["curseforge:1:2"] = local }), Context()), check, "incorrect manual CF file rejected");
        var failedManual = await cfImporter.GetStateAsync(result.Instance.Id, default);
        check(failedManual?.Status == ModpackImportStatus.Failed && failedManual.MissingFiles.Single().Website == manualPage,
            "manual verification failure preserves missing file page in persisted state");
        result = await cfImporter.ResumeAsync(new(result.Instance.Id), Context());
        check(result.State.MissingFiles.Single().Website == manualPage, "resume after manual verification failure preserves file page");
        using (var source = new CancellationTokenSource())
        {
            var context = new OperationContext(Guid.NewGuid(), new Observer(p =>
            { if (p.Step == OperationStep.Loader) source.Cancel(); }), source.Token);
            try { await cfImporter.ResumeAsync(new(result.Instance.Id), context); }
            catch (OperationCanceledException) { }
            var canceledManual = await cfImporter.GetStateAsync(result.Instance.Id, default);
            check(canceledManual?.Status == ModpackImportStatus.Canceled && canceledManual.MissingFiles.Single().Website == manualPage,
                "canceled manual resume preserves missing file page");
        }
        await File.WriteAllBytesAsync(local, Payload);
        result = await cfImporter.ResumeAsync(new(result.Instance.Id, LocalFiles: new Dictionary<string, string> { ["curseforge:1:2"] = local }), Context());
        check(result.State.Status == ModpackImportStatus.Completed && result.State.WrittenFiles.ContainsKey("scripts/example.zs"), "manual CF completion verifies hashes and custom overrides");
        string shadowPack = Pack(directory, "shadow-cf.zip", "manifest.json", CurseForge(), ("custom/mods/fixture.jar", "bundled"));
        int shadowRequests = downloadHandler.Requests.Count;
        var shadowed = await cfImporter.ImportAsync(new(shadowPack, "CFShadowed"), Context());
        check(shadowed.State.Status == ModpackImportStatus.Completed && shadowed.State.MissingFiles.Count == 0 &&
            downloadHandler.Requests.Count == shadowRequests, "bundled override of restricted CF file completes on first import without download");
        api.Restricted = false;
        var automatic = await cfImporter.ImportAsync(new(cfPack, "CFAuto"), Context());
        check(automatic.State.Status == ModpackImportStatus.Completed && downloadHandler.Requests.Any(x => x.Host == "edge.forgecdn.net"), "CurseForge API resolves and downloads exact file");
        api.InvalidId = true;
        await RejectAsync(() => cfImporter.ImportAsync(new(cfPack, "CFWrongId"), Context()), check, "CF wrong file identity rejected");
        check(!(await instances.ListAsync(default)).Any(x => x.DirectoryName == "CFWrongId"), "invalid metadata is rejected before instance creation");
        api.InvalidId = false;
        var reference = cfDefinition.Files.First(x => x.CurseForge is not null);
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            api.Status = status;
            bool rejectedApi = false;
            try { await cfImporter.ImportAsync(new(cfPack, "CFStatus" + (int)status), Context()); }
            catch (HttpRequestException ex) { rejectedApi = ex.StatusCode == status && ex.Message.Contains(((int)status).ToString()); }
            check(rejectedApi && !(await instances.ListAsync(default)).Any(x => x.DirectoryName == "CFStatus" + (int)status),
                "API failure remains distinct and precedes instance creation " + status);
        }
        api.Status = HttpStatusCode.NotFound;
        var unavailable = await resolver.ResolveAsync(reference, default);
        check(unavailable.File is null && unavailable.Missing is { Website: null } issue404 && issue404.Reason.Contains("不可访问"),
            "404 file metadata cannot manufacture a website or claim an authentication failure");
        api.Status = HttpStatusCode.OK; api.Restricted = true;
        api.ClassId = 12; api.Website = "https://www.curseforge.com/minecraft/texture-packs/fixture";
        var resource = await resolver.ResolveAsync(reference, default);
        check(resource.File?.Path == "resourcepacks/fixture.jar" && resource.Missing?.Website?.AbsoluteUri == api.Website + "/files/2",
            "resource pack uses API-provided category and file website");
        foreach (string website in new[] { "https://www.curseforge.com/minecraft/mc-mods/1", "https://evil.example/minecraft/mc-mods/fixture",
            "https://www.curseforge.com:444/minecraft/mc-mods/fixture", "https://user@www.curseforge.com/minecraft/mc-mods/fixture",
            "https://www.curseforge.com/minecraft/mc-mods/fixture?key=secret" })
        {
            api.Website = website;
            var invalidPage = await resolver.ResolveAsync(reference, default);
            check(invalidPage.File is not null && invalidPage.Missing?.Website is null, "unsafe or numeric website rejected " + website);
        }

        downloadHandler.Corrupt = true;
        var file = new ModpackFile("fixture", "mods/test.jar", Payload.Length, new(Sha1(Payload)), new[] { new Uri("https://cdn.modrinth.com/fixture") });
        string target = Path.Combine(directory, "download-target"); await File.WriteAllTextAsync(target, "existing");
        await RejectAsync(() => downloads.DownloadAsync(file, target, default), check, "corrupt download rejected");
        check(await File.ReadAllTextAsync(target) == "existing", "failed integrity check preserves previous download target");
        downloadHandler.Corrupt = false; downloadHandler.RedirectToLocal = true;
        int before = downloadHandler.Requests.Count;
        await RejectAsync(() => downloads.DownloadAsync(file, target, default), check, "untrusted redirect rejected before request");
        check(downloadHandler.Requests.Count == before + 1, "redirect cannot contact loopback address");

        await CheckLegacyForgeAsync(directory, downloads, check);
        string samples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../modpack_test"));
        if (Directory.Exists(samples))
        {
            var bmc = await reader.ReadAsync(Path.Combine(samples, "BMC2 [FABRIC] 1.20.1 v40.mrpack"), default);
            var rl = await reader.ReadAsync(Path.Combine(samples, "RLCraft 1.12.2 - Release v2.9.3.zip"), default);
            check(bmc.MinecraftVersion == "1.20.1" && bmc.Loader == new LoaderSelection(LoaderKind.Fabric, "0.19.3") &&
                bmc.Files.Count(x => x.ArchiveEntry is null) == 336, "real BMC2 sample parsed without network");
            check(rl.MinecraftVersion == "1.12.2" && rl.Loader == new LoaderSelection(LoaderKind.Forge, "14.23.5.2860") &&
                rl.Files.Count(x => x.CurseForge is not null) == 187, "real RLCraft sample parsed without network");
        }
    }

    private sealed class Routing(FeatureChecks.Handler handler,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> custom) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(handler);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/profile/json") || request.RequestUri.AbsolutePath == "/fixture.jar" ||
                request.RequestUri.AbsolutePath.EndsWith(".sha1")) return await custom(request, token);
            return await _inner.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private static byte[] Jar()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) Write(zip, "fixture.txt", Payload);
        return output.ToArray();
    }

    private sealed class LegacyHandler(byte[] installer) : HttpMessageHandler
    {
        private readonly byte[] _version = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["id"] = "1.12.2", ["mainClass"] = "net.minecraft.Main", ["type"] = "release", ["libraries"] = new JsonArray(),
            ["downloads"] = new JsonObject { ["client"] = FeatureChecks.Handler.Descriptor("https://piston-data.mojang.com/client.jar", Payload) }
        });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("version_manifest_v2.json")) return Task.FromResult(FeatureChecks.Handler.Response(new JsonObject
            {
                ["latest"] = new JsonObject { ["release"] = "1.12.2", ["snapshot"] = "1.12.2" },
                ["versions"] = new JsonArray(new JsonObject { ["id"] = "1.12.2", ["type"] = "release", ["url"] = "https://piston-meta.mojang.com/legacy.json", ["sha1"] = Sha1(_version) })
            }.ToJsonString()));
            byte[] data = path.EndsWith("legacy.json") ? _version : path.EndsWith(".sha1") ? Encoding.ASCII.GetBytes(Sha1(installer)) :
                path.EndsWith("-installer.jar") ? installer : Payload;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }

    private static async Task CheckLegacyForgeAsync(string root, ModpackFileDownloader downloads, Action<bool, string> check)
    {
        byte[] universal = Jar();
        byte[] installer;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                Write(zip, "universal.jar", universal);
                Write(zip, "install_profile.json", JsonSerializer.SerializeToUtf8Bytes(new JsonObject
                {
                    ["install"] = new JsonObject { ["minecraft"] = "1.12.2", ["path"] = "net.minecraftforge:forge:1.12.2-14.23.5.2860", ["filePath"] = "universal.jar" },
                    ["versionInfo"] = new JsonObject
                    {
                        ["id"] = "1.12.2-forge-14.23.5.2860", ["inheritsFrom"] = "1.12.2", ["mainClass"] = "net.minecraft.launchwrapper.Launch",
                        ["minecraftArguments"] = "--tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker",
                        ["libraries"] = new JsonArray(new JsonObject { ["name"] = "net.minecraftforge:forge:1.12.2-14.23.5.2860" })
                    }
                }));
            }
            installer = stream.ToArray();
        }
        using var http = new HttpClient(new LegacyHandler(installer));
        var instances = new InstanceManager(Path.Combine(root, "legacy-game"));
        var fetcher = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Official));
        var versions = new VersionResolver(instances, fetcher);
        var importer = new ModpackImportService(instances, versions, new(instances, versions, fetcher),
            new LoaderInstaller(instances, versions, fetcher), new NoFiles(), downloads);
        var json = CurseForge(true); json["files"] = new JsonArray();
        string pack = Pack(root, "legacy.zip", "manifest.json", json, ("custom/config/fixture.cfg", "legacy"));
        var result = await importer.ImportAsync(new(pack, "LegacyForge"), new(Guid.NewGuid(), null, default));
        check(result.State.Status == ModpackImportStatus.Completed && result.Instance.InstalledLoader == new LoaderSelection(LoaderKind.Forge, "14.23.5.2860"),
            "Forge 1.12.2 / 14.23.5.2860 legacy installer fixture imported end-to-end without Java");
        string library = Path.Combine(root, "legacy-game/libraries/net/minecraftforge/forge/1.12.2-14.23.5.2860/forge-1.12.2-14.23.5.2860.jar");
        check(File.Exists(library) && Sha256(await File.ReadAllBytesAsync(library)) == Sha256(universal), "legacy universal library preserved and published");
    }
}
