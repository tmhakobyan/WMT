using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WMT.App.ViewModels;
using WMT.Core;

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
        // UI-008/009 + NFR-003: once per second update heartbeat text and read only NEW log lines.
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => vm.Tick(DateTime.UtcNow);
        timer.Start();
        Title = $"Workstation Migration Toolkit {Toolkit.Version}";
    }

    public TabControl Tabs => MainTabs;
    public TabControl RecoveryTabs => BackupTabs;

    bool scrollPending;
    void OnLogChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        // Never touch the ListBox layout inside CollectionChanged: scroll once, after the batch, at Background priority.
        if (e.Action != NotifyCollectionChangedAction.Add || scrollPending) return;
        scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            scrollPending = false;
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }));
    }

    void OnExplorerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = (sender as Selector)?.SelectedItem as ExplorerItemVM;
        if (item != null && vm.OpenItemCommand.CanExecute(item)) vm.OpenItemCommand.Execute(item);
    }

    void OnHelp(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.Show();
    void OnOpenLog(object sender, RoutedEventArgs e) => App.OpenLog();
    void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(App.LogPath)!) { UseShellExecute = true }); } catch { }
    }
    void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show($"Workstation Migration Toolkit {Toolkit.Version}\nBuild: {Toolkit.BuildId}\n\nLog: {App.LogPath}\n\n{Toolkit.IntegrityNote}", "About", MessageBoxButton.OK, MessageBoxImage.Information);
}
