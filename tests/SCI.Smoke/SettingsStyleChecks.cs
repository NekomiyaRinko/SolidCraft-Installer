using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using McDownload.Service;
using McDownload.View;
using McDownload.ViewModel;

internal static class SettingsStyleChecks
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nuint key, nint data);

    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
    private static Task Flush(Window window) => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static async Task<string> ReadLog(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public static async Task RunAsync(MainWindow owner, string root, FeatureChecks.Handler handler,
        Action<bool, string> check, Action<string, Window?> capture)
    {
        string settingsRoot = Path.Combine(root, "settings-style");
        string gameRoot = Path.Combine(settingsRoot, "game");
        string logRoot = Path.Combine(settingsRoot, "logs");
        string recycleRoot = Path.Combine(settingsRoot, "recycled");
        Directory.CreateDirectory(recycleRoot);
        string settingsFile = Path.Combine(settingsRoot, "settings.json");
        await File.WriteAllTextAsync(settingsFile, JsonSerializer.Serialize(new Dictionary<string, string>
        { ["root"] = gameRoot, ["rootMode"] = "custom", ["java"] = "", ["memory"] = "4096", ["source"] = "Bmclapi" }));
        var recycled = new List<string>();
        bool simulateNoOpRecycle = false;
        using var diagnostics = new DiagnosticLog(logRoot, 8 * 1024 * 1024, (path, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!path.StartsWith(logRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Recycle path escaped isolated log root");
            if (simulateNoOpRecycle) return Task.CompletedTask;
            recycled.Add(path);
            File.Move(path, Path.Combine(recycleRoot, Path.GetFileName(path)));
            return Task.CompletedTask;
        });
        using var http = new HttpClient(handler, false);
        var model = new MainWindowViewModel(http, settingsFile, diagnostics);
        var window = new MainWindow { Owner = owner, DataContext = model, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Show();
        try
        {
            await model.InitializeAsync();
            var settings = model.Settings;
            model.CurrentPage = "Settings";
            await Flush(window); window.UpdateLayout();
            var view = Find<SettingsView>(window).Single();
            var java = (TextBox)view.FindName("JavaPathInput");
            var javaBrowse = (Button)view.FindName("JavaBrowseButton");
            var rootInput = (TextBox)view.FindName("MinecraftRootInput");
            var rootBrowse = (Button)view.FindName("RootBrowseButton");
            capture("SCI-settings-auto", window);
            foreach (var pair in new[] { (java, javaBrowse), (rootInput, rootBrowse) })
            {
                check(pair.Item1.ActualHeight == 36 && pair.Item2.ActualHeight == 36 &&
                    Math.Abs(pair.Item1.TranslatePoint(new Point(), window).Y - pair.Item2.TranslatePoint(new Point(), window).Y) < 1,
                    $"settings input and browse button share the same height and top edge: {pair.Item1.ActualHeight}/{pair.Item2.ActualHeight}, y={pair.Item1.TranslatePoint(new Point(), window).Y}/{pair.Item2.TranslatePoint(new Point(), window).Y}");
            }
            check(!Find<Button>(view).Any(x => Equals(x.Content, "保存设置")), "settings has no standalone save button");
            long configuration = model.State.ConfigurationVersion;
            var memory = (TextBox)view.FindName("MemoryInput");
            memory.Focus(); memory.Text = "8192";
            check(settings.DefaultMemoryMb == 4096, "typed settings wait until focus leaves the input");
            javaBrowse.Focus(); await Flush(window); await settings.PendingSave;
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(settingsFile)))
                check(saved.RootElement.GetProperty("memory").GetString() == "8192" && settings.Message == "已自动保存",
                    "losing focus validates and automatically saves a setting");
            check(model.State.ConfigurationVersion == configuration, "saving preferences leaves instance services and selections intact");

            byte[] beforeInvalid = await File.ReadAllBytesAsync(settingsFile);
            settings.DefaultMemoryMb = 1;
            await settings.PendingSave;
            check((await File.ReadAllBytesAsync(settingsFile)).SequenceEqual(beforeInvalid) && settings.Message.Contains("范围"),
                "invalid settings do not overwrite the saved configuration");
            settings.DefaultMemoryMb = 9000; settings.DefaultMemoryMb = 10000; settings.DefaultMemoryMb = 11000;
            await settings.PendingSave;
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(settingsFile)))
                check(saved.RootElement.GetProperty("memory").GetString() == "11000" && !settings.IsSaving,
                    "rapid auto-saves are serialized and preserve the newest setting");
            memory.Focus(); memory.Text = "13000";
            window.CommitSettingsInput(); await settings.PendingSave;
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(settingsFile)))
                check(saved.RootElement.GetProperty("memory").GetString() == "13000", "window closing commits a still-focused settings input before waiting for saves");
            javaBrowse.Focus(); settings.DefaultMemoryMb = 11000; await settings.PendingSave;

            var source = (ComboBox)view.FindName("DownloadSourceCombo");
            source.SelectedIndex = 1;
            await settings.PendingSave;
            check(model.State.DownloadProvider.Source == McDownload.Model.DownloadSource.Official &&
                model.State.ConfigurationVersion == configuration + 1, "dropdown settings apply and save immediately");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = model.State.EnqueueAsync("settings busy check", context => gate.Task.WaitAsync(context.CancellationToken));
            byte[] busyBefore = await File.ReadAllBytesAsync(settingsFile);
            settings.DefaultMemoryMb = 12000;
            await settings.PendingSave;
            check((await File.ReadAllBytesAsync(settingsFile)).SequenceEqual(busyBefore) && settings.Message.Contains("任务队列"),
                "automatic settings changes remain blocked during queued work");
            gate.TrySetResult(); await running;
            settings.DefaultMemoryMb = 11000;
            await settings.PendingSave;

            await model.NavigateCommand.ExecuteAsync("Download");
            await Flush(window); window.UpdateLayout();
            var download = Find<DownloadView>(window).Single();
            var channel = (ComboBox)download.FindName("MinecraftChannelCombo");
            var version = (ComboBox)download.FindName("MinecraftVersionCombo");
            var refresh = (Button)download.FindName("MinecraftRefreshButton");
            check(new[] { channel.ActualHeight, version.ActualHeight, refresh.ActualHeight }.All(x => x == 36) &&
                Math.Abs(channel.TranslatePoint(new Point(), window).Y - refresh.TranslatePoint(new Point(), window).Y) < 1,
                "Minecraft dropdowns and refresh button have identical row geometry");
            check(channel.Template.FindName("DropDownToggle", channel) is ToggleButton &&
                ((TextBox)java).Template.FindName("PART_ContentHost", java) is ScrollViewer &&
                channel.Background is SolidColorBrush && refresh.Template is not null,
                "flat control templates retain the required WPF input parts");
            capture("SCI-controls", window);
            channel.Focus();
            ((IExpandCollapseProvider)new ComboBoxAutomationPeer(channel).GetPattern(PatternInterface.ExpandCollapse)).Expand();
            await Flush(window); window.UpdateLayout();
            var popup = (Popup)channel.Template.FindName("PART_Popup", channel);
            check(popup.IsOpen && popup.Child is Border { Background: SolidColorBrush } border && border.ActualWidth >= channel.ActualWidth,
                "custom dropdown opens a flat popup matching the input width");
            capture("SCI-dropdown", window);
            var option = (ComboBoxItem)channel.ItemContainerGenerator.ContainerFromIndex(2);
            option.IsSelected = true;
            check(channel.SelectedIndex == 2 && model.Download.SelectedMinecraftChannel == "测试版", "custom dropdown preserves container selection and data binding");
            var popupClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            popup.Closed += (_, _) => popupClosed.TrySetResult();
            channel.IsDropDownOpen = false;
            await popupClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            channel.SelectedIndex = 0;
            await Flush(window);
            window.Activate();
            channel.Focus();
            await Flush(window);
            PostMessage(new WindowInteropHelper(window).Handle, 0x0100, 0x28, 0x00500001);
            PostMessage(new WindowInteropHelper(window).Handle, 0x0101, 0x28, unchecked((nint)(int)0xC0500001));
            await Flush(window);
            check(channel.SelectedIndex == 1, $"custom dropdown preserves native Down-key navigation: index={channel.SelectedIndex}, focused={Keyboard.FocusedElement?.GetType().Name}");
            channel.SelectedIndex = 0;

            model.CurrentPage = "Settings";
            await Flush(window); window.UpdateLayout();
            capture("SCI-settings-auto", window);
            const string secret = "EXPORT_TOKEN_SENTINEL";
            model.State.Log("https://example.com/file?token=" + secret);
            await File.WriteAllTextAsync(Path.Combine(logRoot, "keep.log"), "foreign log");
            await File.WriteAllTextAsync(Path.Combine(logRoot, "SCI-user.log"), "foreign SCI name");
            string export = Path.Combine(settingsRoot, "export.zip");
            await settings.ExportLogsToAsync(export);
            ((Button)view.FindName("LogExportButton")).BringIntoView();
            await Flush(window); window.UpdateLayout();
            capture("SCI-settings-logs", window);
            using (var archive = ZipFile.OpenRead(export))
            {
                bool safe = archive.Entries.Count > 0 && archive.Entries.All(x => x.Name != "keep.log" && x.Name != "SCI-user.log");
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    safe &= !(await reader.ReadToEndAsync()).Contains(secret);
                }
                check(safe && settings.LogMessage.Contains("已导出"), "log export contains only SCI-generated redacted logs");
            }
            byte[] validExport = await File.ReadAllBytesAsync(export);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await diagnostics.ExportAsync(export, canceled.Token); } catch (OperationCanceledException) { }
            }
            check((await File.ReadAllBytesAsync(export)).SequenceEqual(validExport), "canceled export preserves an existing archive");
            bool nonZipRejected = false;
            try { await diagnostics.ExportAsync(Path.Combine(logRoot, "keep.log")); } catch (ArgumentException) { nonZipRejected = true; }
            check(nonZipRejected && await File.ReadAllTextAsync(Path.Combine(logRoot, "keep.log")) == "foreign log",
                "export cannot overwrite source logs with an archive");
            await settings.ConfirmDeleteLogsCommand.ExecuteAsync(null);
            check(recycled.Count == 0, "log deletion cannot execute before a confirmation request");

            Action<LogDeleteDialog>? inspect = null;
            Exception? dialogFailure = null;
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            EventManager.RegisterClassHandler(typeof(LogDeleteDialog), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                var dialog = (LogDeleteDialog)sender;
                if (dialog.Owner != window || inspect is null) return;
                dialog.Closed += (_, _) => closed.TrySetResult();
                dialog.ContentRendered += (_, _) =>
                {
                    try { inspect(dialog); }
                    catch (Exception ex) { dialogFailure = ex; dialog.Close(); }
                };
            }));
            inspect = dialog =>
            {
                var cancel = (Button)dialog.FindName("CancelButton");
                var confirm = (Button)dialog.FindName("ConfirmButton");
                check(dialog.WindowStartupLocation == WindowStartupLocation.CenterOwner && cancel.IsDefault && cancel.IsCancel &&
                    Keyboard.FocusedElement == cancel && !IsWindowEnabled(new WindowInteropHelper(window).Handle),
                    "log deletion uses an owned centered modal with cancel as the safe default");
                check(((SolidColorBrush)confirm.Background).Color == Color.FromRgb(188, 48, 48) &&
                    ((SolidColorBrush)confirm.Foreground).Color == Colors.White, "log deletion confirmation keeps the shared red-white danger style");
                capture("SCI-log-delete", dialog);
                Invoke(cancel);
            };
            settings.RequestDeleteLogsCommand.Execute(null);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dialogFailure is not null) throw dialogFailure;
            check(recycled.Count == 0 && !settings.IsLogDeleteConfirmationVisible, "canceling log confirmation leaves all files intact");

            inspect = dialog => dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.RequestDeleteLogsCommand.Execute(null);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dialogFailure is not null) throw dialogFailure;
            check(recycled.Count == 0 && !settings.IsLogDeleteConfirmationVisible, "Escape closes log confirmation without recycling files");
            inspect = dialog =>
            {
                nint handle = new WindowInteropHelper(dialog).Handle;
                PostMessage(handle, 0x0100, 0x0D, 0x001C0001);
                PostMessage(handle, 0x0101, 0x0D, unchecked((nint)(int)0xC01C0001));
            };
            closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.RequestDeleteLogsCommand.Execute(null);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dialogFailure is not null) throw dialogFailure;
            check(recycled.Count == 0 && !settings.IsLogDeleteConfirmationVisible, "Enter defaults to cancel for log deletion");

            string? previousLog = diagnostics.FilePath;
            int queueRecords = model.State.Tasks.Count;
            Task? deletion = null;
            inspect = _ => deletion = settings.ConfirmDeleteLogsCommand.ExecuteAsync(null);
            closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.RequestDeleteLogsCommand.Execute(null);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dialogFailure is not null) throw dialogFailure;
            await deletion!;
            check(recycled.Count > 0 && recycled.All(path => File.Exists(Path.Combine(recycleRoot, Path.GetFileName(path)))) &&
                File.Exists(Path.Combine(logRoot, "keep.log")) && File.Exists(Path.Combine(logRoot, "SCI-user.log")) &&
                diagnostics.FilePath != previousLog && File.Exists(diagnostics.FilePath) && model.State.Tasks.Count == queueRecords,
                "confirmed log cleanup recycles only scoped files, reopens logging and preserves task history");
            model.State.Log("recording after clear");
            check((await ReadLog(diagnostics.FilePath!)).Contains("recording after clear") &&
                settings.DiagnosticLogPath == diagnostics.FilePath && !settings.IsManagingLogs,
                "logging and the settings file path remain live after cleanup");
            simulateNoOpRecycle = true;
            string notRecycled = diagnostics.FilePath!;
            closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.RequestDeleteLogsCommand.Execute(null);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dialogFailure is not null) throw dialogFailure;
            await deletion!;
            check(File.Exists(notRecycled) && settings.LogMessage.StartsWith("日志清理未完成") &&
                File.Exists(diagnostics.FilePath) && !settings.IsManagingLogs,
                "a recycler that leaves a log file behind cannot report successful cleanup");
            bool directoryRejected = false;
            try { InstanceManager.RequireRecycled(recycleRoot); } catch (IOException) { directoryRejected = true; }
            check(directoryRejected, "the shared recycle completion guard also rejects a remaining directory");
            inspect = null;

            string failureRoot = Path.Combine(settingsRoot, "recycle-failure");
            using var failedCleanup = new DiagnosticLog(failureRoot, 4096, (_, _) => Task.FromException(new IOException("fixture recycle failure")));
            string originalLog = failedCleanup.FilePath!;
            bool cleanupFailed = false;
            try { await failedCleanup.RecycleLogsAsync(); } catch (IOException) { cleanupFailed = true; }
            failedCleanup.Write("INFO", "still recording after failed cleanup");
            check(cleanupFailed && File.Exists(originalLog) && failedCleanup.FilePath != originalLog &&
                (await ReadLog(failedCleanup.FilePath!)).Contains("still recording after failed cleanup"),
                "failed recycling preserves the original file and leaves the new log writable");
        }
        finally
        {
            await model.Settings.PendingSave;
            await model.BackgroundRefresh;
            window.Close();
            model.State.Dispose();
        }
    }
}
