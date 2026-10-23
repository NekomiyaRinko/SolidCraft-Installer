using McDownload.i18n;
using System.Windows;
using McDownload.View;
using McDownload.ViewModel;
namespace McDownload;
public partial class App : Application
{
    private MainWindowViewModel? _model;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var model = _model = new MainWindowViewModel();
        DispatcherUnhandledException += (_, args) => model.State.LogException(Text.Get("App", "UnhandledUiException"), args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) model.State.LogException(Text.Get("App", "UnhandledProcessException"), error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => model.State.LogException(Text.Get("App", "UnobservedTaskException"), args.Exception);
        var window = new MainWindow { DataContext = model };
        bool closeAfterCancellation = false;
        window.Closing += (_, args) =>
        {
            window.CommitSettingsInput();
            if (!model.State.IsBusy && !model.Settings.IsSaving && !model.Settings.IsManagingLogs && !model.Settings.IsUpdatingCurseForgeKey) return;
            args.Cancel = true;
            closeAfterCancellation = true;
            model.State.StopQueue();
        };
        model.State.PropertyChanged += (_, args) =>
        {
            if (closeAfterCancellation && args.PropertyName == nameof(model.State.IsBusy) &&
                 !model.State.IsBusy && !model.Settings.IsSaving && !model.Settings.IsManagingLogs && !model.Settings.IsUpdatingCurseForgeKey)
                Dispatcher.BeginInvoke(() => window.Close());
        };
        model.Settings.PropertyChanged += (_, args) =>
        {
            if (closeAfterCancellation && args.PropertyName is nameof(model.Settings.IsSaving) or nameof(model.Settings.IsManagingLogs) or nameof(model.Settings.IsUpdatingCurseForgeKey) &&
                 !model.State.IsBusy && !model.Settings.IsSaving && !model.Settings.IsManagingLogs && !model.Settings.IsUpdatingCurseForgeKey)
                Dispatcher.BeginInvoke(() => window.Close());
        };
        MainWindow = window; window.Show();
        try { await model.InitializeAsync(); }
        catch (Exception ex) { model.State.ReportError(Text.Get("App", "InitializationFailed"), ex); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _model?.State.Log(Text.Get("App", "Exited"));
        _model?.State.Dispose();
        base.OnExit(e);
    }
}
