using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Mods;

public sealed class ModDetailsViewModel(Mod mod) : ViewModelBase
{
    public string Name => mod.Name;
    public string Description => string.IsNullOrWhiteSpace(mod.Description) ? "No description provided." : mod.Description;
    public string Version => mod.Version ?? "Not specified";
    public string ImportedAt => mod.ImportedAt.ToLocalTime().ToString("g");
    public string ResourceSummary => $"{ModPresentation.Count(mod.PatchSets.Count, "patch set")} · {ModPresentation.Count(mod.PatchSets.Sum(p => p.Resources.Count), "indexed resource")}";
    public IReadOnlyList<string> Archives => mod.PatchSets.Select(p => p.Archive).Distinct().ToArray();
    public IReadOnlyList<string> Files => mod.PatchSets.SelectMany(p => p.Files).Select(f => f.RelativePath).ToArray();
    public bool HasOptions => mod.Options.Count > 0;
}

public sealed class ModOptionsViewModel
{
    public IReadOnlyList<OptionEdit> Options { get; }
    public bool HasOptions => Options.Count > 0;
    public ModOptionsViewModel(Mod mod, IReadOnlyList<OptionSelection> selections)
    {
        Options = mod.Options.Select(o => new OptionEdit(o, selections.FirstOrDefault(s => s.OptionId == o.Id) ?? new(o.Id))).ToArray();
    }
    public IReadOnlyList<OptionSelection> Selections() => Options.Select(o => new OptionSelection(o.Id, o.Enabled, o.ChoiceIndex)).ToArray();
}

public sealed class OptionEdit : ViewModelBase
{
    private bool enabled;
    private int choiceIndex;
    public Guid Id { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<string> Choices { get; }
    public bool HasChoices => Choices.Count > 0;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public int ChoiceIndex { get => choiceIndex; set { if (value >= 0) Set(ref choiceIndex, value); } }
    public OptionEdit(ModOption option, OptionSelection selection)
    {
        Id = option.Id; Name = option.Name; Description = option.Description;
        Choices = option.Choices.Select(c => c.Name).ToArray(); enabled = selection.Enabled; choiceIndex = selection.ChoiceIndex;
    }
}
