namespace NxMassConfigurator.Wpf;

public partial class App
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "DispatcherUnhandledException");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                CrashLog.Write(ex, "UnhandledException");
            else
                CrashLog.Write($"UnhandledException: {args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "UnobservedTaskException");
            args.SetObserved();
        };
    }
}
