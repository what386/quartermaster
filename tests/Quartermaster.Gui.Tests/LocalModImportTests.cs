using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Shared;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class LocalModImportTests
{
    [AvaloniaTheory]
    [InlineData("zip")]
    [InlineData("add")]
    [InlineData("folder")]
    [InlineData("drop")]
    public async Task LocalImportsSaveThePageFromTheConfirmation(string mode)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var source = mode == "folder" ? f.Source("Mod") : f.Zip("Mod");
        f.Dialogs.ImportOptions = new("https://mods.example/mod");
        if (mode == "drop")
            await f.Shell.ImportDropsAsync([source], f.Services.Session.ActiveProfile!.Id);
        else
        {
            f.Shell.NavigationItems.Single(item => item.Page == PageKind.Mods).OpenCommand.Execute(null);
            var mods = Assert.IsType<ModsViewModel>(f.Shell.CurrentPage);
            f.Dialogs.ZipPath = source; f.Dialogs.FolderPath = source;
            if (mode == "add") { f.Dialogs.ModImport = new(ModImportKind.Zip); await mods.AddModCommand.ExecuteAsync(); }
            else if (mode == "folder") await mods.ImportFolderCommand.ExecuteAsync();
            else await mods.ImportZipCommand.ExecuteAsync();
        }
        Assert.False(f.Services.Operations.IsError);
        Assert.Equal(source, Assert.Single(f.Dialogs.ImportConfirmations));
        var mod = Assert.Single((await f.Services.Library.LoadAsync()).Mods);
        Assert.Equal("https://mods.example/mod", mod.PageLink);
        Assert.Equal(mod.PageLink, Assert.Single(f.Services.Session.State.Mods).PageLink);
        if (mode == "drop") Assert.Equal(mod.Id, Assert.Single(f.Services.Session.ActiveProfile!.Entries).ModId);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingTheConfirmationDoesNotImport(bool drop)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.ImportOptions = null;
        var source = f.Zip("Cancelled mod");
        if (drop) await f.Shell.ImportDropsAsync([source], f.Services.Session.ActiveProfile!.Id);
        else await f.Services.Session.ImportAsync(source, CancellationToken.None);
        Assert.Single(f.Dialogs.ImportConfirmations);
        Assert.Empty((await f.Services.Library.LoadAsync()).Mods);
        Assert.Empty(f.Services.Session.ActiveProfile!.Entries);
    }

    [AvaloniaTheory]
    [InlineData("", true)]
    [InlineData("  https://mods.example/mod  ", true)]
    [InlineData("http://mods.example/mod", false)]
    [InlineData("not a URL", false)]
    public void ConfirmationValidatesAnOptionalUrlBeforeAccepting(string page, bool valid)
    {
        var dialog = new ModImportDialog("/tmp/mod.zip");
        Assert.Equal("mod.zip", dialog.FindControl<TextBlock>("SourceText")!.Text);
        dialog.FindControl<TextBox>("PageInput")!.Text = page;
        Dispatcher.UIThread.RunJobs();
        object? result = null;
        dialog.Completed += value => result = value;
        Assert.Equal(valid, dialog.TryAccept());
        Assert.Equal(valid, dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
        if (valid) Assert.Equal(string.IsNullOrWhiteSpace(page) ? null : page.Trim(), Assert.IsType<LocalModImportOptions>(result).PageLink);
        else Assert.Null(result);
    }
}
