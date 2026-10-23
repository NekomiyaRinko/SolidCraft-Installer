using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using McDownload.Model;
using McDownload.Service;
using McDownload.View;
using McDownload.ViewModel;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if (args.Contains("--online")) return FeatureChecks.OnlineAsync().GetAwaiter().GetResult();
        if (args.Contains("--forge-network")) return ForbiddenChecks.OnlineAsync().GetAwaiter().GetResult();
        var errors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SCI;component/View/Theme.xaml") });
        var handler = new FeatureChecks.Handler();
        using var http = new System.Net.Http.HttpClient(handler);
        string root = Path.Combine(Path.GetTempPath(), "opencode", "sci-ui-data-" + Guid.NewGuid().ToString("N"));
        using var diagnostics = new DiagnosticLog(Path.Combine(root, "diagnostics"));
        var model = new MainWindowViewModel(http, diagnostics: diagnostics);
        var window = new MainWindow { DataContext = model };
        int result = 0, checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        async Task RunChecks(string name, Func<Task> run)
        {
            int before = checks;
            try { await run(); }
            catch (Exception ex) { throw new Exception($"{name} failed after {checks - before} checks", ex); }
            Console.WriteLine(checks == before ? $"SKIP {name}: no checks executed" : $"PASS {name}: {checks - before} checks");
        }
        IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T item) yield return item;
                foreach (var descendant in Find<T>(child)) yield return descendant;
            }
        }
        void Capture(string name, Window? target = null)
        {
            var capturedWindow = target ?? window;
            capturedWindow.UpdateLayout();
            var surface = (FrameworkElement)capturedWindow.Content;
            var image = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
                context.DrawRectangle(capturedWindow.Background, null, bounds);
            }
            image.Render(drawing);
            image.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string artifacts = Path.Combine(Path.GetTempPath(), "opencode");
            Directory.CreateDirectory(artifacts);
            using var output = File.Create(Path.Combine(artifacts, name + ".png")); encoder.Save(output);
        }
        window.Loaded += async (_, _) =>
        {
            try
            {
                model.State.Configure(root);
                var instance = await model.State.Instances.CreateAsync(new("Survival", "1.20.1", IsolationPolicy.PerInstance, new("生存世界", null, null)), default);
                await model.Instances.RefreshAsync();
                Check(model.Instances.Instances.Count == 1, "instance list");
                await RunChecks(nameof(UiChecks), () => UiChecks.RunAsync(window, model, Check, Capture));
                await RunChecks(nameof(DownloadFeedbackChecks), () => DownloadFeedbackChecks.RunAsync(window, model, handler, Check, Capture));
                await RunChecks(nameof(ColorStateChecks), () => ColorStateChecks.RunAsync(window, model, Check, Capture));
                await RunChecks(nameof(QueueChecks), () => QueueChecks.RunAsync(window, model, root, handler, Check, Capture));
                await RunChecks(nameof(SettingsStyleChecks), () => SettingsStyleChecks.RunAsync(window, root, handler, Check, Capture));
                foreach (string page in new[] { "Instances", "Download", "Settings", "Loaders", "Tasks", "About", "Licenses", "ProjectLicense" })
                {
                    model.CurrentPage = page; window.UpdateLayout();
                    Check(Find<UserControl>(window).Count(x => x.IsVisible && x is not LoaderVersionSelector) == 1, "single page " + page);
                }
                await model.Download.SelectInstanceAsync(instance);
                model.CurrentPage = "Loaders"; window.UpdateLayout();
                Check(Find<TextBlock>(window).Any(x => x.IsVisible && x.Text.Contains("不会删除 mods")), "warning visible");
                model.Download.IsLoaderConfirmationVisible = true; model.Download.ConfirmationText = "删除当前加载器？"; window.UpdateLayout();
                Check(Find<Button>(window).Any(x => x.IsVisible && Equals(x.Content, "确认")), "confirmation visible");
                model.CurrentPage = "Download"; model.State.IsBusy = true; window.UpdateLayout();
                Check(Find<Button>(window).First(x => Equals(x.Content, "下载并安装")).IsEnabled, "install remains enabled for queue submissions while busy");
                model.OpenTasksCommand.Execute(null); window.UpdateLayout();
                model.State.IsBusy = false;
                Check(!Find<Button>(window).Any(x => (x.Content as string)?.Contains("启动") == true), "no launch action");
                var a = await model.State.Instances.CreateAsync(new("A", "1.20.1", IsolationPolicy.PerInstance, new("A", null, null)), default);
                var b = await model.State.Instances.CreateAsync(new("B", "1.20.1", IsolationPolicy.PerInstance, new("B", null, null)), default);
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
                var ap = await model.State.Instances.ResolvePathsAsync(a.Id, default);
                var bp = await model.State.Instances.ResolvePathsAsync(b.Id, default);
                var patchId = Guid.NewGuid();
                var selection = new LoaderSelection(LoaderKind.Forge, "47.3.0");
                a = a with { InstalledLoader = selection, LoaderPatchId = patchId, ActivePatchIds = new[] { patchId } };
                b = b with { InstalledLoader = selection, LoaderPatchId = Guid.NewGuid(), ActivePatchIds = new[] { Guid.NewGuid() } };
                b = b with { ActivePatchIds = new[] { b.LoaderPatchId!.Value } };
                await File.WriteAllBytesAsync(ap.BaseVersionJsonFile, JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["id"] = "1.20.1", ["mainClass"] = "net.minecraft.Main" }, options));
                var patch = new VersionPatch(patchId, VersionPatchKind.Loader, selection, 1000, new JsonObject { ["mainClass"] = "net.minecraftforge.Main" });
                await File.WriteAllBytesAsync(Path.Combine(ap.PatchDirectory, patchId.ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(patch, options));
                await File.WriteAllBytesAsync(ap.VersionJsonFile, JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["id"] = "A", ["jar"] = "A", ["inheritsFrom"] = "1.20.1", ["mainClass"] = "net.minecraftforge.Main" }, options));
                await File.WriteAllBytesAsync(ap.MetadataFile, JsonSerializer.SerializeToUtf8Bytes(a, options));
                await File.WriteAllBytesAsync(bp.MetadataFile, JsonSerializer.SerializeToUtf8Bytes(b, options));
                a = await model.State.Instances.GetAsync(a.Id, default);
                b = await model.State.Instances.GetAsync(b.Id, default);
                await model.Download.SelectInstanceAsync(a);
                Check(model.Download.CurrentLoader.Contains("Forge"), "A loader state known");
                var aBefore = await File.ReadAllBytesAsync(ap.MetadataFile);
                await model.Download.SelectInstanceAsync(b);
                Check(model.Download.CurrentLoader.Contains("读取失败"), "B damaged state rejected");
                model.Download.RequestRemoveCommand.Execute(null);
                Check(!model.Download.IsLoaderConfirmationVisible, "B failure cannot confirm A removal");
                await model.Download.ConfirmLoaderCommand.ExecuteAsync(null);
                var aAfter = await File.ReadAllBytesAsync(ap.MetadataFile);
                Check(aBefore.SequenceEqual(aAfter), "A metadata untouched after B failure");
                Check((await model.State.Loaders.GetStateAsync(a.Id, default)).InstalledLoader == selection, "A loader remains installed");
                var run = model.State.RunAsync("取消回归", context => Task.Delay(Timeout.Infinite, context.CancellationToken));
                Check(model.State.IsBusy, "cancel task active");
                model.State.CancelCommand.Execute(null);
                Check(!await run && !model.State.IsBusy && model.State.Status.Contains("取消"), "cancel propagates token");
                await RunChecks(nameof(DownloadConcurrencyChecks), () => DownloadConcurrencyChecks.RunAsync(root, Check));
                await RunChecks(nameof(FeatureChecks), () => FeatureChecks.RunAsync(model, root, handler, Check));
                await RunChecks(nameof(ModpackUiChecks), () => ModpackUiChecks.RunAsync(window, model, root, Check, Capture));
                if (!args.Contains("--fixture-only"))
                    await RunChecks(nameof(ModpackSampleUiChecks), () => ModpackSampleUiChecks.RunAsync(window, model, Check, Capture));
                else Console.WriteLine($"SKIP {nameof(ModpackSampleUiChecks)}: --fixture-only");
                await RunChecks(nameof(ModpackChecks), () => ModpackChecks.RunAsync(root, Check));
                await RunChecks(nameof(ModpackQueueChecks), () => ModpackQueueChecks.RunAsync(root, Check));
                await RunChecks(nameof(ModpackRecoveryChecks), () => ModpackRecoveryChecks.RunAsync(root, Check));
                await RunChecks(nameof(ModpackTransportChecks), () => ModpackTransportChecks.RunAsync(root, Check));
                await RunChecks(nameof(CurseForgeCredentialChecks), () => CurseForgeCredentialChecks.RunAsync(window, root, Check, Capture));
                await RunChecks(nameof(LocalizationChecks), () => { LocalizationChecks.Run(root, Check); return Task.CompletedTask; });
                await RunChecks(nameof(ForbiddenChecks), () => ForbiddenChecks.RunAsync(model.State, root, Check));
                await RunChecks(nameof(DiagnosticChecks), () => DiagnosticChecks.RunAsync(model.State, root, Check));
                model.CurrentPage = "Settings"; window.UpdateLayout();
                Check(Find<Button>(window).Any(x => x.IsVisible && Equals(x.Content, "打开日志文件夹")), "diagnostic log action visible");
                Check(model.Licenses.Documents.Select(x => x.FileName).SequenceEqual(new[]
                    { "LICENSE", "HMCL-ADDITIONAL-TERMS.md", "NOTICE", "THIRD-PARTY-NOTICES.md" }) &&
                    model.Licenses.Documents.All(x => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, x.FileName)) == x.Content),
                    "license texts are embedded in the application assembly in project order and match the source files");
                await RunChecks(nameof(LicenseChecks), () => LicenseChecks.RunAsync(window, model, Check, Capture));
                string thirdPartyNotices = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md"));
                Check(thirdPartyNotices.Contains("CommunityToolkit.Mvvm 8.4.0") && thirdPartyNotices.Contains("Copyright © .NET Foundation and Contributors"),
                    "MVVM dependency copyright notice is distributed with the application");
                Check(thirdPartyNotices.Contains("The above copyright notice and this permission notice") && thirdPartyNotices.Contains("THE SOFTWARE IS PROVIDED"),
                    "MVVM dependency MIT permission and warranty text are distributed intact");
                model.CurrentPage = "Settings"; Capture("SCI-settings");
                model.CurrentPage = "Instances";
                await model.Download.SelectInstanceAsync(instance);
                model.CurrentPage = "Instances"; Capture("SCI-instances");
                model.CurrentPage = "Download"; Capture("SCI-download");
                model.CurrentPage = "Loaders"; Capture("SCI-loaders");
                model.CurrentPage = "Download";
                window.Width = 820; window.Height = 620; Capture("SCI-download-small");
                Check(string.IsNullOrWhiteSpace(errors.ToString()), "binding errors: " + errors);
                Console.WriteLine($"PASS {checks} offline checks; fixture-only downloads, no real game/Java execution or instance recycling.");
            }
            catch (Exception ex) { Console.WriteLine(ex); result = 1; }
            finally
            {
                await model.BackgroundRefresh;
                model.State.Dispose();
                app.Shutdown();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        };
        app.Run(window);
        return result;
    }
}
