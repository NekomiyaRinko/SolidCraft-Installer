using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownload.Model;
using McDownload.Service;
using McDownload.ViewModel;

internal static class ModpackRecoveryChecks
{
    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(-1, token); return 0; }
    }

    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "modpack-recovery"); Directory.CreateDirectory(folder);
        string pack = Path.Combine(folder, "recovery.mrpack");
        using (var output = File.Create(pack))
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open(), new UTF8Encoding(false)))
                writer.Write("""{"formatVersion":1,"game":"minecraft","name":"Recovery","versionId":"1","dependencies":{"minecraft":"1.20.1"},"files":[]}""");
            using (var writer = new StreamWriter(zip.CreateEntry("overrides/config/recovery.txt").Open(), new UTF8Encoding(false))) writer.Write("original");
        }
        using var http = new HttpClient(new FeatureChecks.Handler());
        var instances = new InstanceManager(Path.Combine(folder, "game"));
        var fetcher = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Official));
        var versions = new VersionResolver(instances, fetcher);
        using var resolver = new CurseForgeFileResolver();
        using var downloads = new ModpackFileDownloader();
        var importer = new ModpackImportService(instances, versions, new(instances, versions, fetcher),
            new LoaderInstaller(instances, versions, fetcher), resolver, downloads);
        OperationContext Context() => new(Guid.NewGuid(), null, default);
        var result = await importer.ImportAsync(new(pack, "Recovery"), Context());
        var paths = await instances.ResolvePathsAsync(result.Instance.Id, default);
        string stateFile = InstanceManager.Child(paths.GameDirectory, ".launcher/modpack/state.json");
        string target = InstanceManager.Child(paths.GameDirectory, "config/recovery.txt");
        string hash = result.State.WrittenFiles["config/recovery.txt"];
        var pending = result.State with { WrittenFiles = new Dictionary<string, string>(),
            Status = ModpackImportStatus.Importing, PendingWrite = new("config/recovery.txt", hash) };
        await File.WriteAllBytesAsync(stateFile, InstanceManager.Serialize(pending));
        result = await importer.ResumeAsync(new(result.Instance.Id), Context());
        check(result.State.Status == ModpackImportStatus.Completed && result.State.PendingWrite is null &&
            result.State.WrittenFiles["config/recovery.txt"] == hash, "resume recovers crash after payload publish before journal completion");

        File.Delete(target);
        await File.WriteAllBytesAsync(stateFile, InstanceManager.Serialize(pending));
        result = await importer.ResumeAsync(new(result.Instance.Id), Context());
        check(result.State.Status == ModpackImportStatus.Completed && await File.ReadAllTextAsync(target) == "original",
            "resume recovers crash before payload publish");

        await File.WriteAllBytesAsync(stateFile, InstanceManager.Serialize(pending));
        await File.WriteAllTextAsync(target, "changed");
        bool conflict = false;
        try { await importer.ResumeAsync(new(result.Instance.Id), Context()); }
        catch (InstanceConflictException) { conflict = true; }
        check(conflict && await File.ReadAllTextAsync(target) == "changed", "pending write recovery cannot overwrite conflicting user data");
        await File.WriteAllTextAsync(target, "original");
        await File.WriteAllBytesAsync(stateFile, InstanceManager.Serialize(pending));

        string lockFile = InstanceManager.Child(paths.GameDirectory, ".launcher/modpack/import.lock");
        await using (var lease = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool locked = false;
            try { await importer.ResumeAsync(new(result.Instance.Id), Context()); }
            catch (IOException) { locked = true; }
            check(locked && (await File.ReadAllBytesAsync(stateFile)).SequenceEqual(InstanceManager.Serialize(pending)),
                "concurrent resume is rejected without rewriting import state");
        }
        string relocated = Path.Combine(folder, "relocated.mrpack"); File.Move(pack, relocated);
        result = await importer.ResumeAsync(new(result.Instance.Id, ArchivePath: relocated), Context());
        check(result.State.Status == ModpackImportStatus.Completed && result.State.ArchivePath == relocated,
            "resume accepts relocated byte-identical source archive");

        string timeoutPack = Path.Combine(folder, "timeout.mrpack");
        byte[] payload = Encoding.UTF8.GetBytes("timeout");
        using (var output = File.Create(timeoutPack))
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
        {
            var json = new JsonObject
            {
                ["formatVersion"] = 1, ["game"] = "minecraft", ["name"] = "Timeout", ["versionId"] = "1",
                ["dependencies"] = new JsonObject { ["minecraft"] = "1.20.1" },
                ["files"] = new JsonArray(new JsonObject
                {
                    ["path"] = "mods/timeout.jar", ["fileSize"] = payload.Length,
                    ["hashes"] = new JsonObject { ["sha1"] = Convert.ToHexString(SHA1.HashData(payload)), ["sha512"] = Convert.ToHexString(SHA512.HashData(payload)) },
                    ["downloads"] = new JsonArray("https://cdn.modrinth.com/timeout")
                })
            };
            using var entry = zip.CreateEntry("modrinth.index.json").Open();
            await entry.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(json));
        }
        using var stalledDownloads = new ModpackFileDownloader(new StalledHandler(), requestTimeout: TimeSpan.FromMilliseconds(30));
        var stalledImporter = new ModpackImportService(instances, versions, new(instances, versions, fetcher),
            new LoaderInstaller(instances, versions, fetcher), resolver, stalledDownloads);
        using var diagnostics = new DiagnosticLog(Path.Combine(folder, "diagnostics"));
        using var workspace = new WorkspaceViewModel(http, diagnostics);
        var timedOut = workspace.EnqueueAsync("stalled-import", context => stalledImporter.ImportAsync(new(timeoutPack, "TimedOut"), context));
        bool followed = false;
        var next = workspace.EnqueueAsync("next", _ => { followed = true; return Task.CompletedTask; });
        check(!await timedOut && await next && followed, "stalled import body times out and serial queue continues");
        var timedOutInstance = (await instances.ListAsync(default)).Single(x => x.DirectoryName == "TimedOut");
        check((await stalledImporter.GetStateAsync(timedOutInstance.Id, default))?.Status == ModpackImportStatus.Failed,
            "stalled import body is persisted as failure rather than user cancellation");
        await using var released = await instances.LockInstanceAsync(timedOutInstance.Id, default);
        check(released.CanWrite, "stalled import body deadline releases instance lock");
    }
}
