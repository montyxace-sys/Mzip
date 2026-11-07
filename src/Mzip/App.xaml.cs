using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Mzip;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mzip", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        var jobPath = Elevation.GetJobPath(e.Args);
        if (jobPath is not null)
        {
            // Modo trabajador: lo lanza Mzip con permisos elevados para una operación concreta.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunJobAsync(jobPath);
            return;
        }

        var registration = ShellArguments.RegistrationCommand(e.Args);
        if (registration is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunRegistration(registration.Value);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        var request = ShellArguments.Parse(e.Args);
        if (request is not null)
        {
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => _ = window.RunShellAsync(request)));
        }
    }

    private static async Task RunJobAsync(string jobPath)
    {
        var exitCode = await Elevation.ExecuteJobAsync(jobPath);
        Application.Current.Shutdown(exitCode);
    }

    private static void RunRegistration(bool enable)
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            var exitCode = 0;
            try
            {
                ShellIntegration.Apply(enable);
            }
            catch
            {
                exitCode = 1;
            }
            Application.Current.Shutdown(exitCode);
        }));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show(
            $"Se produjo un error inesperado.\n\n{e.Exception.Message}\n\nDetalles en:\n{LogPath}",
            "Mzip", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // Nunca dejamos que el registro de errores tape el error original.
        }
    }
}
