using System.Diagnostics;
using System.IO;
using System.Windows;
using WMT.App.ViewModels;
using WMT.Core;

namespace WMT.App;

public partial class App : Application
{
    ActivityLog? log;
    public static string LogPath { get; private set; } = "";

    void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        int st = Array.IndexOf(args, "--selftest");
        string? selfTestDir = st >= 0 && st + 1 < args.Length ? Path.GetFullPath(args[st + 1]) : null;
        LogPath = selfTestDir != null
            ? Path.Combine(selfTestDir, "selftest-activity.log")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WMT", "Logs", $"wmt-{DateTime.Now:yyyyMMdd}.log");
        log = new ActivityLog(LogPath);
        var reg = new WindowsRegistry();
        var vm = new MainViewModel(log, reg, reg, new ProcessRunner());
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        if (args.Contains("--diagnostics"))
        {
            log.Info("app.diagnostics", $"Diagnostic start. OS {Environment.OSVersion}, .NET {Environment.Version}, 64-bit process {Environment.Is64BitProcess}, user {Environment.UserName}, DPI-aware per monitor.");
            OpenLog();
        }
        if (selfTestDir != null)
        {
            var test = new SelfTest(window, vm, log, selfTestDir);
            _ = window.Dispatcher.InvokeAsync(async () =>
            {
                int code;
                try { code = await test.RunAsync(); }
                catch (Exception ex) { log.Error("selftest.crash", ex.ToString()); code = 2; }
                Shutdown(code);
            });
        }
    }

    public static void OpenLog()
    {
        try
        {
            if (!File.Exists(LogPath)) { MessageBox.Show("The log file does not exist yet:\n" + LogPath); return; }
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + LogPath + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("Cannot open the log: " + ex.Message); }
    }

    void OnUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        log?.Error("app.unhandled", e.Exception.ToString());
        if (Environment.GetCommandLineArgs().Contains("--selftest")) { e.Handled = true; Shutdown(3); return; }
        MessageBox.Show("Unexpected error (written to the log):\n" + e.Exception.Message, "WMT", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
