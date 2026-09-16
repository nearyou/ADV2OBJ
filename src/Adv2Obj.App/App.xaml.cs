using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Adv2Obj.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ADV2OBJ",
            "error.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Error reporting must never terminate the application.
        }

        MessageBox.Show(
            "Conversion encountered an unexpected error. The application will remain open."
            + Environment.NewLine + Environment.NewLine
            + e.Exception.Message
            + Environment.NewLine + Environment.NewLine
            + "Log: " + logPath,
            "ADV2OBJ conversion error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
