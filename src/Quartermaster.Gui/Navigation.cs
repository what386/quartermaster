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

public sealed record SidebarProfile(Profile Profile, bool IsActive, AsyncCommand SelectCommand,
    AsyncCommand RenameCommand, AsyncCommand DeleteCommand)
{
    public string Name => Profile.Name;
    public string Monogram => string.Concat(Profile.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => char.ToUpperInvariant(s[0])));
    public string Summary => $"{Name} · {ModPresentation.Count(Profile.Entries.Count(e => e.Enabled), "enabled mod")}";
}
