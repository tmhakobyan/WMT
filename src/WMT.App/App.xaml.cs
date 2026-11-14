using System.IO;
using System.Diagnostics;
using System.Windows;
using WMT.Core;
using WMT.Core.ViewModels;

namespace WMT.App;

public partial class App : Application
{
    ActivityLog? log;
    public static string LogPath { get; private set; } = "";

    void OnStartup(object sender, StartupEventArgs e)
    {
        LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WMT", "Logs", $"wmt-{DateTime.Now:yyyyMMdd}.log");
        log = new ActivityLog(LogPath);
        var vm = new MainViewModel(
            new SoftwareDiscovery(new WindowsUninstallSource()),
            new ProfileDiscovery(new WindowsProfileSource(), new WindowsAccountResolver()),
            OfficeFingerprint.Read, log);
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        if (e.Args.Contains("--diagnostics")) OpenLog();
    }

    public static void OpenLog()
    {
        try
        {
            if (!File.Exists(LogPath)) { MessageBox.Show("Log file does not exist yet:\n" + LogPath); return; }
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + LogPath + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("Cannot open log: " + ex.Message); }
    }

    void OnUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        log?.Write("error", "app.unhandled", e.Exception.ToString());
        MessageBox.Show("Unexpected error (logged):\n" + e.Exception.Message, "WMT", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
