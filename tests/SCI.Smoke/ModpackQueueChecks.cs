using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using McDownload.Model;
using McDownload.Service;
using McDownload.ViewModel;

internal static class ModpackQueueChecks
{
    private static void Pack(string path, bool curseforge, string content = "first")
    {
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        string manifest = curseforge ? """
            {"manifestType":"minecraftModpack","manifestVersion":1,"name":"QueueCF","version":"1",
            "minecraft":{"version":"1.20.1","modLoaders":[]},"overrides":"overrides",
            "files":[{"projectID":1,"fileID":2,"required":true}]}
            """ : """
            {"formatVersion":1,"game":"minecraft","name":"QueuePack","versionId":"1",
            "dependencies":{"minecraft":"1.20.1"},"files":[]}
            """;
        using (var writer = new StreamWriter(zip.CreateEntry(curseforge ? "manifest.json" : "modrinth.index.json").Open(), Encoding.UTF8))
            writer.Write(manifest);
        using (var writer = new StreamWriter(zip.CreateEntry("overrides/config/queue.txt").Open(), Encoding.UTF8)) writer.Write(content);
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "modpack-queue"); Directory.CreateDirectory(folder);
        using var http = new HttpClient(new FeatureChecks.Handler());
        using var diagnostics = new DiagnosticLog(Path.Combine(folder, "diagnostics"));
        using var workspace = new WorkspaceViewModel(http, diagnostics);
        workspace.Configure(Path.Combine(folder, "game"), DownloadSource.Official);
        string pack = Path.Combine(folder, "queue.mrpack"); Pack(pack, false);
        var preview = await workspace.Modpacks.PreviewAsync(pack, default);
        bool rejected = false;
        try { await workspace.EnqueueModpackAsync(new(pack, "Unpinned")); }
        catch (ArgumentException) { rejected = true; }
        check(rejected && workspace.Tasks.Count == 0, "modpack queue requires a pinned preview digest");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = workspace.EnqueueAsync("hold", _ => hold.Task);
        var selection = new List<string>();
        var pending = workspace.EnqueueModpackAsync(new(pack, "Snapshot", SelectedOptionalFiles: selection,
            ExpectedArchiveSha256: preview.ArchiveSha256));
        selection.Add("unknown-mutated-after-submit");
        check(workspace.Tasks.Count(x => x.IsPending) == 1, "modpack task waits behind existing queue item");
        hold.SetResult(); await blocking;
        check(await pending && workspace.Tasks.Last().State == QueuedTaskState.Completed, "modpack queue snapshots mutable optional selection");

        hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        blocking = workspace.EnqueueAsync("hold-again", _ => hold.Task);
        pending = workspace.EnqueueModpackAsync(new(pack, "ChangedAfterSubmit", ExpectedArchiveSha256: preview.ArchiveSha256));
        Pack(pack, false, "changed"); hold.SetResult(); await blocking;
        check(!await pending && !(await workspace.Instances.ListAsync(default)).Any(x => x.DirectoryName == "ChangedAfterSubmit"),
            "source archive changed while queued cannot create an instance");

        string cf = Path.Combine(folder, "queue.zip"); Pack(cf, true);
        var cfPreview = await workspace.Modpacks.PreviewAsync(cf, default);
        bool finished = await workspace.EnqueueModpackAsync(new(cf, "MissingCF", ExpectedArchiveSha256: cfPreview.ArchiveSha256));
        check(!finished && workspace.Tasks.Last().State == QueuedTaskState.Failed &&
            !(await workspace.Instances.ListAsync(default)).Any(x => x.DirectoryName == "MissingCF"),
            "queued CF preflight rejects missing key without creating an instance");
        check(await workspace.EnqueueAsync("next-after-preflight-failure", _ => Task.CompletedTask), "queue accepts new work after missing-key preflight failure");
    }
}
