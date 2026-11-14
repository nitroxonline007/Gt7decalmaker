using System.IO;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;

namespace GT7DecalMaker
{
    public partial class App : Application
    {
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "gt7decalmaker-crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                Log(args.Exception);
                MessageBox.Show(args.Exception.ToString(), "Unexpected error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log(args.Exception);
                args.SetObserved();
            };
        }

        private static void Log(Exception? ex)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch
            {
                // last resort - never throw from the logger
            }
        }
    }
}
