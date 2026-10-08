using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Settings;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class ArsenalImportTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettingsReviewsArsenalProfilesBeforeCopyingAndImportsTheirOrganization(bool accept)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var root = Path.Combine(f.Root, "arsenal"); Directory.CreateDirectory(root);
        var source = f.Source("Arsenal mod");
        var id = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(Path.Combine(root, "hd2a_data.json"), JsonSerializer.Serialize(new
        {
            selectedProfile = "imported",
            profileOrder = new[] { "imported" },
            modsLibrary = new[] { new { uuid = id, label = "Arsenal mod", path = source } },
            modsList = new Dictionary<string, object>
            {
                ["imported"] = new
                {
                    label = "Arsenal profile",
                    mods = new object[]{
                    new {uuid="separator",type="separator",label="Weapons"},
                    new {uuid=id,enabled=false}
                }
                }
            }
        }), TestContext.Current.CancellationToken);
        f.Shell.Navigate(PageKind.Settings);
        var model = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            model.Search = "Arsenal";
            Assert.True(view.FindControl<Border>("ImportsSettingsSection")!.IsVisible);
            var input = view.FindControl<TextBox>("ArsenalDirectoryInput")!;
            input.Text = root; Dispatcher.UIThread.RunJobs();
            f.Dialogs.Confirm = accept;
            await model.ImportArsenalCommand.ExecuteAsync();
            Assert.False(f.Services.Operations.IsError);
            var review = Assert.Single(f.Dialogs.Confirmations);
            Assert.Equal("Import Arsenal profiles", review.Title);
            Assert.Contains("Arsenal profile", review.Message);
            Assert.Contains("1 mods", review.Message);
            Assert.True(Directory.Exists(source));
            Assert.Equal(accept ? 1 : 0, f.Services.Session.State.Mods.Count);
            Assert.Equal(accept ? 2 : 1, f.Services.Session.State.Profiles.Count);
            if (accept)
            {
                var profile = f.Services.Session.ActiveProfile!;
                Assert.Equal("Arsenal profile", profile.Name);
                Assert.Equal("Weapons", Assert.Single(profile.Groups).Name);
                Assert.False(Assert.Single(profile.Entries).Enabled);
            }
        }
        finally { window.Close(); }
    }
}
