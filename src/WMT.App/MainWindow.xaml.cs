using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using WMT.Core.ViewModels;

namespace WMT.App;

public partial class MainWindow : Window
{
    readonly MainViewModel vm;
    readonly DispatcherTimer timer;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        DataContext = vm;
        vm.LogLines.CollectionChanged += OnLogChanged;
        // Incremental log reading (only new lines) + heartbeat check; nothing is scanned automatically.
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { vm.PollLog(); vm.CheckHeartbeat(DateTime.UtcNow); };
        timer.Start();
    }

    void OnLogChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    void OnHelp(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.Show();
    void OnOpenLog(object sender, RoutedEventArgs e) => App.OpenLog();
    void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show("Workstation Migration Toolkit v3.0.0-m1\nM1: Discovery/UI shell. No backup or restore yet.\nLog: " + App.LogPath,
            "About", MessageBoxButton.OK, MessageBoxImage.Information);
}
