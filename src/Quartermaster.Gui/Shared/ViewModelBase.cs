using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Quartermaster.Gui.Shared;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
    protected void NotifyChanges(IEnumerable<(string Name, object? Before, object? After)> changes)
    {
        foreach (var (name, before, after) in changes)
            if (!Equals(before, after)) Notify(name);
    }
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public abstract class SessionViewModel(Quartermaster.Gui.Services.AppServices services) : ViewModelBase
{
    protected Quartermaster.Gui.Services.AppServices Services { get; } = services;
    protected Quartermaster.Gui.Services.LibrarySession Session => Services.Session;
    public OperationState Operations => Services.Operations;
    protected void WatchSession()
    {
        Session.Changed += (_, _) => Refresh();
        Refresh();
    }
    protected abstract void Refresh();
}
