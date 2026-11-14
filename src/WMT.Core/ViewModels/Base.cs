using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WMT.Core.ViewModels;

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
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    readonly Action<object?> exec;
    readonly Func<bool>? can;
    public RelayCommand(Action<object?> exec, Func<bool>? can = null) { this.exec = exec; this.can = can; }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => can?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) exec(parameter); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class SelectableSoftware : ObservableObject
{
    bool selected;
    public SelectableSoftware(SoftwareItem item) { Item = item; }
    public SoftwareItem Item { get; }
    public string Key => $"{Item.Name}|{Item.Version}|{Item.Architecture}";
    public bool IsSelected { get => selected; set { if (Set(ref selected, value)) SelectionChanged?.Invoke(); } }
    public event Action? SelectionChanged;
}

public sealed class SelectableProfile : ObservableObject
{
    bool selected;
    public SelectableProfile(UserProfileItem item) { Item = item; }
    public UserProfileItem Item { get; }
    public string LastUsedText => Item.LastUsedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "unknown";
    public string LoadedText => Item.Loaded ? "Yes" : "No";
    public bool IsSelected { get => selected; set { if (Set(ref selected, value)) SelectionChanged?.Invoke(); } }
    public event Action? SelectionChanged;
}
