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
        AttachRowMenu(RestoreFilesGrid, "files", "Restore");
        AttachRowMenu(RestoreRegistryGrid, "registry", "Apply");
        AttachRowMenu(SettingsActionsGrid, "settings", "Apply");
        AttachRowMenu(KeyboardRestoreGrid, "keyboard", "Install");
    }

    /// <summary>Right-click menu of a restore list: act on the row under the mouse (or the highlighted rows) only,
    /// tick/untick the highlighted rows, select all / clear.</summary>
    void AttachRowMenu(DataGrid grid, string list, string verb)
    {
        var only = new MenuItem { FontWeight = FontWeights.SemiBold };
        var tick = new MenuItem { Header = "Tick highlighted rows" };
        var untick = new MenuItem { Header = "Untick highlighted rows" };
        var all = new MenuItem { Header = "Select all" };
        var none = new MenuItem { Header = "Clear all" };
        var menu = new ContextMenu();
        foreach (var i in new object[] { only, new Separator(), tick, untick, new Separator(), all, none }) menu.Items.Add(i);
        grid.ContextMenu = menu;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        // The right-clicked row becomes the highlighted row (unless it is already part of a multi-row highlight).
        grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row != null && !row.IsSelected) { grid.UnselectAll(); row.IsSelected = true; }
        };
        grid.ContextMenuOpening += (_, _) =>
        {
            int n = grid.SelectedItems.Count;
            only.Header = n <= 1 ? $"{verb} only this item" : $"{verb} only these {n} items";
            only.IsEnabled = tick.IsEnabled = untick.IsEnabled = n > 0 && vm.IsIdle;
            all.IsEnabled = none.IsEnabled = vm.IsIdle && grid.Items.Count > 0;
        };
        List<object> Highlighted() => grid.SelectedItems.Cast<object>().ToList();
        only.Click += async (_, _) => await vm.RunOnlyAsync(list, Highlighted());
        tick.Click += (_, _) => vm.SetChecked(list, Highlighted(), true);
        untick.Click += (_, _) => vm.SetChecked(list, Highlighted(), false);
        all.Click += (_, _) => vm.SetAll(list, true);
        none.Click += (_, _) => vm.SetAll(list, false);
    }

    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    public TabControl Tabs => MainTabs;
    public TabControl RecoveryTabs => BackupTabs;
    public DataGrid FilesGrid => RestoreFilesGrid;

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
