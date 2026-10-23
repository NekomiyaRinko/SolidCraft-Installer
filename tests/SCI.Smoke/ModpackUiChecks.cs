using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using McDownload.Model;
using McDownload.Service;
using McDownload.View;
using McDownload.ViewModel;

internal static class ModpackUiChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
    private static string Pack(string root, string name, bool curseforge = false)
    {
        string path = Path.Combine(root, name + (curseforge ? ".zip" : ".mrpack"));
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        JsonObject json = curseforge ? new()
        {
            ["manifestType"] = "minecraftModpack", ["manifestVersion"] = 1, ["name"] = name, ["version"] = "1",
            ["minecraft"] = new JsonObject { ["version"] = "1.20.1", ["modLoaders"] = new JsonArray() },
            ["overrides"] = "overrides", ["files"] = new JsonArray(new JsonObject { ["projectID"] = 1, ["fileID"] = 2, ["required"] = true })
        } : new()
        {
            ["formatVersion"] = 1, ["game"] = "minecraft", ["name"] = name, ["versionId"] = "v1",
            ["dependencies"] = new JsonObject { ["minecraft"] = "1.20.1" },
            ["files"] = new JsonArray(new JsonObject
            {
                ["path"] = "mods/optional.jar", ["fileSize"] = 1,
                ["hashes"] = new JsonObject { ["sha1"] = new string('0', 40), ["sha512"] = new string('0', 128) },
                ["downloads"] = new JsonArray("https://cdn.modrinth.com/optional"),
                ["env"] = new JsonObject { ["client"] = "optional" }
            })
        };
        using (var writer = new StreamWriter(zip.CreateEntry(curseforge ? "manifest.json" : "modrinth.index.json").Open(), new UTF8Encoding(false)))
            writer.Write(json.ToJsonString());
        using (var writer = new StreamWriter(zip.CreateEntry("overrides/config/test.txt").Open(), new UTF8Encoding(false))) writer.Write("config");
        return path;
    }
    private static DragEventArgs Drag(IDataObject data, RoutedEvent routedEvent, DependencyObject target)
    {
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { data, DragDropKeyStates.None, DragDropEffects.Copy, target, new Point(100, 100) }, null)!;
        args.RoutedEvent = routedEvent;
        return args;
    }
    public static async Task RunAsync(MainWindow window, MainWindowViewModel model, string root, Action<bool, string> check,
        Action<string, Window?> capture)
    {
        string folder = Path.Combine(root, "modpack-ui"); Directory.CreateDirectory(folder);
        string pack = Pack(folder, "UI Pack"), other = Pack(folder, "Other Pack"), cf = Pack(folder, "UI CF", true);
        var vm = model.Download.Modpacks;
        var download = Find<DownloadView>(window).Single();
        var home = (Border)download.FindName("ModpackHome");
        model.CurrentPage = "Download"; model.Download.SelectModpackTabCommand.Execute(null); window.UpdateLayout();
        check(model.Download.IsModpackTab && home.IsVisible && !model.Download.IsPageBusy, "download page exposes import tab and dashed drop homepage");
        capture("SCI-modpack-home", null);
        var data = new DataObject(DataFormats.FileDrop, new[] { pack });
        foreach (string page in new[] { "Instances", "Download", "Settings", "About", "Tasks", "Loaders", "Licenses", "ProjectLicense", "ModpackResume" })
        {
            model.CurrentPage = page; window.UpdateLayout();
            var drag = Drag(data, DragDrop.PreviewDragOverEvent, window);
            window.RaiseEvent(drag);
            check(drag.Handled && drag.Effects == DragDropEffects.Copy && ((Grid)window.FindName("ModpackDragOverlay")).IsVisible,
                "entire window accepts pack drag from " + page);
            window.RaiseEvent(Drag(data, DragDrop.PreviewDropEvent, window));
            await window.DropImport;
            check(model.CurrentPage == "Download" && model.Download.IsModpackTab && vm.PackName == "UI Pack" &&
                ((Grid)window.FindName("ModpackDragOverlay")).Visibility == Visibility.Collapsed, "drop opens parsed import view from " + page);
        }
        check(vm.MinecraftVersion == "1.20.1" && vm.OptionalFiles.Count == 1 && !vm.OptionalFiles[0].IsSelected && vm.CanSubmit,
            "real archive preview supplies fixed version and optional selection");
        var input = (TextBox)download.FindName("ModpackInstanceName");
        var textDrag = Drag(new DataObject(DataFormats.UnicodeText, "dragged text"), DragDrop.PreviewDragOverEvent, window);
        window.RaiseEvent(textDrag);
        check(!textDrag.Handled, "global drag-over handler leaves non-file data unhandled");
        string original = input.Text;
        input.RaiseEvent(Drag(data, DragDrop.PreviewDropEvent, input)); await window.DropImport;
        check(input.Text == original && vm.PackName == "UI Pack", "window preview drop intercepts file drops over editable textboxes");
        bool textReachedInput = false;
        DragEventHandler observeText = (_, e) => { textReachedInput = true; e.Handled = true; };
        input.AddHandler(DragDrop.PreviewDropEvent, observeText);
        input.RaiseEvent(Drag(new DataObject(DataFormats.UnicodeText, "dragged text"), DragDrop.PreviewDropEvent, input));
        input.RemoveHandler(DragDrop.PreviewDropEvent, observeText);
        check(textReachedInput, "global drop handler leaves text drags to the target control");
        model.Download.IsLoaderConfirmationVisible = true;
        var modalDrag = Drag(data, DragDrop.PreviewDragOverEvent, window); window.RaiseEvent(modalDrag);
        check(modalDrag.Effects == DragDropEffects.None, "loader confirmation gates file drag");
        model.Download.IsLoaderConfirmationVisible = false;
        model.CurrentPage = "Settings";
        model.Settings.IsLogDeleteConfirmationVisible = true;
        window.RaiseEvent(Drag(data, DragDrop.PreviewDropEvent, window));
        check(model.CurrentPage == "Settings", "modal delete state prevents drop navigation");
        model.Settings.IsLogDeleteConfirmationVisible = false;
        // 清除已排队但尚未显示的对话框回调。
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var unsupported = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(folder, "a.txt") });
        var rejected = Drag(unsupported, DragDrop.PreviewDragOverEvent, window); window.RaiseEvent(rejected);
        check(rejected.Effects == DragDropEffects.None, "unsupported extension rejected during drag");
        await model.ImportDroppedFilesAsync(new[] { pack, other });
        check(vm.IsHome && vm.HasError && !vm.CanSubmit, "multiple dropped files never partially import");
        string invalid = Path.Combine(folder, "invalid.zip"); await File.WriteAllTextAsync(invalid, "not a ZIP");
        await model.ImportDroppedFilesAsync(new[] { invalid });
        check(vm.IsHome && vm.HasError && !vm.CanSubmit, "invalid archive returns actionable error without stale preview");
        var first = vm.LoadFilesAsync(new[] { pack });
        var second = vm.LoadFilesAsync(new[] { other });
        await Task.WhenAll(first, second);
        check(vm.PackName == "Other Pack" && !vm.IsReading, "new drop wins over superseded asynchronous preview");
        var stale = vm.LoadFilesAsync(new[] { pack }); vm.Reset(); await stale;
        check(vm.IsHome && !vm.HasPack && !vm.IsReading, "canceling preview prevents late result from restoring details");
        await model.ImportDroppedFilesAsync(new[] { pack });
        vm.InstanceName = "CON"; check(!vm.CanSubmit && vm.HasNameError, "reserved Windows name rejected before queueing");
        vm.InstanceName = "Survival"; check(!vm.CanSubmit, "existing instance name rejected before queueing");
        vm.InstanceName = "UIQueued";
        window.UpdateLayout(); capture("SCI-modpack-preview", null);
        window.Width = 820; window.Height = 620; window.UpdateLayout(); capture("SCI-modpack-preview-small", null);
        check(input.ActualWidth > 250 && ((ScrollViewer)download.FindName("DownloadContent")).ScrollableHeight > 0,
            "import details remain scrollable at minimum window size");
        window.Width = 1120; window.Height = 780;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = model.State.EnqueueAsync("UI hold", _ => release.Task);
        try
        {
            vm.SubmitCommand.Execute(null);
            window.UpdateLayout();
            check(vm.IsHome && home.IsVisible && vm.HasMessage && vm.ArchivePath.Length == 0 && vm.OptionalFiles.Count == 0 &&
                model.CurrentPage == "Download" && !vm.LastSubmission.IsCompleted, "queue submission immediately resets to drop homepage before download finishes");
            capture("SCI-modpack-queued-home", null);
            await vm.LoadFilesAsync(new[] { pack }); vm.InstanceName = "UIQueued";
            check(!vm.CanSubmit && vm.HasNameError, "same pending instance name cannot be submitted twice");
        }
        finally { release.TrySetResult(); await blocker; }
        check(await vm.LastSubmission, "UI import submission completes through real offline backend");
        await model.BackgroundRefresh; await model.Instances.RefreshAsync();
        var item = model.Instances.Instances.Single(x => x.Metadata?.DirectoryName == "UIQueued");
        check(item.HasModpack && !item.CanResumeModpack && item.ModpackStatus.Contains("已完成"), "instance list reads completed import sidecar");

        await model.ImportDroppedFilesAsync(new[] { cf }); vm.InstanceName = "UICurseForge";
        int beforeCF = model.State.Tasks.Count;
        await vm.SubmitCommand.ExecuteAsync(null);
        check(vm.HasPack && vm.Error.Contains("API key") && model.State.Tasks.Count == beforeCF &&
            !(await model.State.Instances.ListAsync(default)).Any(x => x.DirectoryName == "UICurseForge"),
            "CF UI preflight preserves preview and rejects missing key before queue or instance creation");
        var legacy = await model.State.Instances.CreateAsync(new("UICurseForge", "1.20.1", IsolationPolicy.PerInstance, new("UICurseForge", null, null)), default);
        var legacyPaths = await model.State.Instances.ResolvePathsAsync(legacy.Id, default);
        var legacyState = new ModpackImportState(1, legacy.Id, legacy.DirectoryName, cf, vm.Definition!.ArchiveSha256,
            vm.Definition.Name, vm.Definition.Version, Array.Empty<string>(), ModpackImportStatus.NeedsFiles, false, false,
            new Dictionary<string, ModpackFile>(), new Dictionary<string, string>(), new[] { new MissingModpackFile("curseforge:1:2", "未配置 API key",
                new Uri("https://www.curseforge.com/minecraft/mc-mods/1/files/2")) });
        await InstanceManager.AtomicBytesAsync(InstanceManager.Child(legacyPaths.GameDirectory, ".launcher/modpack/state.json"), InstanceManager.Serialize(legacyState), default);
        await model.BackgroundRefresh; await model.Instances.RefreshAsync();
        item = model.Instances.Instances.Single(x => x.Metadata?.DirectoryName == "UICurseForge");
        model.Instances.SelectedInstance = item;
        check(item.CanResumeModpack && item.ModpackStatus.Contains("待补齐"), "incomplete pack exposes resume in instance details");
        await model.Instances.ResumeModpackCommand.ExecuteAsync(null); window.UpdateLayout();
        var resume = model.ModpackResume;
        check(model.CurrentPage == "ModpackResume" && resume.MissingFiles.Count == 1 && !resume.MissingFiles[0].CanChoose && !resume.MissingFiles[0].CanOpenWebsite,
            "resume page loads unresolved CF references without accepting unverified local files");
        capture("SCI-modpack-missing", null);
        var metadata = item.Metadata!;
        var state = (await model.State.Modpacks.GetStateAsync(metadata.Id, default))!;
        byte[] payload = Encoding.UTF8.GetBytes("ui-local-mod");
        string local = Path.Combine(folder, "manual.jar"); await File.WriteAllBytesAsync(local, payload);
        var descriptor = new ModpackFile("curseforge:1:2", "mods/local.jar", payload.Length,
            new(Convert.ToHexString(SHA1.HashData(payload))), Array.Empty<Uri>(), CurseForge: new(1, 2));
        state = state with { ResolvedFiles = new Dictionary<string, ModpackFile> { [descriptor.Key] = descriptor },
            MissingFiles = new[] { new MissingModpackFile(descriptor.Key, "手动下载", new Uri("https://www.curseforge.com/minecraft/mc-mods/fixture/files/2")) } };
        var paths = await model.State.Instances.ResolvePathsAsync(metadata.Id, default);
        await File.WriteAllBytesAsync(InstanceManager.Child(paths.GameDirectory, ".launcher/modpack/state.json"), InstanceManager.Serialize(state));
        await resume.LoadAsync(metadata); resume.MissingFiles[0].LocalPath = local;
        check(resume.MissingFiles[0].CanChoose && resume.MissingFiles[0].CanOpenWebsite &&
            resume.MissingFiles[0].SelectionStatus.Contains("待校验"), "manual file selection exposes only verified-descriptor completion and safe website");
        capture("SCI-modpack-manual", null);
        resume.ArchivePath = other;
        await resume.ResumeCommand.ExecuteAsync(null);
        check(resume.HasError && resume.Error.Contains("相同") && resume.ArchivePath == other &&
            resume.MissingFiles.Single().LocalPath == local && !resume.IsQueued,
            "wrong source digest failure stays visible and preserves selected source and manual files");
        resume.ArchivePath = Path.Combine(folder, "nonexistent.zip");
        await resume.ResumeCommand.ExecuteAsync(null);
        check(resume.HasError && resume.ArchivePath.EndsWith("nonexistent.zip") &&
            resume.MissingFiles.Single().LocalPath == local && !resume.IsQueued,
            "missing source archive failure stays visible and preserves user selection");
        resume.ArchivePath = cf;
        await resume.ResumeCommand.ExecuteAsync(null);
        check(resume.ImportState?.Status == ModpackImportStatus.Completed && resume.MissingFiles.Count == 0 && !resume.IsQueued,
            "UI resume snapshot completes manual file verification and reloads result");
        await vm.LoadFilesAsync(new[] { pack });
        model.State.Configure(model.State.Instances.MinecraftRoot, model.State.DownloadProvider.Source);
        check(vm.IsHome && !vm.HasPack && resume.ImportState is null, "configuration change invalidates previews and resume target");
        model.Download.IsModpackTab = false; model.CurrentPage = "Instances";
        await model.BackgroundRefresh;
    }
}
