using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Search;
using Quartermaster.Gui.Settings;
using Quartermaster.Gui.Shared;
using Avalonia.Media.Imaging;
using Quartermaster.Library.Profiles;
using Quartermaster.Providers.Downloads;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class ModDownloadTests
{
    [AvaloniaFact]
    public async Task AutomaticDownloadImportFailureShowsDismissibleFloatingError()
    {
        using var api = Api(); using var f = new Fixture(nexusApi: api); await ConfigureAsync(f);
        var mods = (ModsViewModel)Navigate(f, PageKind.Mods);
        f.Dialogs.ModImport = new(ModImportKind.Link, "https://www.nexusmods.com/helldivers2/mods/123");
        await mods.AddModCommand.ExecuteAsync();
        var job = Assert.Single(f.Services.Providers.State.Jobs);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var bounds = window.FindControl<ContentControl>("PageHost")!.Bounds;
            await File.WriteAllBytesAsync(Path.Combine(f.Services.Providers.State.Directories[0], "broken.zip"), [1, 2, 3]);
            await f.Services.Providers.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()?.Dispose();
            var operations = f.Services.Operations;
            Assert.True(operations.IsErrorNotificationVisible);
            Assert.Contains("Example mod", operations.NotificationMessage);
            Assert.Equal(DownloadStatus.Failed, Assert.Single(f.Services.Providers.State.Jobs).Status);
            Assert.Empty(f.Services.Session.State.Mods);
            Assert.Equal(bounds, window.FindControl<ContentControl>("PageHost")!.Bounds);
            var overlay = window.FindControl<ErrorNotification>("ErrorOverlay")!;
            var point = overlay.TranslatePoint(new Point(10, 10), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.False(operations.IsErrorNotificationVisible);
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(f.Services.Providers.State.Jobs).Error));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FloatingErrorAutomaticallyDismissesWithoutClearingFailure()
    {
        var operations = new OperationState();
        var overlay = new ErrorNotification { DataContext = operations, DismissAfter = TimeSpan.FromMilliseconds(30) };
        var window = new Window { Content = overlay }; window.Show();
        try
        {
            var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operations.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OperationState.IsErrorNotificationVisible) && !operations.IsErrorNotificationVisible) dismissed.TrySetResult();
            };
            operations.ReportError(new IOException("Invalid archive"));
            await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(operations.IsError); Assert.Equal("Invalid archive", operations.Message);
            operations.ShowErrorNotification("Another error"); Assert.True(operations.IsErrorNotificationVisible);
            operations.DismissErrorCommand.Execute(null); Assert.False(operations.IsErrorNotificationVisible);
        }
        finally { window.Close(); }
    }

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
            var image = Assert.Single(window.GetVisualDescendants().OfType<RemoteImage>());
            Assert.Equal(104, image.GetVisualAncestors().OfType<Border>().First().Bounds.Height);
            Assert.Contains(image.GetVisualAncestors().OfType<Border>(), border => border.MinHeight == 120);
            await result.AddCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
            Assert.Equal(Assert.Single(f.Services.Providers.State.Jobs).File.DownloadPage, opened);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task SearchTextboxSubmitsWithEnterAndButtonAndSelectsNexusByDefault()
    {
        var requests = 0;
        using var api = new HttpClient(new Handler(_ =>
        {
            requests++;
            return Json(new { data = new { mods = new { nodes = new[] { new { modId = 123, name = "Example mod", summary = "Description", version = "2" } } } } });
        }));
        using var f = new Fixture(nexusApi: api); await ConfigureAsync(f);
        var search = (SearchViewModel)Navigate(f, PageKind.Search);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<SearchView>());
            var input = view.FindControl<TextBox>("SearchInput")!;
            var button = view.FindControl<Button>("SearchButton")!;
            var selector = view.FindControl<ComboBox>("ProviderSelector")!;
            Assert.Equal("nexusmods", Assert.IsType<SearchProvider>(selector.SelectedItem).Id);
            Assert.Same(search.SearchCommand, button.Command);
            Assert.False(button.IsEffectivelyEnabled);
            input.Text = "Example"; Dispatcher.UIThread.RunJobs();
            Assert.Equal("Example", search.Query); Assert.True(button.IsEffectivelyEnabled);
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            input.RaiseEvent(enter); Assert.True(enter.Handled);
            await f.Services.Operations.WhenIdle; Dispatcher.UIThread.RunJobs();
            Assert.Single(search.Results); Assert.Equal("1 results", view.FindControl<TextBlock>("SearchFeedback")!.Text);
            Assert.Equal(1, requests);
            input.Text = "Another"; Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Dispose();
            var point = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await f.Services.Operations.WhenIdle; Dispatcher.UIThread.RunJobs();
            Assert.Single(search.Results); Assert.False(f.Services.Operations.IsError); Assert.Equal(2, requests);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task SearchShowsProgressEmptyResultsAndFailuresOnThePage()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>();
        var started = new TaskCompletionSource();
        using var api = new HttpClient(new AsyncHandler(_ => { started.SetResult(); return response.Task; }));
        using var f = new Fixture(nexusApi: api); await ConfigureAsync(f);
        var search = (SearchViewModel)Navigate(f, PageKind.Search); search.Query = "Example";
        var task = search.SearchCommand.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Searching Nexus Mods…", search.ResultSummary);
        response.SetResult(Json(new { data = new { mods = new { nodes = Array.Empty<object>() } } }));
        await task;
        Assert.Empty(search.Results); Assert.Equal("No matching mods.", search.ResultSummary);
        search.SelectedProvider = null;
        Assert.False(search.SearchCommand.CanExecute(null));
        search.SelectedProvider = search.Providers[0];
        await f.Services.Keys.SetAsync("nexusmods", null);
        await search.SearchCommand.ExecuteAsync();
        Assert.Contains("Search failed:", search.ResultSummary); Assert.Contains("Settings", search.ResultSummary);
        Assert.Empty(search.Results); Assert.True(f.Services.Operations.IsError);
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request).WaitAsync(ct); }
    [AvaloniaFact]
    public async Task RemoteThumbnailLoadsWithoutCredentialsAndReleasesBitmapOnDetach()
    {
        using var pixels = new WriteableBitmap(new PixelSize(24, 24), new Vector(96, 96));
        using var bytes = new MemoryStream(); pixels.Save(bytes, new PngBitmapEncoderOptions());
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++; Assert.False(request.Headers.Contains("apikey")); Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) };
        }));
        var image = new RemoteImage(http) { SourceUri = new("https://images.example/mod.png"), Width = 104, Height = 104 };
        var loaded = new TaskCompletionSource();
        image.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty && image.Source is not null) loaded.TrySetResult(); };
        var window = new Window { Content = image }; window.Show();
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.IsType<Bitmap>(image.Source);
            window.Content = null; Assert.Null(image.Source);
            image.SourceUri = new("file:///tmp/unsafe.png"); window.Content = image;
            Assert.Null(image.Source); Assert.Equal(1, requests);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task BrowserDownloadSpinnerTracksMultipleJobsWithoutBlockingTheApp()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var first = new Quartermaster.Providers.Providers.ProviderFile("nexusmods", "123", "456", "First", "first.zip", "1",
            new("https://www.nexusmods.com/helldivers2/mods/123?tab=files&file_id=456"));
        var a = await f.Services.Providers.QueueAsync(first);
        var b = await f.Services.Providers.QueueAsync(first with { FileId = "457", Name = "Second", FileName = "second.zip",
            DownloadPage = new("https://www.nexusmods.com/helldivers2/mods/123?tab=files&file_id=457") });
        Dispatcher.UIThread.RunJobs();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var overlay = window.FindControl<Border>("DownloadProgressOverlay")!;
            Assert.True(overlay.IsVisible); Assert.Single(overlay.GetVisualDescendants().OfType<BusySpinner>(), spinner => spinner.IsVisible);
            Assert.Equal("Waiting for 2 browser downloads", f.Services.Downloads.PendingSummary);
            Assert.Contains("Finish the download in your browser", f.Services.Downloads.PendingExplanation);
            Assert.False(f.Services.Operations.IsBusy);
            await f.Services.Providers.CancelAsync(a.Id); Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsVisible); Assert.Equal("Waiting for 1 browser download", f.Services.Downloads.PendingSummary);
            await f.Services.Providers.CancelAsync(b.Id); Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsVisible); Assert.False(f.Services.Downloads.HasPendingDownloads);
            var dialog = new ModFilesDialog(new("123", "Example", "Description", "1", first.DownloadPage, [first]));
            Assert.Equal("Open download page", dialog.FindControl<Button>("AcceptButton")!.Content);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task DownloadsTabCanAttachExistingZipAndRemoveImportedHistory()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Providers.SetDirectoriesAsync([Path.Combine(f.Root, "empty")]);
        var file = new Quartermaster.Providers.Providers.ProviderFile("nexusmods", "123", "456", "Already downloaded", "expected.zip", "1",
            new("https://www.nexusmods.com/helldivers2/mods/123?tab=files&file_id=456"));
        await f.Services.Providers.QueueAsync(file, f.Services.Session.ActiveProfile!.Id); Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(f.Services.Downloads.Jobs); f.Shell.Navigate(PageKind.Downloads);
        Assert.Same(f.Services.Downloads, f.Shell.CurrentPage);
        f.Dialogs.ZipPath = f.Zip("Existing mod");
        await row.AttachCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
        Assert.False(f.Services.Operations.IsError); Assert.False(f.Services.Downloads.HasPendingDownloads);
        Assert.Single(f.Services.Session.State.Mods); Assert.Single(f.Services.Session.ActiveProfile!.Entries);
        Assert.True(File.Exists(f.Dialogs.ZipPath)); Assert.True(row.RemoveCommand.CanExecute(null));
        await row.RemoveCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
        Assert.Empty(f.Services.Downloads.Jobs); Assert.Single(f.Services.Session.State.Mods);
        Assert.Single(f.Services.Session.ActiveProfile.Entries);
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
