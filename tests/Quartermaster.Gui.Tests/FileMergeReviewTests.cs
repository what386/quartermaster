using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Quartermaster.FileMerger;
using Quartermaster.Gui.Mods;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class FileMergeReviewTests
{
    [AvaloniaFact]
    public void ReviewSeparatesFinalWinnersOverriddenAndSkippedModsAndRequiresAcceptance()
    {
        var plan = new FileMergePlan("game", null, [],
            [new("data/game/generated_entities.dl_bin", 1, 0, 0, "First", "Winner"),
             new("data/game/generated_entities.dl_bin", 1, 0, 0, "Middle", "Winner")],
            [new("data/game/generated_weapons.dl_bin", "Skipped", "Different layout") { WinningMod = "Replacement" }]);
        var model = new FileMergeReviewViewModel(plan);
        Assert.Equal(2, model.Groups.Count);
        var conflicts = Assert.Single(model.Groups[0].Items);
        Assert.Equal("Winner", conflicts.Winner);
        Assert.Equal(new[] { "First", "Middle" }, conflicts.OtherMods.Select(mod => mod.Name));
        Assert.Equal(1, conflicts.ValueCount);
        Assert.Equal("Overlapping config edits", conflicts.Title);
        Assert.Contains("same parts of this config file", conflicts.Summary);
        Assert.DoesNotContain("conflicting values", conflicts.Summary);
        var replacement = Assert.Single(model.Groups[1].Items);
        Assert.Equal("Replacement", replacement.Winner);
        Assert.Equal("Skipped", Assert.Single(replacement.OtherMods).Name);
        Assert.True(replacement.WholeFile);
        using var f = new Fixture();
        var dialog = new FileMergeReviewDialog(model);
        var window = new MainWindow { Content = dialog }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var labels = dialog.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert.Contains("Winning mod", labels);
            Assert.Contains("Overridden mods", labels);
            Assert.Contains("Skipped mods", labels);
            Assert.Contains("Winner", labels);
            Assert.Contains("First", labels);
            Assert.Contains("Middle", labels);
            Assert.Contains("Replacement", labels);
            Assert.Contains("Skipped", labels);
            bool? accepted = null;
            dialog.Completed += result => accepted = (bool?)result;
            Assert.Null(accepted);
            Assert.True(dialog.TryAccept());
            Assert.True(accepted);
            dialog.Cancel();
            Assert.False(accepted);
        }
        finally { window.Close(); }
    }
}
