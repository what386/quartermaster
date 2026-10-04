using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Search;
using Quartermaster.Gui.Settings;
using Quartermaster.Library.Profiles;
using Quartermaster.Providers.Downloads;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class ModDownloadTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object ModInfo => new { mod_id = 123, name = "Example mod", summary = "Example description", version = "2", available = true };
    private static object ModFile(int id, int category = 1) => new { file_id = id, name = "Main file", file_name = "mod.zip", version = "2", category_id = category, is_primary = true };
    private static HttpClient Api() => new(new Handler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("validate.json")) return Json(new { user_id = 7, name = "Example user", is_premium = false });
        if (path == "/v2/graphql") return Json(new { data = new { mods = new { nodes = new[] { new { modId = 123, name = "Example mod", summary = "Example description", version = "2" } } } } });
        if (path.Contains("md5_search")) return Json(new[] { new { mod = ModInfo, file_details = ModFile(20) } });
        if (path.EndsWith("files.json")) return Json(new { files = new[] { ModFile(10, 4), ModFile(20) }, file_updates = new[] { new { old_file_id = 10, new_file_id = 20 } } });
        return Json(ModInfo);
    }));
    private static async Task ConfigureAsync(Fixture f)
    {
        await f.Shell.InitializeAsync(); await f.Services.Keys.SetAsync("nexusmods", "test-key");
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await f.Services.Providers.SetDirectoriesAsync([folder]);
        await ((SettingsViewModel)Navigate(f, PageKind.Settings)).InitializeProviderSettingsAsync(CancellationToken.None);
    }
    private static object Navigate(Fixture f, PageKind page) { f.Shell.Navigate(page); return f.Shell.CurrentPage; }

    [AvaloniaFact]
    public async Task AddModDispatchesGenericLinkSelectsFileAndQueuesBrowserDownload()
    {
        using var api = Api(); Uri? opened = null;
        using var f = new Fixture(openBrowser: uri => opened = uri, nexusApi: api); await ConfigureAsync(f);
        var mods = (ModsViewModel)Navigate(f, PageKind.Mods);
        f.Dialogs.ModImport = new(ModImportKind.Link, "https://www.nexusmods.com/helldivers2/mods/123");
        await mods.AddModCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
        Assert.False(f.Services.Operations.IsError);
        var job = Assert.Single(f.Services.Providers.State.Jobs);
        Assert.Equal("20", job.File.FileId); Assert.Equal("Example mod", job.File.Name);
        Assert.Equal(job.File.DownloadPage, opened); Assert.Equal(DownloadStatus.Waiting, job.Status);
        Assert.Empty(f.Services.Session.State.Mods); Assert.False(f.Services.Operations.IsBusy);
        await mods.AddModCommand.ExecuteAsync(); Assert.Single(f.Services.Providers.State.Jobs);
        f.Dialogs.ModImport = new(ModImportKind.Link, "https://unsupported.example/mod/123"); opened = null;
        await mods.AddModCommand.ExecuteAsync(); Assert.True(f.Services.Operations.IsError);
        Assert.Contains("No installed provider", f.Services.Operations.Message); Assert.Null(opened); Assert.Single(f.Services.Providers.State.Jobs);
    }
    [AvaloniaFact]
    public async Task AddModStillImportsLocalFilesAndCancelledFileSelectionDoesNotQueue()
    {
        using var api = Api(); using var f = new Fixture(nexusApi: api); await ConfigureAsync(f);
        var mods = (ModsViewModel)Navigate(f, PageKind.Mods);
        f.Dialogs.ModImport = new(ModImportKind.Zip); f.Dialogs.ZipPath = f.Zip("Cape"); await mods.AddModCommand.ExecuteAsync();
        Assert.Single(mods.Mods);
        f.Dialogs.ModImport = new(ModImportKind.Folder); f.Dialogs.FolderPath = f.Source("Armor", 2); await mods.AddModCommand.ExecuteAsync();
        Assert.Equal(2, mods.Mods.Count);
        f.Dialogs.ModImport = new(ModImportKind.Link, "https://nexusmods.com/helldivers2/mods/123"); f.Dialogs.CancelModFile = true;
        await mods.AddModCommand.ExecuteAsync(); Assert.Empty(f.Services.Providers.State.Jobs);
    }
    [AvaloniaFact]
    public async Task SearchHasItsOwnPageAndUsesTheSameLinkImportFlow()
    {
        using var api = Api(); Uri? opened = null;
        using var f = new Fixture(nexusApi: api, openBrowser: uri => opened = uri); await ConfigureAsync(f);
        Assert.DoesNotContain(f.Shell.NavigationItems, item => item.Label == "Providers");
        var search = (SearchViewModel)Navigate(f, PageKind.Search); search.Query = "Example";
        await search.SearchCommand.ExecuteAsync(); var result = Assert.Single(search.Results);
        Assert.Equal("Example mod · 2", result.Title);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose(); Assert.Single(window.GetVisualDescendants().OfType<SearchView>());
            await result.AddCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
            Assert.Equal(Assert.Single(f.Services.Providers.State.Jobs).File.DownloadPage, opened);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task SettingsSingleSaveValidatesAndStoresMaskedKeyAndDownloadFolder()
    {
        using var api = Api(); using var f = new Fixture(nexusApi: api); await f.Shell.InitializeAsync();
        var settings = (SettingsViewModel)Navigate(f, PageKind.Settings);
        var folder = Path.Combine(f.Root, "browser-downloads"); Directory.CreateDirectory(folder);
        settings.NexusApiKey = "test-personal-key"; settings.DownloadFolder = folder;
        Assert.True(settings.SaveCommand.CanExecute(null));
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Save"));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(), box => box.PasswordChar == '●');
            await settings.SaveCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
            Assert.Equal("test-personal-key", await f.Services.Keys.GetAsync("nexusmods")); Assert.Equal("", settings.NexusApiKey);
            Assert.Contains("Example user", settings.NexusAccount); Assert.Equal(folder, Assert.Single(f.Services.Providers.State.Directories));
            Assert.DoesNotContain("test-personal-key", await File.ReadAllTextAsync(Path.Combine(f.Data, "settings.json")));
            settings.Search = "nexus"; Assert.True(settings.ShowNexus); Assert.False(settings.ShowRepatch);
            settings.RemoveNexusKey = true; await settings.SaveCommand.ExecuteAsync(); Assert.Null(await f.Services.Keys.GetAsync("nexusmods"));
            Assert.False(settings.HasSavedNexusKey);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task InvalidKeyDoesNotOverwriteCredentialsOrApplyOtherDraftSettings()
    {
        using var api = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized)));
        using var f = new Fixture(nexusApi: api); await f.Shell.InitializeAsync(); await f.Services.Keys.SetAsync("nexusmods", "previous-key");
        var settings = (SettingsViewModel)Navigate(f, PageKind.Settings);
        settings.NexusApiKey = "invalid-key"; settings.RepatchChoice = (int)Quartermaster.Gui.Services.RepatchMode.Automatic;
        await settings.SaveCommand.ExecuteAsync(); Assert.True(f.Services.Operations.IsError);
        Assert.Equal("previous-key", await f.Services.Keys.GetAsync("nexusmods"));
        Assert.Equal(Quartermaster.Gui.Services.RepatchMode.Ask, f.Services.Session.Settings.Repatch);
        Assert.DoesNotContain("invalid-key", f.Services.Operations.Message);
    }
    [AvaloniaFact]
    public async Task UpdateButtonsAppearAfterCheckingAndBrowserDownloadUpgradesProfiles()
    {
        using var api = Api(); Uri? opened = null;
        using var f = new Fixture(nexusApi: api, openBrowser: uri => opened = uri); await ConfigureAsync(f);
        await f.Services.Session.ImportAsync(f.Source("Old", 1), CancellationToken.None);
        var old = Assert.Single(f.Services.Session.State.Mods);
        await f.Services.Library.SetSourcesAsync(old.Id, [new("nexusmods", "123", "10", "1")]);
        var profile = ProfileEditor.Add(f.Services.Session.ActiveProfile!, old);
        profile = ProfileEditor.AddGroup(profile, "Equipment", [old.Id]); profile = ProfileEditor.SetEnabled(profile, old.Id, false);
        await f.Services.Session.SaveProfileAsync(profile, true, CancellationToken.None);
        var mods = (ModsViewModel)Navigate(f, PageKind.Mods); Assert.False(Assert.Single(mods.Mods).HasUpdate);
        await mods.CheckUpdatesCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
        Assert.True(Assert.Single(mods.Mods).HasUpdate); Assert.Empty(f.Services.Providers.State.Jobs); Assert.Null(opened);
        var profiles = (ProfilesViewModel)Navigate(f, PageKind.Profiles); Assert.True(Assert.Single(profiles.Entries).HasUpdate);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "UpdateModButton" && b.IsVisible);
            var row = Assert.IsType<ProfileModItem>(button.DataContext);
            Assert.Same(row.UpdateCommand, button.Command);
            Assert.True(button.IsVisible); Assert.Equal("Update", button.Content);
            await row.UpdateCommand!.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
            var job = Assert.Single(f.Services.Providers.State.Jobs); Assert.Equal(old.Id, job.ReplacesModId);
            Assert.Equal(job.File.DownloadPage, opened); Assert.False(row.UpdateCommand.CanExecute(null));
            var zip = f.Zip("Updated", 2);
            File.Copy(zip, Path.Combine(f.Services.Providers.State.Directories[0], "downloaded (1).zip"));
            await f.Services.Providers.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(10));
            await f.Services.Session.ReloadAsync(CancellationToken.None); Dispatcher.UIThread.RunJobs();
            Assert.Equal(DownloadStatus.Complete, Assert.Single(f.Services.Providers.State.Jobs).Status);
            var entry = Assert.Single(f.Services.Session.ActiveProfile!.Entries);
            Assert.NotEqual(old.Id, entry.ModId); Assert.False(entry.Enabled); Assert.Equal(profile.Groups[0].Id, entry.GroupId);
            var updated = f.Services.Session.State.Mods.Single(m => m.Id == entry.ModId);
            Assert.Equal("20", Assert.Single(updated.Sources).FileId);
            Assert.False(Assert.Single(profiles.Entries).HasUpdate);
            Assert.False(((ModsViewModel)Navigate(f, PageKind.Mods)).Mods.Single(m => m.Mod.Id == old.Id).HasUpdate);
            Assert.Empty(Directory.GetFiles(f.Game, "*.patch_*"));
        }
        finally { window.Close(); }
    }
}
