using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui;

public enum PageKind { Mods, Profiles, Search, Downloads, Settings, ManualChecks }

public sealed class NavigationItem(PageKind page, string label, string iconFile) : ViewModelBase
{
    private bool active;
    private bool tourTarget;
    public bool IsTourTarget { get => tourTarget; internal set => Set(ref tourTarget, value); }
    public PageKind Page { get; } = page;
    public string Label => Localizer.Text(label);
    public string IconSource { get; } = $"avares://Quartermaster.Gui/Assets/{iconFile}";
    public bool IsActive { get => active; internal set => Set(ref active, value); }
    public Command OpenCommand { get; internal set; } = new(() => { });
}

public sealed class SidebarProfile(Profile profile, bool isActive, AsyncCommand selectCommand,
    AsyncCommand renameCommand, AsyncCommand deleteCommand, AsyncCommand exportCommand, AsyncCommand duplicateCommand) : ViewModelBase
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
    public AsyncCommand ExportCommand { get; } = exportCommand;
    public AsyncCommand DuplicateCommand { get; } = duplicateCommand;
    public string Name => Profile.Name;
    public string Monogram => string.Concat(Profile.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => char.ToUpperInvariant(s[0])));
    public string Summary => $"{Name} · {ModPresentation.Count(Profile.Entries.Count(e => e.Enabled), "enabled mod")}{(IsDeployed ? Localizer.Text(" · Deployed") : "")}";
    internal void Update(Profile value, bool isActive, bool isDeployed)
    {
        if (Set(ref profile, value, nameof(Profile)))
        { Notify(nameof(Name)); Notify(nameof(Monogram)); Notify(nameof(Summary)); }
        IsActive = isActive; IsDeployed = isDeployed;
    }
}
