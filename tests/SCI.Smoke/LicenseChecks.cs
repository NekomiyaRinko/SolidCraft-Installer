using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using McDownload.i18n;
using McDownload.View;
using McDownload.ViewModel;

internal static class LicenseChecks
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

    public static async Task RunAsync(MainWindow owner, MainWindowViewModel model, Action<bool, string> check,
        Action<string, Window?> capture)
    {
        await model.NavigateCommand.ExecuteAsync("About");
        owner.UpdateLayout();
        var about = Find<AboutView>(owner).Single();
        var settings = Find<SettingsView>(owner).Single();
        var labels = Find<TextBlock>(about).Select(x => x.Text).ToArray();
        string version = typeof(MainWindowViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        check(!string.IsNullOrWhiteSpace(version) && model.ProductVersion == version &&
            ((TextBlock)about.FindName("ProductVersionLabel")).Text == Text.Format("AboutView", "VersionFormat", version),
            "about version is bound to the application informational version");
        check(labels.Any(x => x.Contains("2020–2026 huangyuhui and HMCL contributors")) &&
            labels.Any(x => x.Contains("Copyright © 2026 NekomiyaRinko.")), "about preserves both copyright notices at the same level");
        check(labels.Contains("SCI 不托管、不分发游戏文件。Minecraft 文件及加载器由 SCI 从 Mojang、加载器官方源或第三方镜像源获取后写入用户本地实例目录，不随 SCI 发布包捆绑，各自由其权利方的使用条款管理。") &&
            labels.Contains("SCI 的 GPL 声明不改变第三方组件各自的许可，由各自权利方的使用条款管理。") &&
            !labels.Any(x => x.Contains("BMCLAPI") || x.Contains("项目来源")), "about retains the approved README paragraphs without duplicate mirror or source descriptions");
        check(!Find<TextBlock>(settings).Any(x => x.Text == "许可证与声明") &&
            Find<TextBlock>(settings).Any(x => x.Text.Contains("bangbang93")), "settings keeps mirror attribution and moves legal notices to about");
        capture("SCI-about", null);

        var open = (Button)about.FindName("OpenLicensesButton");
        bool openVisible = open.IsVisible && Equals(open.Content, Text.Get("AboutView", "OpenLicenses"));
        open.Command.Execute(open.CommandParameter);
        await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        owner.UpdateLayout();
        var view = Find<LicensesView>(owner).Single();
        var tree = (TreeView)view.FindName("LicenseTree");
        var content = (TextBox)view.FindName("LicenseContent");
        check(openVisible && model.CurrentPage == "Licenses" && view.IsVisible && tree.Items.Count == 2 &&
            content.Text == model.Licenses.Documents[0].Content && content.IsReadOnly, "license button opens a grouped secondary page with complete embedded GPL text");
        check(model.Licenses.Groups.SelectMany(x => x.Documents).SequenceEqual(model.Licenses.Documents) &&
            model.Licenses.Groups[0].Documents.Count == 3 && model.Licenses.Groups[1].Documents.Count == 1,
            "license files are grouped by project and retain the approved document order");
        foreach (var group in model.Licenses.Groups)
        {
            var parent = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(group);
            check(parent.IsExpanded, "project menu starts expanded: " + group.Title);
            parent.IsExpanded = false;
            owner.UpdateLayout();
            check(!Find<TreeViewItem>(parent).Any(x => x.IsVisible), "project menu collapses its documents: " + group.Title);
            parent.IsExpanded = true;
            owner.UpdateLayout();
            foreach (var document in group.Documents)
            {
                var item = (TreeViewItem)parent.ItemContainerGenerator.ContainerFromItem(document);
                item.IsSelected = true;
                owner.UpdateLayout();
                check(model.Licenses.SelectedDocument == document && content.Text == document.Content,
                    "selecting an embedded license displays its full text: " + document.FileName);
            }
        }
        capture("SCI-open-source-licenses", null);
        model.OpenTasksCommand.Execute(null);
        model.CloseTasksCommand.Execute(null);
        check(model.CurrentPage == "Licenses" && model.Licenses.SelectedDocument == model.Licenses.Documents[^1],
            "task queue returns to the secondary page without losing the selected license");
        var back = Find<Button>(view).Single();
        back.Command.Execute(back.CommandParameter);
        check(model.CurrentPage == "About", "license secondary page returns to about");

        var project = (Button)about.FindName("OpenProjectLicenseButton");
        project.Command.Execute(project.CommandParameter);
        owner.UpdateLayout();
        var projectView = Find<ProjectLicenseView>(owner).Single();
        var projectContent = (TextBox)projectView.FindName("ProjectLicenseContent");
        check(model.CurrentPage == "ProjectLicense" && projectView.IsVisible && projectContent.IsReadOnly &&
            projectContent.Text == model.Licenses.ProjectLicense.Content && projectContent.Text.Contains("END OF TERMS AND CONDITIONS"),
            "project agreement opens the full embedded LICENSE independently of the grouped selection");
        capture("SCI-project-license", null);
        owner.Width = 820; owner.Height = 620;
        await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        owner.UpdateLayout();
        check(projectContent.ActualHeight > 250 && Find<ScrollViewer>(projectContent).First().ScrollableHeight > 0,
            "project agreement remains scrollable at minimum window size");
        await model.NavigateCommand.ExecuteAsync("Licenses");
        owner.UpdateLayout();
        check(content.ActualWidth > 250 && content.ActualHeight > 250 && tree.ActualWidth == 240,
            "grouped license secondary page fits the minimum window size");
        capture("SCI-open-source-licenses-small", null);
        await model.NavigateCommand.ExecuteAsync("About");
        owner.UpdateLayout();
        check(((Button)about.FindName("OpenProjectLicenseButton")).IsVisible &&
            Find<ScrollViewer>(about).Single().ScrollableWidth == 0, "about actions remain accessible without horizontal scrolling");
        capture("SCI-about-small", null);
        owner.Width = 1120; owner.Height = 780;
        await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
