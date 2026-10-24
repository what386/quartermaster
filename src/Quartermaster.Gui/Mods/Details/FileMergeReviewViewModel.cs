using Quartermaster.FileMerger;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Mods;

public sealed record FileMergeParticipant(string Name);
public sealed record FileMergeReviewItem(string Winner, IReadOnlyList<FileMergeParticipant> OtherMods, int ValueCount, bool WholeFile)
{
    public string Title => Localizer.Text(WholeFile ? "Whole-file replacement" : "Overlapping config edits");
    public string WinnerLabel => Localizer.Text(WholeFile ? "Applied mod" : "Winning mod");
    public string OtherLabel => Localizer.Text(WholeFile ? "Skipped mods" : "Overridden mods");
    public string Summary => WholeFile
        ? Localizer.Text("Only the applied mod is installed for this file. The skipped mods cannot be combined with its file layout.")
        : Localizer.Text("These mods edit the same parts of this config file. The winning mod replaces overlapping edits from the overridden mods. Other edits are kept.");
}
public sealed record FileMergeReviewGroup(string Target, IReadOnlyList<FileMergeReviewItem> Items)
{
    public string FileName => Path.GetFileName(Target);
}

public sealed class FileMergeReviewViewModel
{
    public IReadOnlyList<FileMergeReviewGroup> Groups { get; }
    public FileMergeReviewViewModel(FileMergePlan plan)
    {
        var targets = plan.Conflicts.Select(conflict => conflict.Target).Concat(plan.Skipped.Select(skip => skip.Target))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Groups = targets.Select(target => new FileMergeReviewGroup(target,
            plan.Conflicts.Where(conflict => conflict.Target == target).GroupBy(conflict => (conflict.WinningMod, conflict.WinningSource))
                .Select(group => new FileMergeReviewItem(group.Key.WinningMod,
                    group.Select(conflict => conflict.LosingMod).Distinct(StringComparer.Ordinal).Select(name => new FileMergeParticipant(name)).ToArray(),
                    group.Select(conflict => (conflict.BlockIndex, conflict.Offset)).Distinct().Count(), false))
                .Concat(plan.Skipped.Where(skip => skip.Target == target).GroupBy(skip => (skip.WinningMod, skip.WinningSource))
                    .Select(group => new FileMergeReviewItem(group.Key.WinningMod ?? Localizer.Text("Highest-priority mod"),
                        group.Select(skip => skip.Name).Distinct(StringComparer.Ordinal).Select(name => new FileMergeParticipant(name)).ToArray(), 0, true))).ToArray())).ToArray();
    }
}
