using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using McDownload.View;
using McDownload.ViewModel;
using McDownload.Model;

internal static class UiChecks
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint key, nint data);

    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }

    private static void Invoke(Button button)
        => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    public static async Task RunAsync(MainWindow window, MainWindowViewModel model, Action<bool, string> check,
        Action<string, Window?> capture)
    {
        model.CurrentPage = "Instances";
        var instances = Find<InstanceListView>(window).Single();
        var details = (ScrollViewer)instances.FindName("InstanceDetails");
        var detailColumn = (ColumnDefinition)instances.FindName("InstanceDetailColumn");
        model.Instances.Instances.Clear();
        model.Instances.SelectedInstance = null;
        window.UpdateLayout();
        check(!details.IsVisible && detailColumn.ActualWidth == 0 &&
            ((StackPanel)instances.FindName("EmptyInstances")).IsVisible, "empty instance page hides details and releases their width");
        capture("SCI-instances-empty", null);

        await model.Instances.RefreshAsync();
        window.UpdateLayout();
        check(details.IsVisible && detailColumn.ActualWidth == 300, "selecting an instance restores details");
        var list = (ListBox)instances.FindName("InstanceList");
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(model.Instances.SelectedInstance);
        var selection = Find<Border>(item).First();
        check(item.ActualHeight is > 70 and < 130 && Math.Abs(selection.ActualHeight - item.ActualHeight) < 1,
            "selection background follows compact instance card height");
        var delete = Find<Button>(instances).Single(x => Equals(x.Content, "删除实例"));
        check(((SolidColorBrush)delete.Background).Color == Color.FromRgb(188, 48, 48) &&
            ((SolidColorBrush)delete.Foreground).Color == Colors.White, "delete action has red background and white text");
        capture("SCI-instances-compact", null);
        model.Instances.SelectedInstance = null;
        window.UpdateLayout();
        check(!details.IsVisible && detailColumn.ActualWidth == 0, "details also disappear when selection is cleared in a nonempty list");
        model.Instances.SelectedInstance = model.Instances.Instances.First();

        foreach (string page in new[] { "Instances", "Download", "Settings", "Loaders", "About", "Licenses", "ProjectLicense" })
        {
            model.CurrentPage = page;
            model.OpenTasksCommand.Execute(null);
            model.OpenTasksCommand.Execute(null);
            window.UpdateLayout();
            check(model.CurrentPage == "Tasks" && Find<UserControl>(window).Count(x => x.IsVisible && x is not LoaderVersionSelector) == 1,
                "task button opens a single secondary page from " + page);
            model.CloseTasksCommand.Execute(null);
            check(model.CurrentPage == page, "task page returns to " + page);
        }
        model.CurrentPage = "Download";
        window.UpdateLayout();
        check(!Find<ProgressBar>(window).Any(x => x.IsVisible) && !Find<ListBox>(Find<DownloadView>(window).Single()).Any(),
            "download page and window footer no longer duplicate task controls");
        check(((Button)window.FindName("OpenTasksButton")).IsVisible, "task entry remains visible outside the task page");

        OperationContext? runningContext = null;
        var pending = model.State.RunAsync("正在下载 Minecraft…", context =>
        { runningContext = context; return Task.Delay(Timeout.Infinite, context.CancellationToken); });
        model.OpenTasksCommand.Execute(null);
        var activeTask = model.State.CurrentTask!;
        runningContext!.Progress!.Report(new(runningContext.OperationId, default, 1, OperationStage.Downloading, null, .42, 0, null, "正在下载 Minecraft…"));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var tasks = Find<TaskView>(window).Single();
        Find<Expander>(tasks).Single(x => ReferenceEquals(x.DataContext, activeTask) && Equals(x.Header, "运行日志")).IsExpanded = true;
        window.UpdateLayout();
        check(Find<ProgressBar>(tasks).Single(x => ReferenceEquals(x.DataContext, activeTask) && x.Name == "OverallProgress").Value == 42 &&
            activeTask.CurrentItem == model.State.Status &&
            Find<ListBox>(tasks).Any(x => ReferenceEquals(x.ItemsSource, activeTask.Logs)),
            "task page binds current progress, status and logs");
        capture("SCI-tasks", null);
        model.CloseTasksCommand.Execute(null);
        check(model.State.IsBusy && !pending.IsCompleted, "leaving the task page does not cancel active work");
        model.OpenTasksCommand.Execute(null);
        Find<Button>(tasks).Single(x => x.IsVisible && Equals(x.Content, "取消当前项")).Command.Execute(null);
        check(!await pending && !model.State.IsBusy && model.State.Status == "任务已取消", "task page cancels active work");
        model.State.Error = "下载失败，请重试。";
        window.UpdateLayout();
        check(((TextBlock)tasks.FindName("TaskError")).IsVisible &&
            ((TextBlock)tasks.FindName("TaskError")).Text == model.State.Error, "task errors remain visible on the secondary page");
        model.State.Error = null;

        model.CurrentPage = "Instances";
        await model.BackgroundRefresh;
        await model.Instances.RefreshAsync();
        string metadata = model.Instances.SelectedInstance!.Version.DirectoryPath;
        Action<InstanceDeleteDialog>? inspect = null;
        Exception? dialogFailure = null;
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventManager.RegisterClassHandler(typeof(InstanceDeleteDialog), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (inspect is null) return;
                var dialog = (InstanceDeleteDialog)sender;
                dialog.Closed += (_, _) => closed.TrySetResult();
                dialog.ContentRendered += (_, _) =>
                {
                    try { inspect(dialog); }
                    catch (Exception error) { dialogFailure = error; dialog.Close(); }
                };
            }));

        inspect = dialog =>
        {
            var cancel = (Button)dialog.FindName("CancelButton");
            var confirm = (Button)dialog.FindName("ConfirmButton");
            check(dialog.Owner == window && dialog.WindowStartupLocation == WindowStartupLocation.CenterOwner &&
                Math.Abs((dialog.Left + dialog.ActualWidth / 2) - (window.Left + window.ActualWidth / 2)) < 3 &&
                Math.Abs((dialog.Top + dialog.ActualHeight / 2) - (window.Top + window.ActualHeight / 2)) < 3,
                "delete confirmation is centered on its modal owner");
            check(cancel.IsDefault && cancel.IsCancel && Keyboard.FocusedElement == cancel && !confirm.IsDefault,
                "delete confirmation defaults keyboard focus and Enter to cancel");
            check(((SolidColorBrush)confirm.Background).Color == ((SolidColorBrush)delete.Background).Color &&
                ((SolidColorBrush)confirm.Foreground).Color == Colors.White && ReferenceEquals(confirm.Command, model.Instances.ConfirmDeleteCommand),
                "modal confirmation reuses the red-white danger action and existing delete command");
            check(((Border)window.FindName("DeleteOverlay")).IsVisible, "modal confirmation dims the owner window");
            check(!IsWindowEnabled(new WindowInteropHelper(window).Handle), "modal confirmation disables native owner input");
            check(model.Instances.DeleteWarning.Contains("模组、存档和配置"), "delete warning identifies all instance data at risk");
            capture("SCI-delete-modal", dialog);
            capture("SCI-delete-overlay", null);
            Invoke(cancel);
        };
        await model.Instances.DeleteCommand.ExecuteAsync(null);
        await closed.Task;
        if (dialogFailure is not null) throw dialogFailure;
        check(!model.Instances.IsDeleteConfirmationVisible && Directory.Exists(metadata), "cancel button closes confirmation without recycling files");

        inspect = dialog =>
        {
            model.Instances.DeleteWarning = string.Join("、", Enumerable.Repeat("生存世界存档", 200));
            dialog.UpdateLayout();
            check(dialog.ActualHeight < window.ActualHeight - 40 &&
                Find<ScrollViewer>(dialog).First().ScrollableHeight > 0 &&
                ((Button)dialog.FindName("ConfirmButton")).IsVisible, "long save warnings scroll while confirmation buttons remain visible");
            dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            if (dialog.IsVisible) { dialog.Close(); throw new Exception("Escape did not close delete confirmation"); }
        };
        closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await model.Instances.DeleteCommand.ExecuteAsync(null);
        await closed.Task;
        if (dialogFailure is not null) throw dialogFailure;
        check(!model.Instances.IsDeleteConfirmationVisible && Directory.Exists(metadata), "Escape dismisses confirmation without recycling files");

        inspect = dialog =>
        {
            nint handle = new WindowInteropHelper(dialog).Handle;
            if (!PostMessage(handle, 0x0100, 0x0D, 0x001C0001) ||
                !PostMessage(handle, 0x0101, 0x0D, unchecked((nint)(int)0xC01C0001)))
            { dialog.Close(); throw new Exception("Failed to send Enter to the modal window"); }
        };
        closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await model.Instances.DeleteCommand.ExecuteAsync(null);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dialogFailure is not null) throw dialogFailure;
        check(!model.Instances.IsDeleteConfirmationVisible && Directory.Exists(metadata), "Enter cancels confirmation without recycling files");

        inspect = _ => model.Instances.SelectedInstance = null;
        closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await model.Instances.DeleteCommand.ExecuteAsync(null);
        await closed.Task;
        if (dialogFailure is not null) throw dialogFailure;
        check(!model.Instances.IsDeleteConfirmationVisible && Directory.Exists(metadata), "changing the target dismisses stale delete confirmation");
        inspect = null;
        model.Instances.SelectedInstance = model.Instances.Instances.First();

        window.Width = 820; window.Height = 620;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        check(details.ActualWidth == 300 && list.ActualWidth > 250, "compact instance layout fits the minimum window size");
        capture("SCI-instances-small", null);
        model.OpenTasksCommand.Execute(null);
        window.UpdateLayout();
        check(((ScrollViewer)tasks.FindName("QueueScroll")).ViewportHeight > 100, "task queue remains scrollable at minimum window size");
        capture("SCI-tasks-small", null);
        window.Width = 1120; window.Height = 780;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        model.CloseTasksCommand.Execute(null);
    }
}
