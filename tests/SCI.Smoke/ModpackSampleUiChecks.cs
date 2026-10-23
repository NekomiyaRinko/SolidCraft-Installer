using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using McDownload.View;
using McDownload.ViewModel;

internal static class ModpackSampleUiChecks
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
    public static async Task RunAsync(MainWindow window, MainWindowViewModel model, Action<bool, string> check,
        Action<string, Window?> capture)
    {
        string samples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../modpack_test"));
        if (!Directory.Exists(samples)) return;
        var vm = model.Download.Modpacks;
        await model.ImportDroppedFilesAsync(new[] { Path.Combine(samples, "BMC2 [FABRIC] 1.20.1 v40.mrpack") });
        window.UpdateLayout();
        check(vm.PackName == "Better MC [FABRIC] BMC2" && vm.PackVersion == "整合包版本 v1" &&
            vm.Loader == "Fabric 0.19.3" && vm.MinecraftVersion == "1.20.1" && vm.CanSubmit,
            "BMC2 real sample preview exposes actual manifest version rather than filename version");
        capture("SCI-modpack-BMC2", null);
        await model.ImportDroppedFilesAsync(new[] { Path.Combine(samples, "RLCraft 1.12.2 - Release v2.9.3.zip") });
        window.UpdateLayout();
        check(vm.IsCurseForge && vm.PackName == "RLCraft" && vm.PackVersion == "整合包版本 2.9.3" &&
            vm.Loader == "Forge 14.23.5.2860" && vm.MinecraftVersion == "1.12.2" && vm.CanSubmit,
            "RLCraft real sample preview exposes exact versions without API or network calls");
        capture("SCI-modpack-RLCraft", null);
        var view = Find<DownloadView>(window).Single();
        var scroll = (ScrollViewer)view.FindName("DownloadContent");
        window.Width = 820; window.Height = 620; window.UpdateLayout();
        scroll.ScrollToBottom(); window.UpdateLayout();
        var submit = (Button)view.FindName("SubmitModpackButton");
        var position = submit.TranslatePoint(new Point(), scroll);
        check(submit.IsVisible && position.Y >= 0 && position.Y + submit.ActualHeight <= scroll.ActualHeight,
            "real pack submit action scrolls fully into view at minimum window size");
        capture("SCI-modpack-RLCraft-bottom-small", null);
        string bmc4 = Path.Combine(samples, "BMC4 [FORGE] 1.20.1 v62hf.zip");
        if (File.Exists(bmc4))
        {
            await model.ImportDroppedFilesAsync(new[] { bmc4 });
            check(vm.PackName == "BMC4" && vm.Loader == "Forge 47.4.20" &&
                vm.Definition!.Files.Count(x => x.CurseForge is not null) == 425,
                "real BMC4 failing sample parses all 425 exact CurseForge references");
            int queued = model.State.Tasks.Count;
            await vm.SubmitCommand.ExecuteAsync(null);
            check(vm.HasPack && vm.Error.Contains("API key") && model.State.Tasks.Count == queued &&
                !(await model.State.Instances.ListAsync(default)).Any(x => x.DirectoryName == vm.InstanceName),
                "real BMC4 no-key regression fails before queueing or creating an instance");
        }
        window.Width = 1120; window.Height = 780;
        scroll.ScrollToTop();
        vm.Reset(); window.UpdateLayout();
        capture("SCI-modpack-home-final", null);
        model.Download.IsModpackTab = false; model.CurrentPage = "Instances";
    }
}
