using Avalonia.Media;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui;

public enum PageKind { Mods, Profiles, Providers, Settings }

public sealed class NavigationItem(PageKind page, string label, string iconPath) : ViewModelBase
{
    private bool active;
    public PageKind Page { get; } = page;
    public string Label { get; } = label;
    public Geometry Icon { get; } = Geometry.Parse(iconPath);
    public bool IsActive { get => active; internal set => Set(ref active, value); }
    public Command OpenCommand { get; internal set; } = new(() => { });
}

public sealed class SidebarProfile(Profile profile, bool isActive, AsyncCommand selectCommand,
    AsyncCommand renameCommand, AsyncCommand deleteCommand) : ViewModelBase
{
    private Profile profile = profile;
    private bool active = isActive;
    private bool deployed;
    public Profile Profile => profile;
    public bool IsActive { get => active; internal set => Set(ref active, value); }
    public bool IsDeployed
    {
        get => deployed;
        internal set { if (Set(ref deployed, value)) Notify(nameof(Summary)); }
    }
    public AsyncCommand SelectCommand { get; } = selectCommand;
    public AsyncCommand RenameCommand { get; } = renameCommand;
    public AsyncCommand DeleteCommand { get; } = deleteCommand;
    public string Name => Profile.Name;
    public string Monogram => string.Concat(Profile.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => char.ToUpperInvariant(s[0])));
    public string Summary => $"{Name} · {ModPresentation.Count(Profile.Entries.Count(e => e.Enabled), "enabled mod")}{(IsDeployed ? " · Deployed" : "")}";
    internal void Update(Profile value, bool isActive, bool isDeployed)
    {
        if (Set(ref profile, value, nameof(Profile)))
        { Notify(nameof(Name)); Notify(nameof(Monogram)); Notify(nameof(Summary)); }
        IsActive = isActive; IsDeployed = isDeployed;
    }
}
