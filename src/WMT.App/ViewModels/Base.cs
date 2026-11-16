using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using WMT.App.Services;
using WMT.Core;

namespace WMT.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    public void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class RelayCommand : ICommand
{
    readonly Action<object?> exec;
    readonly Func<object?, bool>? can;
    public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null) { this.exec = exec; this.can = can; }
    public RelayCommand(Action exec, Func<bool>? can = null) : this(_ => exec(), can == null ? null : _ => can()) { }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) exec(parameter); }
}

public sealed class SoftwareRow : ObservableObject
{
    public SoftwareRow(SoftwareItem item) { Item = item; }
    public SoftwareItem Item { get; }
    public bool IsSelected { get => Item.Selected; set { if (Item.Selected != value) { Item.Selected = value; Raise(); SelectionChanged?.Invoke(); } } }
    public string DependencyStatus => Item.DependencyStatus;
    public event Action? SelectionChanged;
}

public sealed class ProfileRow : ObservableObject
{
    public ProfileRow(UserProfileInfo item) { Item = item; }
    public UserProfileInfo Item { get; }
    public bool IsSelected { get => Item.Selected; set { if (Item.Selected != value) { Item.Selected = value; Raise(); SelectionChanged?.Invoke(); } } }
    public string LoadedText => Item.Loaded ? "Yes" : "No";
    public string AccessText => Item.Access.ToString();
    public string LastUsedText => Item.LastUsedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "unknown";
    public event Action? SelectionChanged;
}

public sealed class TreeNodeVM : ObservableObject
{
    static readonly TreeNodeVM Dummy = new();
    readonly MainViewModel? owner;
    bool expanded, selected, loaded;
    TreeNodeVM() { Node = new FolderNode(); }
    public TreeNodeVM(FolderNode node, MainViewModel owner)
    {
        Node = node; this.owner = owner;
        if (node.Children.Count > 0) Children.Add(Dummy);
        owner.RegisterTreeVm(this);
    }
    public FolderNode Node { get; }
    public System.Collections.ObjectModel.ObservableCollection<TreeNodeVM> Children { get; } = new();
    public string Name => Node.Name;
    public string Counts => Node.State switch
    {
        FolderState.Inaccessible => "INACCESSIBLE",
        FolderState.SkippedReparsePoint => "link not followed",
        _ when Node.IncludedEmpty => "empty folder (kept)",
        _ => $"{Node.SelectedCount}/{Node.EligibleCount} · {PathUtil.FormatBytes(Node.SelectedBytes)}"
    };
    public bool IsProblem => Node.State is FolderState.Inaccessible or FolderState.SkippedReparsePoint;
    public ImageSource? Icon => Node.IsCategory ? null : IconService.Get("", true, 16);
    public bool? IsChecked
    {
        get => Node.CheckState;
        set { owner?.SetFolderSelected(Node, value != false); }
    }
    public bool IsExpanded
    {
        get => expanded;
        set { if (Set(ref expanded, value) && value) EnsureChildren(); }
    }
    public bool IsSelected
    {
        get => selected;
        set { if (Set(ref selected, value) && value) owner?.NavigateTo(Node, fromTree: true); }
    }
    public void EnsureChildren()
    {
        if (loaded || owner == null) return;
        loaded = true;
        Children.Clear();
        foreach (var c in Node.Children.Values) Children.Add(new TreeNodeVM(c, owner));
    }
    public void Refresh() { Raise(nameof(IsChecked)); Raise(nameof(Counts)); }
    public void SetSelectedSilently(bool v) { selected = v; Raise(nameof(IsSelected)); }
}

public sealed class ExplorerItemVM : ObservableObject
{
    readonly MainViewModel owner;
    ImageSource? image;
    public ExplorerItemVM(ExplorerItem item, MainViewModel owner, int iconSize)
    {
        Item = item; this.owner = owner;
        var ext = item.IsFolder ? "" : Path.GetExtension(item.Name);
        image = IconService.Get(ext, item.IsFolder, iconSize);
        TypeText = item.IsFolder ? "Folder" : IconService.TypeName(ext);
    }
    public ExplorerItem Item { get; }
    public string Name => Item.Name;
    public string TypeText { get; }
    public string SizeText => Item.IsFolder ? (Item.Folder != null ? $"{Item.Folder.EligibleCount} files" : "") : PathUtil.FormatBytes(Item.Size);
    public string ModifiedText => Item.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public FileEntry? File => Item.IsFolder ? null : owner.Plan?.Index.Files[Item.FileIndex];
    public string ClassText => Item.IsFolder ? (Item.Folder?.State == FolderState.Inaccessible ? "Inaccessible" : "") : File?.Classification.ToString() ?? "";
    public string Note => Item.IsFolder ? Item.Folder?.Note ?? "" : File?.Note ?? "";
    public string ToolTip => (Item.FullPath ?? Item.Name) + (string.IsNullOrEmpty(Note) ? "" : "\n" + Note);
    public ImageSource? Image { get => image; set => Set(ref image, value); }
    public bool? IsChecked
    {
        get => Item.IsFolder ? Item.Folder?.CheckState : File?.Selected;
        set
        {
            if (Item.IsFolder && Item.Folder != null) owner.SetFolderSelected(Item.Folder, value != false);
            else if (!Item.IsFolder) owner.SetFileSelected(Item.FileIndex, value == true);
        }
    }
    public void Refresh() => Raise(nameof(IsChecked));
}

public sealed class ProfileMapRow : ObservableObject
{
    string? target;
    public UserProfileInfo Source { get; init; } = new();
    public int FileCount { get; init; }
    public List<string> Choices { get; init; } = new();
    public string? Target { get => target; set => Set(ref target, value); }
}

public sealed class ReadinessRow : ObservableObject
{
    readonly Action<ReadinessRow, bool> onConfirm;
    public ReadinessRow(ReadinessItem item, Action<ReadinessRow, bool> onConfirm) { Item = item; this.onConfirm = onConfirm; }
    public ReadinessItem Item { get; }
    public bool Confirmed { get => Item.ConfirmedByUser; set { onConfirm(this, value); } }
}

public sealed class CheckRow : ObservableObject
{
    bool done;
    public string Text { get; init; } = "";
    public bool Done { get => done; set => Set(ref done, value); }
}
