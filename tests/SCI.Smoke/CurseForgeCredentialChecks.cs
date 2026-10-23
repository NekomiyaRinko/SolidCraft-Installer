using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using McDownload.Service;
using McDownload.View;
using McDownload.ViewModel;
using McDownload.Model;

internal static class CurseForgeCredentialChecks
{
    private sealed class Api(string key) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri?.Host != "api.curseforge.com" || request.Headers.GetValues("x-api-key").Single() != key)
                throw new Exception("credential did not reach the official API request");
            Requests++;
            string data = request.RequestUri.AbsolutePath.Contains("/files/")
                ? "{\"id\":2,\"modId\":1,\"fileName\":\"fixture.jar\",\"fileLength\":1,\"hashes\":[{\"algo\":1,\"value\":\"0000000000000000000000000000000000000000\"}]}"
                : "{\"id\":1,\"gameId\":432,\"classId\":6,\"allowModDistribution\":false,\"links\":{\"websiteUrl\":\"https://www.curseforge.com/minecraft/mc-mods/fixture\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":" + data + "}") });
        }
    }

    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var item2 in Find<T>(child)) yield return item2;
        }
    }

    public static async Task RunAsync(Window owner, string root, Action<bool, string> check, Action<string, Window?> capture)
    {
        string folder = Path.Combine(root, "credential-checks"), file = Path.Combine(folder, "curseforge-key.dpapi");
        string settingsFile = Path.Combine(folder, "settings.json");
        const string key = "SCI_FIXTURE_ONLY_API_KEY_SENTINEL";
        var store = new CurseForgeCredentialStore(file);
        check(await store.LoadAsync() is null, "missing encrypted credential is not an error");
        await store.SaveAsync(key);
        byte[] encrypted = await File.ReadAllBytesAsync(file);
        check(!Encoding.UTF8.GetString(encrypted).Contains(key) && await store.LoadAsync() == key,
            "real DPAPI encrypts credential and round-trips for current Windows user");
        var corrupted = encrypted.ToArray(); corrupted[^1] ^= 1; await File.WriteAllBytesAsync(file, corrupted);
        bool rejected = false;
        try { await store.LoadAsync(); } catch (CryptographicException) { rejected = true; }
        check(rejected, "tampered DPAPI credential is rejected without plaintext fallback");
        await File.WriteAllTextAsync(settingsFile, JsonSerializer.Serialize(new Dictionary<string, string>
        { ["root"] = Path.Combine(folder, "game"), ["rootMode"] = "custom" }));
        using var http = new HttpClient(new FeatureChecks.Handler());
        using var diagnostics = new DiagnosticLog(Path.Combine(folder, "logs"));
        var opened = new List<string>();
        var api = new Api(key);
        var model = new MainWindowViewModel(http, settingsFile, diagnostics, opened.Add, api);
        var window = new MainWindow { Owner = owner, DataContext = model };
        window.Show();
        Action<ExternalLinkDialog>? inspect = null;
        Exception? dialogFailure = null;
        RoutedEventHandler observeDialog = (sender, _) =>
        {
            if (sender is not ExternalLinkDialog dialog || dialog.Owner != window) return;
            dialog.ContentRendered += (_, _) =>
            {
                try { inspect?.Invoke(dialog); }
                catch (Exception error) { dialogFailure = error; dialog.Close(); }
            };
        };
        EventManager.RegisterClassHandler(typeof(ExternalLinkDialog), FrameworkElement.LoadedEvent, observeDialog);
        try
        {
            await model.InitializeAsync();
            var settings = model.Settings;
            check(settings.CurseForgeMessage.Contains("无法解密") && !model.State.HasCurseForgeApiKey,
                "startup decryption failure asks for replacement without loading a key");
            settings.ClearCurseForgeKeyCommand.Execute(null);
            check(!File.Exists(file), "clear removes corrupted saved credential");
            await settings.ApplyCurseForgeKeyAsync(key);
            check(model.State.HasCurseForgeApiKey && !File.Exists(file) && settings.CurseForgeMessage.Contains("本次"),
                "default credential mode is memory only");
            settings.RememberCurseForgeKey = true;
            await settings.ApplyCurseForgeKeyAsync(key);
            check(File.Exists(file) && await store.LoadAsync() == key && !Directory.GetFiles(folder).Any(x => Path.GetFileName(x).StartsWith(".tmp.")),
                "remember stores DPAPI ciphertext atomically without temporary leftovers");
            using (var reloaded = new WorkspaceViewModel(http, new DiagnosticLog(Path.Combine(folder, "reload-logs"))))
            {
                var loadedSettings = new SettingsViewModel(reloaded, settingsFile);
                var loadedView = new SettingsView { DataContext = loadedSettings };
                var loadedImport = new ModpackImportViewModel(reloaded, loadedSettings);
                await loadedSettings.LoadAsync();
                check(((PasswordBox)loadedView.FindName("CurseForgeKeyInput")).Password == new string('*', 64),
                    "startup credential load displays sixty-four fixed stars");
                var lateView = new SettingsView { DataContext = loadedSettings };
                check(((PasswordBox)lateView.FindName("CurseForgeKeyInput")).Password == new string('*', 64),
                    "settings created after credential load displays sixty-four stars");
                loadedSettings.ClearCurseForgeKeyCommand.Execute(null);
                check(((PasswordBox)loadedView.FindName("CurseForgeKeyInput")).Password.Length == 0,
                    "clearing credentials removes the loaded placeholder");
                loadedSettings.RememberCurseForgeKey = true;
                await loadedSettings.ApplyCurseForgeKeyAsync(key);
                check(reloaded.HasCurseForgeApiKey && loadedSettings.RememberCurseForgeKey, "new settings session loads saved encrypted key automatically");
                check(loadedImport.CurseForgeAccessMessage.StartsWith("已配置"), "startup-loaded encrypted key updates the download credential notice");
            }
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = model.State.EnqueueAsync("credential busy", _ => gate.Task);
            try
            {
                await settings.ApplyCurseForgeKeyAsync("replacement"); settings.ClearCurseForgeKeyCommand.Execute(null);
                check(!settings.CanEditCurseForgeKey && await store.LoadAsync() == key && model.State.HasCurseForgeApiKey,
                    "running queue rejects credential replacement and clearing");
            }
            finally { gate.TrySetResult(); await work; }
            settings.RememberCurseForgeKey = false; await settings.ApplyCurseForgeKeyAsync(key);
            check(!File.Exists(file) && model.State.HasCurseForgeApiKey, "applying memory-only mode removes previous saved key");
            await settings.ApplyCurseForgeKeyAsync("invalid\r\nkey");
            check(settings.CurseForgeMessage.Contains("换行") && model.State.HasCurseForgeApiKey && !File.Exists(file),
                "invalid key input preserves active credential without logging its value");
            model.CurrentPage = "Settings"; window.UpdateLayout();
            var view = Find<SettingsView>(window).Single();
            var password = (PasswordBox)view.FindName("CurseForgeKeyInput");
            ((Button)view.FindName("CurseForgeConsoleButton")).BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            capture("SCI-curseforge-settings", window);
            window.Width = 820; window.Height = 620;
            ((Button)view.FindName("ApplyCurseForgeKeyButton")).BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout(); capture("SCI-curseforge-settings-small", window);
            check(password.ActualWidth > 250 && ((Button)view.FindName("CurseForgeConsoleButton")).ActualWidth < password.ActualWidth,
                "credential form and console link fit minimum window width");
            window.Width = 1120; window.Height = 780;
            check(password.Password == new string('*', 64) && Find<TextBlock>(view).Any(x => x.Text.Contains("不是账号密码")),
                "credential UI masks input and explains API key purpose below it");
            settings.ClearCurseForgeKeyCommand.Execute(null);
            var import = model.Download.Modpacks;
            import.Definition = new(ModpackFormat.CurseForge, "Credential fixture", "1", null, "1.20.1", null,
                new[] { new ModpackFile("curseforge:1:2", null, null, new(), Array.Empty<Uri>(), CurseForge: new(1, 2)) },
                Array.Empty<string>(), new string('0', 64), "fixture.zip");
            import.InstanceName = "CredentialFixture";
            var download = Find<DownloadView>(window).Single();
            var notice = (TextBlock)download.FindName("CurseForgeAccessNotice");
            await import.SubmitCommand.ExecuteAsync(null);
            check(import.Error.Contains("未配置") && notice.Text.Contains("尚未配置") && api.Requests == 0,
                "unconfigured download notice and preflight agree before applying via button");
            password.Password = key;
            ((Button)view.FindName("ApplyCurseForgeKeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(password.Password == new string('*', 64) && password.PasswordChar == '*' && model.State.HasCurseForgeApiKey && settings.CurseForgeMessage.Contains("已配置"),
                "apply button replaces the real key with sixty-four fixed stars");
            ((Button)view.FindName("ApplyCurseForgeKeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(password.Password == new string('*', 64) && settings.CurseForgeMessage.Contains("已配置"),
                "repeated apply does not submit the display placeholder");
            settings.RememberCurseForgeKey = true;
            ((Button)view.FindName("ApplyCurseForgeKeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while (settings.IsUpdatingCurseForgeKey) await Task.Delay(10, deadline.Token);
            check(password.Password == new string('*', 64) && File.Exists(file) && await store.LoadAsync() == key,
                "placeholder apply can remember the existing key without storing stars");
            settings.RememberCurseForgeKey = false;
            ((Button)view.FindName("ApplyCurseForgeKeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(password.Password == new string('*', 64) && !File.Exists(file) && model.State.HasCurseForgeApiKey,
                "placeholder apply can return to memory-only mode without replacing the key");
            model.CurrentPage = "Download"; model.Download.IsModpackTab = true; window.UpdateLayout();
            check(notice.IsVisible && notice.Text.StartsWith("已配置") && !import.HasError && import.HasPack,
                "returning to download updates credential notice and removes stale missing-key error without losing preview");
            await model.State.Modpacks.CheckAccessAsync(import.Definition, Array.Empty<string>(), default);
            check(api.Requests == 2, "actual workspace resolver uses key from apply button for file and project preflight requests");
            model.CurrentPage = "Settings"; window.UpdateLayout();
            password.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            password.Focus();
            check(password.Password.Length == 0, "editing a configured key clears the placeholder before replacement");
            password.Password = "invalid\r\nkey";
            ((Button)view.FindName("ApplyCurseForgeKeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(password.Password.Length == 0 && settings.CurseForgeMessage.Contains("换行"),
                "failed apply does not display a success placeholder");
            model.CurrentPage = "Download"; window.UpdateLayout();
            notice.BringIntoView(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            capture("SCI-curseforge-configured", window);
            import.Error = "unrelated archive failure";
            settings.ClearCurseForgeKeyCommand.Execute(null);
            check(notice.Text.Contains("尚未配置") && import.Error == "unrelated archive failure",
                "clearing credentials updates existing notice without discarding unrelated errors");
            import.JavaPath = Path.Combine(folder, "missing-java.exe");
            await import.SubmitCommand.ExecuteAsync(null);
            check(import.Error.Contains("java.exe"), "invalid installer Java is reported while no key is configured");
            settings.RememberCurseForgeKey = true;
            await settings.ApplyCurseForgeKeyAsync(key);
            check(notice.Text.StartsWith("已配置") && import.Error.Contains("java.exe"),
                "encrypted apply updates notice without masking unrelated errors");
            settings.ClearCurseForgeKeyCommand.Execute(null);
            import.JavaPath = "";
            await import.SubmitCommand.ExecuteAsync(null);
            check(import.Error.Contains("未配置"), "missing-key failure is tracked again before replacing the displayed error");
            import.Error = "replacement error";
            await settings.ApplyCurseForgeKeyAsync(key);
            check(import.Error == "replacement error" && import.HasPack && notice.Text.StartsWith("已配置"),
                "applying a key preserves a newer error even when a missing-key failure was tracked");
            import.Reset();
            model.CurrentPage = "Settings"; window.UpdateLayout();
            settings.ConfirmOpenCurseForgeConsole(); check(opened.Count == 0, "external launch cannot bypass confirmation state");
            var link = (Button)view.FindName("CurseForgeConsoleButton");
            inspect = dialog =>
            {
                check(dialog.Owner == window && !model.CanDropModpack && opened.Count == 0 && dialog.CancelButton.IsKeyboardFocused,
                    "external confirmation is owner-modal, defaults to cancel and blocks file drop");
                capture("SCI-curseforge-external-confirm", dialog);
                dialog.Close();
            };
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (dialogFailure is not null) throw dialogFailure;
            check(opened.Count == 0 && !settings.IsExternalLinkConfirmationVisible, "closing external confirmation never opens browser");
            inspect = dialog => dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (dialogFailure is not null) throw dialogFailure;
            check(opened.SequenceEqual(new[] { SettingsViewModel.CurseForgeConsoleUrl }) && !settings.IsExternalLinkConfirmationVisible,
                "explicit external confirmation opens only fixed HTTPS console URL without key");
            settings.RememberCurseForgeKey = true;
            await settings.ApplyCurseForgeKeyAsync(key);
            string sentinel = Path.Combine(diagnostics.DirectoryPath, "credential-sentinel.dpapi");
            await File.WriteAllBytesAsync(sentinel, await File.ReadAllBytesAsync(file));
            check(File.Exists(file) && File.Exists(sentinel) && await store.LoadAsync() == key,
                "export exclusion is tested with saved credential and non-log ciphertext sentinel present");
            var expectedLogs = Directory.GetFiles(diagnostics.DirectoryPath, "SCI-*.log")
                .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string export = Path.Combine(folder, "export.zip"); await settings.ExportLogsToAsync(export);
            using var zip = ZipFile.OpenRead(export);
            check(expectedLogs.Length > 0 && zip.Entries.Select(x => x.FullName).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expectedLogs),
                "export contains exactly the expected logs while saved credentials exist");
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                check(!entry.Name.EndsWith(".dpapi") && !(await reader.ReadToEndAsync()).Contains(key), "export excludes credential and plaintext key");
            }
            settings.ClearCurseForgeKeyCommand.Execute(null);
            check(!model.State.HasCurseForgeApiKey && !File.Exists(file), "clear removes both current key and local saved item");
            check(!(await File.ReadAllTextAsync(settingsFile)).Contains(key), "normal settings file contains no credential");
        }
        finally
        {
            inspect = null; window.Close(); await model.BackgroundRefresh; model.State.Dispose();
        }
    }
}
