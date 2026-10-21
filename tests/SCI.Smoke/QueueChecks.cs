using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using McDownload.Model;
using McDownload.Service;
using McDownload.View;
using McDownload.ViewModel;

internal static class QueueChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Flush(MainWindow window) => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    public static async Task RunAsync(MainWindow window, MainWindowViewModel model, string root,
        FeatureChecks.Handler handler, Action<bool, string> check, Action<string, Window?> capture)
    {
        var state = model.State;
        var order = new List<string>();
        var release = Gate();
        var cleanup = Gate();
        var cleaned = Gate();
        OperationContext? running = null;
        var steps = new[]
        {
            (OperationStep.Preparing, "准备实例"), (OperationStep.Client, "下载客户端"),
            (OperationStep.Libraries, "下载依赖库"), (OperationStep.Assets, "下载资源文件"),
            (OperationStep.Loader, "安装 Forge"), (OperationStep.Saving, "保存实例")
        };
        var first = state.EnqueueAsync("1.20.1-Forge-47.4.28", async context =>
        {
            running = context;
            order.Add("first");
            try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
            finally { cleanup.TrySetResult(); await release.Task; order.Add("cleanup"); cleaned.TrySetResult(); }
        }, steps);
        var firstItem = state.CurrentTask!;
        var second = state.EnqueueAsync("1.21.1-Fabric-0.16.9", _ => { order.Add("second"); return Task.CompletedTask; });
        var secondItem = state.Tasks.Single(x => x.Title == "1.21.1-Fabric-0.16.9");
        bool removedExecuted = false;
        var removed = state.EnqueueAsync("removed", _ => { removedExecuted = true; return Task.CompletedTask; });
        var removedItem = state.Tasks.Single(x => x.Title == "removed");
        check(order.SequenceEqual(new[] { "first" }) && state.IsBusy && secondItem.IsPending &&
            secondItem.Steps.All(x => x.State == QueuedTaskState.Pending) && state.Tasks[0] == firstItem,
            "queued tasks remain pending without executing or claiming subtask progress");
        state.RemoveQueuedCommand.Execute(removedItem);
        check(!await removed && !state.Tasks.Contains(removedItem) && !removedExecuted,
            "removing a waiting task prevents all of its work");
        bool configurationBlocked = false;
        try { state.Configure(root); } catch (InvalidOperationException) { configurationBlocked = true; }
        check(configurationBlocked && !await state.RunAsync("exclusive", _ => throw new Exception("must not execute")),
            "configuration changes and exclusive operations remain blocked while the queue is occupied");

        running!.Progress!.Report(new(running.OperationId, default, 1, OperationStage.Downloading, null, 1, 0, null, "客户端已完成", OperationStep.Client));
        running.Progress.Report(new(running.OperationId, default, 2, OperationStage.Downloading, null, .64, 0, null, "依赖库 140/220", OperationStep.Libraries));
        await Flush(window);
        check(Math.Abs(firstItem.Progress - 44) < .001 && firstItem.Steps[0].State == QueuedTaskState.Completed &&
            firstItem.Steps[1].State == QueuedTaskState.Completed && firstItem.Steps[2].IsRunning &&
            firstItem.Steps.Skip(3).All(x => x.State == QueuedTaskState.Pending),
            "overall progress aggregates real stage progress and leaves future subtasks pending");
        running.Progress.Report(new(running.OperationId, default, 3, OperationStage.Downloading, null, .1, 0, null, "依赖库 20/220", OperationStep.Libraries));
        running.Progress.Report(new(running.OperationId, default, 4, OperationStage.Downloading, null, .1, 0, null, "late client", OperationStep.Client));
        await Flush(window);
        check(Math.Abs(firstItem.Progress - 44) < .001 && firstItem.CurrentItem == "依赖库 140/220" && state.Status == firstItem.CurrentItem,
            "late or reordered subtask reports cannot move overall progress backwards");
        model.OpenTasksCommand.Execute(null);
        window.UpdateLayout();
        var taskView = Find<TaskView>(window).Single();
        var activeCard = Find<Border>(taskView).Single(x => x.Name == "TaskCard" && ReferenceEquals(x.DataContext, firstItem));
        var pendingCard = Find<Border>(taskView).Single(x => x.Name == "TaskCard" && ReferenceEquals(x.DataContext, secondItem));
        check(((SolidColorBrush)activeCard.Background).Color == Color.FromRgb(233, 242, 253) &&
            ((SolidColorBrush)pendingCard.Background).Color == Color.FromRgb(233, 237, 242) &&
            !Find<ProgressBar>(pendingCard).Any(x => x.IsVisible) &&
            !Find<Expander>(pendingCard).Any(x => x.IsVisible),
            "running task is colored while waiting tasks are gray and hide detailed progress");
        capture("SCI-task-queue", null);
        double oldWidth = window.Width, oldHeight = window.Height;
        window.Width = 820; window.Height = 620;
        await Flush(window); window.UpdateLayout();
        check(((ScrollViewer)taskView.FindName("QueueScroll")).ViewportHeight > 200 && activeCard.ActualWidth > 500,
            "expanded queue remains usable at minimum window size");
        capture("SCI-task-queue-small", null);
        window.Width = oldWidth; window.Height = oldHeight;
        await Flush(window);

        running.Progress.Report(new(running.OperationId, default, 5, OperationStage.Downloading, null, null, 0, null, "等待资源响应", OperationStep.Libraries));
        await Flush(window);
        check(firstItem.IsIndeterminate && firstItem.ProgressText == "进行中" && double.IsFinite(firstItem.Progress),
            "unknown subtask progress is shown as in progress instead of a fabricated percentage");
        state.CancelCommand.Execute(null);
        await cleanup.Task;
        check(secondItem.IsPending && order.SequenceEqual(new[] { "first" }) && !first.IsCompleted,
            "canceling current work waits for cleanup before starting the next item");
        release.TrySetResult();
        check(!await first && await second && cleaned.Task.IsCompleted &&
            order.SequenceEqual(new[] { "first", "cleanup", "second" }) && !state.IsBusy && !removedExecuted,
            "the queue continues in FIFO order after cancellation and cleanup");
        check(firstItem.State == QueuedTaskState.Canceled && secondItem.State == QueuedTaskState.Completed &&
            !firstItem.IsExpanded && !secondItem.IsExpanded, "terminal tasks collapse and retain their final status");

        var failureGate = Gate();
        var failure = state.EnqueueAsync("failed", async _ => { await failureGate.Task; throw new IOException("fixture failure"); });
        var failedItem = state.CurrentTask!;
        OperationContext? laterContext = null;
        var laterGate = Gate();
        var later = state.EnqueueAsync("after failure", context => { laterContext = context; return laterGate.Task; });
        failureGate.TrySetResult();
        check(!await failure && failedItem.State == QueuedTaskState.Failed && failedItem.Error == "fixture failure" && state.CurrentTask?.Title == "after failure",
            "failure is retained on its own task and does not stop subsequent work");
        var laterItem = state.CurrentTask!;
        running.Progress.Report(new(running.OperationId, default, 999, OperationStage.Downloading, 1, 1, 0, null, "stale report"));
        laterContext!.Progress!.Report(new(Guid.NewGuid(), default, 1, OperationStage.Downloading, 1, 1, 0, null, "wrong task"));
        await Flush(window);
        check(laterItem.Progress == 0 && laterItem.CurrentItem != "stale report" && laterItem.CurrentItem != "wrong task" && laterItem.Error is null,
            "old or mismatched progress cannot modify the next task");
        laterGate.TrySetResult();
        check(await later && failedItem.Error == "fixture failure", "later success leaves the failed task's error intact");

        await model.Download.RefreshVersionsAsync();
        model.Download.SelectedMinecraftChannel = "正式版";
        model.Download.SelectedMinecraftVersion = model.Download.MinecraftVersions.Single();
        var selector = model.Download.NewGameLoader;
        selector.SelectedType = "不安装";
        await selector.PendingRefresh;
        string originalJava = model.Settings.JavaPath;
        var downloadGate = Gate();
        var blocker = state.EnqueueAsync("snapshot blocker", _ => downloadGate.Task);
        model.Download.InstanceName = "QueueOne";
        var installOne = model.Download.InstallGameCommand.ExecuteAsync(null);
        check(model.Download.InstallGameCommand.CanExecute(null), "installation command permits a second submission while the first is queued");
        model.Download.InstanceName = "QueueTwo";
        var installTwo = model.Download.InstallGameCommand.ExecuteAsync(null);
        check(state.Tasks.Count(x => x.IsPending && x.Title is "QueueOne" or "QueueTwo") == 2,
            "two real installation command submissions create two waiting tasks");
        model.Download.InstanceName = "MutatedAfterSubmit";
        model.Settings.JavaPath = "mutated-java.exe";
        selector.SelectedType = "Forge";
        await selector.PendingRefresh;
        downloadGate.TrySetResult();
        await blocker; await Task.WhenAll(installOne, installTwo);
        model.Settings.JavaPath = originalJava;
        selector.SelectedType = "不安装";
        await selector.PendingRefresh;
        var installed = await state.Instances.ListAsync(default);
        foreach (string name in new[] { "QueueOne", "QueueTwo" })
        {
            var instance = installed.Single(x => x.DirectoryName == name);
            check(instance.MinecraftVersionId == "1.20.1" && instance.InstalledLoader is null &&
                instance.Editable.JavaExecutable == (string.IsNullOrWhiteSpace(originalJava) ? null : originalJava) &&
                File.Exists(Path.Combine(root, "versions", name, name + ".json")) &&
                state.Tasks.Single(x => x.Title == name).State == QueuedTaskState.Completed &&
                state.Tasks.Single(x => x.Title == name).Steps.All(x => x.State == QueuedTaskState.Completed),
                "queued install freezes name, Minecraft, loader and Java parameters: " + name);
        }
        check(installed.All(x => x.DirectoryName != "MutatedAfterSubmit"), "editing form inputs never renames queued installs");

        var originalInstaller = state.Loaders;
        byte[]? originalForgeInstaller = handler.ForgeInstaller;
        var javaRequests = new List<string?>();
        handler.ForgeInstaller = ForgeFixture();
        var fixtureInstaller = new LoaderInstaller(state.Instances, state.Versions, state.Fetcher,
            (java, _, _) => { javaRequests.Add(java); return Task.FromResult("fixture-java"); },
            async (_, _, work, token) =>
            {
                using var archive = ZipFile.OpenRead(Path.Combine(work, "installer.jar"));
                using var reader = new StreamReader(archive.GetEntry("version.json")!.Open());
                var profile = JsonNode.Parse(await reader.ReadToEndAsync(token))!.AsObject();
                string id = profile["id"]!.GetValue<string>();
                string directory = Path.Combine(work, "versions", id);
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), profile.ToJsonString(), token);
                return string.Empty;
            });
        var installerProperty = typeof(WorkspaceViewModel).GetProperty(nameof(WorkspaceViewModel.Loaders))!;
        installerProperty.SetValue(state, fixtureInstaller);
        var loaderGate = Gate();
        Task loaderBlocker = Task.CompletedTask, installLoaderOne = Task.CompletedTask, installLoaderTwo = Task.CompletedTask;
        try
        {
            var one = installed.Single(x => x.DirectoryName == "QueueOne");
            var two = installed.Single(x => x.DirectoryName == "QueueTwo");
            await model.Download.SelectInstanceAsync(one);
            model.Download.InstanceLoader.SelectedType = "Forge";
            await model.Download.InstanceLoader.PendingRefresh;
            model.Download.InstanceLoader.SelectedVersion = model.Download.InstanceLoader.Versions.Single(x => x.Version == "47.3.0");
            loaderBlocker = state.EnqueueAsync("loader snapshot blocker", context => loaderGate.Task.WaitAsync(context.CancellationToken));
            await using (var lease = await state.Instances.LockInstanceAsync(one.Id, default))
            {
                model.Settings.JavaPath = "queue-java-one.exe";
                installLoaderOne = model.Download.InstallLoaderCommand.ExecuteAsync(null);
                model.Settings.JavaPath = "queue-java-two.exe";
                await model.Download.SelectInstanceAsync(two);
                installLoaderTwo = model.Download.InstallLoaderCommand.ExecuteAsync(null);
                model.Settings.JavaPath = "mutated-after-loader-submit.exe";
                check(state.Tasks.Count(x => x.IsPending && x.Title is "QueueOne · 安装 Forge" or "QueueTwo · 安装 Forge") == 2,
                    "loader submissions enter the queue before shared display refresh or instance locks can delay them");
            }
            loaderGate.TrySetResult();
            await loaderBlocker; await Task.WhenAll(installLoaderOne, installLoaderTwo);
            check(javaRequests.SequenceEqual(new[] { "queue-java-one.exe", "queue-java-two.exe" }),
                "each standalone loader install keeps its Java path from submission time");
            check((await state.Instances.GetAsync(one.Id, default)).InstalledLoader == new LoaderSelection(LoaderKind.Forge, "47.3.0") &&
                (await state.Instances.GetAsync(two.Id, default)).InstalledLoader == new LoaderSelection(LoaderKind.Forge, "47.3.0") &&
                state.Tasks.Single(x => x.Title == "QueueOne · 安装 Forge").State == QueuedTaskState.Completed &&
                state.Tasks.Single(x => x.Title == "QueueTwo · 安装 Forge").State == QueuedTaskState.Completed,
                "interleaved loader submissions for two instances both finish without being silently dropped");
        }
        finally
        {
            loaderGate.TrySetResult();
            try { await Task.WhenAll(loaderBlocker, installLoaderOne, installLoaderTwo); }
            finally
            {
                model.Settings.JavaPath = originalJava;
                installerProperty.SetValue(state, originalInstaller);
                handler.ForgeInstaller = originalForgeInstaller;
                model.Download.InstanceLoader.SelectedType = "不安装";
                await model.Download.InstanceLoader.PendingRefresh;
            }
        }

        using var http = new HttpClient(handler, false);
        using var stopping = new WorkspaceViewModel(http, new DiagnosticLog(Path.Combine(root, "queue-stop-logs")));
        var stopCleanup = Gate();
        var stopStarted = Gate();
        var active = stopping.EnqueueAsync("shutdown active", async context =>
        {
            try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
            finally { stopStarted.TrySetResult(); await stopCleanup.Task; }
        });
        bool shutdownPendingRan = false;
        var waiting = stopping.EnqueueAsync("shutdown pending", _ => { shutdownPendingRan = true; return Task.CompletedTask; });
        stopping.StopQueue();
        await stopStarted.Task;
        check(!await waiting && stopping.IsBusy && !shutdownPendingRan, "shutdown removes waiting work but waits for active cleanup");
        stopCleanup.TrySetResult();
        check(!await active && !stopping.IsBusy && !await stopping.EnqueueAsync("too late", _ => throw new Exception("must not execute")),
            "shutdown never starts another queued task or accepts new work");
        model.CloseTasksCommand.Execute(null);
    }

    private static byte[] ForgeFixture()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Write(string name, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
            Write("install_profile.json", """
                {"spec":1,"minecraft":"1.20.1","json":"version.json","processors":[],"libraries":[]}
                """);
            Write("version.json", """
                {"id":"1.20.1-forge-47.3.0","inheritsFrom":"1.20.1","mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher","libraries":[]}
                """);
            Write("net/minecraftforge/installer/Fixture.class", "install-client");
        }
        return stream.ToArray();
    }
}
