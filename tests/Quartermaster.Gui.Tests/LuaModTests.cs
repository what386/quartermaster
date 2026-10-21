using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class LuaModTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LuaIconIdentifiesPackedScriptsAndRefreshesRetainedRows(bool library)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var source = f.Source("Script mod");
        var patch = Directory.GetFiles(source, "*.patch_7").Single();
        var bytes = await File.ReadAllBytesAsync(patch);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80), 0xa14e8dfa2cd117e2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), 0xa14e8dfa2cd117e2);
        await File.WriteAllBytesAsync(patch, bytes);
        await f.Services.Session.ImportAsync(source, CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Plain mod", 2), CancellationToken.None);
        var profile = f.Services.Session.ActiveProfile!;
        foreach (var mod in f.Services.Session.State.Mods) profile = ProfileEditor.Add(profile, mod);
        await f.Services.Session.SaveProfileAsync(profile, false, CancellationToken.None);
        f.Shell.Navigate(library ? PageKind.Mods : PageKind.Profiles);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var rows = window.GetVisualDescendants().OfType<ModRowView>()
                .ToDictionary(view => ((IModRow)view.DataContext!).Name);
            var icon = rows["Script mod"].FindControl<Border>("LuaIcon")!;
            Assert.True(icon.IsVisible);
            Assert.Equal("Contains Lua scripts", ToolTip.GetTip(icon));
            Assert.False(rows["Plain mod"].FindControl<Border>("LuaIcon")!.IsVisible);
            var ordinaryPatches = f.Services.Session.State.Mods.Single(mod => mod.Name == "Plain mod").PatchSets;
            // Recycled rows must refresh their badge when their mod data changes.
            switch (rows["Script mod"].DataContext)
            {
                case ModListItem item:
                    item.Update(new(item.Mod with { PatchSets = ordinaryPatches }, item.Index, item.HasConflict));
                    break;
                case ProfileModItem item:
                    item.Update(new(item.Mod with { PatchSets = ordinaryPatches }, item.Entry, item.Index, item.EnableCommand, item.HasConflict));
                    break;
            }
            window.CaptureRenderedFrame()?.Dispose();
            Assert.False(icon.IsVisible);
        }
        finally { window.Close(); }
    }
}
