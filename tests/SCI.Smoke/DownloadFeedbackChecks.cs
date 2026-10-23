using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using McDownload.Model;
using McDownload.View;
using McDownload.ViewModel;

internal static class DownloadFeedbackChecks
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

    public static async Task RunAsync(MainWindow window, MainWindowViewModel model, FeatureChecks.Handler handler,
        Action<bool, string> check, Action<string, Window?> capture)
    {
        using var fixture = new HttpClient(new FeatureChecks.Handler());
        string manifest = await fixture.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Override = async (_, _) => { await release.Task; return FeatureChecks.Handler.Response(manifest); };
        var view = Find<DownloadView>(window).Single();
        var overlay = (Border)view.FindName("DownloadOverlay");
        var content = (ScrollViewer)view.FindName("DownloadContent");
        Task navigation = Task.CompletedTask;
        try
        {
            navigation = model.NavigateCommand.ExecuteAsync("Download");
            window.UpdateLayout();
            check(overlay.IsVisible && !content.IsEnabled && overlay.ActualWidth == view.ActualWidth &&
                overlay.ActualHeight == view.ActualHeight, "loading overlay covers the entire download page including its heading");
            check(model.NavigateCommand.CanExecute("Settings"), "navigation stays enabled during a pending version request");
            capture("SCI-download-loading", null);
            await model.NavigateCommand.ExecuteAsync("Settings");
            window.UpdateLayout();
            check(model.CurrentPage == "Settings" && !overlay.IsVisible && !navigation.IsCompleted,
                "settings page is usable while download metadata continues loading");
            await model.NavigateCommand.ExecuteAsync("Instances");
            check(model.CurrentPage == "Instances" && !navigation.IsCompleted, "other pages remain reachable during loading");
            release.TrySetResult();
            await navigation;
            check(model.CurrentPage == "Instances" && !model.Download.IsPageBusy, "background load completion never changes the active page");
        }
        finally { release.TrySetResult(); await navigation; handler.Override = null; }

        string root = model.State.Instances.MinecraftRoot;
        var originalSource = model.State.DownloadProvider.Source;
        model.State.Configure(root, originalSource);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        handler.Override = async (_, _) =>
        {
            if (++requests == 1) await release.Task;
            else { latestStarted.TrySetResult(); await latestRelease.Task; }
            return FeatureChecks.Handler.Response(manifest);
        };
        try
        {
            navigation = model.NavigateCommand.ExecuteAsync("Download");
            await model.NavigateCommand.ExecuteAsync("Settings");
            model.State.Configure(root, DownloadSource.Official);
            await model.NavigateCommand.ExecuteAsync("Download");
            release.TrySetResult();
            await latestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.UpdateLayout();
            check(overlay.IsVisible && model.Download.IsRefreshingVersions && model.Download.MinecraftVersions.Count == 0,
                "configuration change during loading automatically requests current metadata and keeps the overlay visible");
            latestRelease.TrySetResult();
            await navigation;
            check(requests == 2 && model.Download.SelectedMinecraftVersion is not null && !model.Download.IsPageBusy &&
                model.CurrentPage == "Download", "returning after a source change never leaves an empty version list from a stale request");
        }
        finally
        {
            release.TrySetResult(); latestRelease.TrySetResult(); await navigation;
            handler.Override = null;
            model.State.Configure(root, originalSource);
        }

        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await model.NavigateCommand.ExecuteAsync("Download");
            handler.Override = async (_, _) => { await release.Task; return FeatureChecks.Handler.Response("[]"); };
            model.Download.NewGameLoader.SelectedType = "Forge";
            window.UpdateLayout();
            check(overlay.IsVisible && !content.IsEnabled, "loader version requests also cover the entire download page");
            await model.NavigateCommand.ExecuteAsync("Settings");
            release.TrySetResult();
            await model.Download.NewGameLoader.PendingRefresh;
            check(model.CurrentPage == "Settings" && !model.Download.IsPageBusy, "loader request completes without blocking navigation");
        }
        finally
        {
            release.TrySetResult(); await model.Download.NewGameLoader.PendingRefresh;
            handler.Override = null;
            await model.Download.NewGameLoader.ReloadCommand.ExecuteAsync(null);
            model.Download.NewGameLoader.SelectedType = "不安装";
            await model.Download.NewGameLoader.PendingRefresh;
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
