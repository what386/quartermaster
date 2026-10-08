using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Search;
using Quartermaster.Gui.Mods;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Providers.Downloads;
using Xunit;

namespace Quartermaster.Gui.Tests;

public class NexusMetadataTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    private sealed class NexusServer
    {
        public Dictionary<int, byte[]> Archives { get; } = [];
        public bool External { get; set; }
        public bool FailScans { get; set; }
        public bool Updated { get; set; }
        public bool FileRequirements { get; set; }
        public List<string> RequirementFiles { get; } = [];
        public int[] RootDependencies { get; set; } = [2, 1];
        public async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v3/games/helldivers2/mod-file-versions/"))
            {
                RequirementFiles.Add(Path.GetFileName(path));
                return Json(new { data = new { id = "version-" + Path.GetFileName(path) } });
            }
            if (path.StartsWith("/v3/mod-file-versions/"))
            {
                object Candidate(int id) => new { id = "file-" + id, name = "Mod" + id,
                    mod = new { id = "mod-" + id, game_scoped_id = id.ToString(), name = "Mod" + id,
                        game = new { domain_name = "helldivers2" } },
                    candidate_versions = new[] { new { game_scoped_id = (id * 10).ToString(), category = "main" } } };
                return Json(new { dependencies = new[] {
                    new { id = "loader", candidate_mod_files = new[] { Candidate(2) } },
                    new { id = "installer", candidate_mod_files = new[] { Candidate(4) } } } });
            }
            if (path.EndsWith("validate.json")) return Json(new { user_id = 7, name = "User", is_premium = true });
            if (path.EndsWith("download_link.json")) return Json(new[] { new { URI = "https://cdn.example/" + path.Split('/')[^4] } });
            if (path == "/v2/graphql")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var query = body.RootElement.GetProperty("query").GetString()!;
                var variables = body.RootElement.GetProperty("variables");
                if (query.Contains("game(domainName")) return Json(new { data = new { game = new { id = 7184 } } });
                if (query.Contains("modRequirements"))
                {
                    var id = int.Parse(variables.GetProperty("mod").GetString()!);
                    var deps = id == 3 ? RootDependencies : id == 2 ? new[] { 1, 3 } : Array.Empty<int>();
                    var nodes = deps.Select(dep => (object)new { modName = "Mod" + dep, modId = dep.ToString(), gameId = "7184",
                        url = $"https://www.nexusmods.com/helldivers2/mods/{dep}", notes = "Required", externalRequirement = false }).ToList();
                    if (External && id == 3) nodes.Add(new { modName = "External tool", modId = "0", gameId = "0",
                        url = "https://example.com/tool", notes = "Install separately", externalRequirement = true });
                    return Json(new { data = new { mod = new { legacyModRequirementsEnabled = !(FileRequirements && id == 3),
                        modRequirements = new { nexusRequirements = new { totalCount = FileRequirements && id == 3 ? 0 : nodes.Count,
                            nodes = FileRequirements && id == 3 ? new List<object>() : nodes } } } } });
                }
                if (query.Contains("modFiles"))
                {
                    if (FailScans) return Json(new { errors = new[] { new { message = "Unavailable" } } });
                    return Json(new { data = new
                    {
                        m0 = new[] { new { categoryId = 1, detectedFileExtension = "zip", scannedV2 = "VERIFIED" } },
                        m1 = new[] { new { categoryId = 1, detectedFileExtension = "zip", scannedV2 = "QUARANTINED" } },
                        m2 = new[] { new { categoryId = 1, detectedFileExtension = "zip", scannedV2 = "WAITING_REPORT" } }
                    } });
                }
                return Json(new { data = new { mods = new { nodes = Enumerable.Range(1, 3).Select(id => new
                { modId = id, gameId = 7184, name = "Mod" + id, summary = "Description", version = "1" }) } } });
            }
            if (path.Contains("md5_search"))
            {
                var hash = Path.GetFileNameWithoutExtension(path);
                return Json(Archives.Where(pair => Convert.ToHexString(MD5.HashData(pair.Value)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => new { mod = new { mod_id = pair.Key, name = "Mod" + pair.Key, summary = "", version = "1", available = true },
                        file_details = new { file_id = pair.Key * 10 + (Updated && pair.Key == 3 ? 1 : 0), name = "Main", file_name = $"Mod{pair.Key}.zip", version = "1", category_id = 1, is_primary = true } }));
            }
            if (path.EndsWith("files.json"))
            {
                var id = int.Parse(path.Split('/')[^2]);
                return Json(new { files = new[] { new { file_id = id * 10 + (Updated && id == 3 ? 1 : 0), name = "Main", file_name = FileRequirements && id == 4 ? "Installer.exe" : $"Mod{id}.zip", version = Updated && id == 3 ? "2" : "1",
                    category_id = 1, is_primary = true, size_in_bytes = Archives[id].Length } }, file_updates = Updated && id == 3 ? new object[] { new { old_file_id = 30, new_file_id = 31 } } : Array.Empty<object>() });
            }
            var modId = int.Parse(Path.GetFileNameWithoutExtension(path));
            return Json(new { mod_id = modId, name = "Mod" + modId, summary = "", version = "1", available = true });
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileRequirementsOfferCompatibleModsAndKeepExecutablesManual(bool update)
    {
        var server = new NexusServer { FileRequirements = true, Updated = update }; using var api = new HttpClient(new Handler(server.Respond));
        using var f = new Fixture(nexusApi: api); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "test-key");
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await f.Services.Providers.SetDirectoriesAsync([folder]);
        for (var id = 1; id <= 3; id++) server.Archives[id] = File.ReadAllBytes(f.Zip("Mod" + id, (ulong)id));
        server.Archives[4] = [1, 2, 3];
        if (update)
        {
            var old = await f.Services.Library.ImportAsync(f.Source("Old root", 33), name: "Mod3");
            await f.Services.Library.SetSourcesAsync(old.Id, [new("nexusmods", "3", "30", "1")]);
            await f.Services.Library.RecordUpdateCheckAsync(new(old.Id, "nexusmods", DateTimeOffset.UtcNow, "2", "31"));
            await f.Services.Session.ReloadAsync(CancellationToken.None);
            await f.Services.Operations.RunAsync("Updating", ct => f.Services.Downloads.ApplyUpdatesAsync(ct));
        }
        else await f.Services.Operations.RunAsync("Installing", ct => f.Services.Downloads.AddLinkAsync("https://www.nexusmods.com/helldivers2/mods/3", ct));
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal(new[] { update ? "31" : "30" }, server.RequirementFiles);
        var prompt = Assert.Single(f.Dialogs.Confirmations);
        Assert.Contains("Mod2", prompt.Message); Assert.Contains("Requirements to install manually", prompt.Message); Assert.Contains("Mod4", prompt.Message);
        Assert.Equal(3, f.Services.Providers.State.Jobs.Count); Assert.DoesNotContain(f.Services.Providers.State.Jobs, job => job.File.ModId == "4");
        foreach (var job in f.Services.Providers.State.Jobs.ToArray())
        {
            File.WriteAllBytes(Path.Combine(folder, job.File.FileName), server.Archives[int.Parse(job.File.ModId)]);
            await f.Services.Providers.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        var root = f.Services.Session.State.Mods.Single(mod => !mod.Superseded && mod.Name == "Mod3");
        Assert.True(root.Dependencies.Single(item => item.Name == "Mod2").CanInstall);
        Assert.False(root.Dependencies.Single(item => item.Name == "Mod4").CanInstall);
        Assert.Equal(new[] { "20" }, root.Dependencies.Single(item => item.Name == "Mod2").AllowedFileIds);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DependenciesAndTransitiveRequirementsImportIntoTheSameDestination(bool profile, bool installed)
    {
        var server = new NexusServer(); using var api = new HttpClient(new Handler(server.Respond));
        using var f = new Fixture(nexusApi: api); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "test-key");
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await f.Services.Providers.SetDirectoriesAsync([folder]);
        for (var id = 1; id <= 3; id++) server.Archives[id] = File.ReadAllBytes(f.Zip("Mod" + id, (ulong)id));
        if (installed)
        {
            await f.Services.Session.ImportAsync(Path.Combine(f.Root, "Mod1.zip"), CancellationToken.None);
            await f.Services.Library.SetSourcesAsync(f.Services.Session.State.Mods.Single().Id, [new("nexusmods", "1", "10", "1")]);
            await f.Services.Session.ReloadAsync(CancellationToken.None);
        }
        Guid? target = profile ? f.Services.Session.ActiveProfile!.Id : null;
        await f.Services.Operations.RunAsync("Installing mod", ct => f.Services.Downloads.AddLinkAsync("https://www.nexusmods.com/helldivers2/mods/3", ct, target));
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        var prompt = Assert.Single(f.Dialogs.Confirmations);
        Assert.Equal("Mod requirements", prompt.Title);
        Assert.Contains("Mod2", prompt.Message);
        if (installed && profile) Assert.Contains("already in your library", prompt.Message);
        Assert.Equal(installed ? 2 : 3, f.Services.Providers.State.Jobs.Count);
        Assert.Equal(new[] { "1", "2", "3" }.Where(id => !installed || id != "1"), f.Services.Providers.State.Jobs.Select(job => job.File.ModId));
        Assert.All(f.Services.Providers.State.Jobs, job => Assert.Equal(target, job.ProfileId));
        Assert.All(f.Services.Providers.State.Jobs, job => Assert.Equal(job.File.ModId != "3", job.InstalledAsDependency));
        foreach (var job in f.Services.Providers.State.Jobs.ToArray())
        {
            File.WriteAllBytes(Path.Combine(folder, job.File.FileName), server.Archives[int.Parse(job.File.ModId)]);
            await f.Services.Providers.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await f.Services.Session.ReloadAsync(CancellationToken.None); Dispatcher.UIThread.RunJobs();
        Assert.All(f.Services.Providers.State.Jobs, job => Assert.Equal(DownloadStatus.Complete, job.Status));
        Assert.Equal(3, f.Services.Session.State.Mods.Count);
        var rootMod = f.Services.Session.State.Mods.Single(mod => mod.Name == "Mod3");
        Assert.True(rootMod.DependenciesKnown);
        Assert.False(rootMod.InstalledAsDependency);
        Assert.True(f.Services.Session.State.Mods.Single(mod => mod.Name == "Mod2").InstalledAsDependency);
        Assert.Equal(!installed, f.Services.Session.State.Mods.Single(mod => mod.Name == "Mod1").InstalledAsDependency);
        await using (var reopened = f.ReopenServices())
            Assert.True((await reopened.Library.LoadAsync()).Mods.Single(mod => mod.Name == "Mod2").InstalledAsDependency);
        Assert.Equal(new[] { "Mod2", "Mod1" }, rootMod.Dependencies.Select(dependency => dependency.Name));
        Assert.Equal(new[] { "Mod1", "Mod3" }, f.Services.Session.State.Mods.Single(mod => mod.Name == "Mod2").Dependencies.Select(dependency => dependency.Name));
        Assert.Equal(profile ? 3 : 0, f.Services.Session.ActiveProfile!.Entries.Count);
    }

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UpdatesOfferNewDependenciesAndIncludeThemInEveryProfile(bool includeDependencies, bool singleUpdate)
    {
        var server = new NexusServer { Updated = true }; using var api = new HttpClient(new Handler(server.Respond));
        using var f = new Fixture(nexusApi: api); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "test-key");
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await f.Services.Providers.SetDirectoriesAsync([folder]);
        for (var id = 1; id <= 3; id++) server.Archives[id] = File.ReadAllBytes(f.Zip("Mod" + id, (ulong)id));
        var old = await f.Services.Library.ImportAsync(f.Source("Old root", 33), name: "Mod3", installedAsDependency: true);
        await f.Services.Library.SetSourcesAsync(old.Id, [new("nexusmods", "3", "30", "1")]);
        await f.Services.Library.SetDependenciesAsync(old.Id, []);
        var first = ProfileEditor.Add(f.Services.Session.ActiveProfile!, old);
        var second = ProfileEditor.Add(ProfileEditor.Create("Second"), old);
        await f.Services.Session.SaveProfileAsync(first, false, CancellationToken.None);
        await f.Services.Session.SaveProfileAsync(second, false, CancellationToken.None);
        await f.Services.Library.RecordUpdateCheckAsync(new(old.Id, "nexusmods", DateTimeOffset.UtcNow, "2", "31"));
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        f.Dialogs.Confirm = includeDependencies;
        if (singleUpdate) await f.Services.Downloads.CreateUpdateCommand(f.Services.Session.State.Mods.Single())!.ExecuteAsync();
        else await f.Services.Operations.RunAsync("Updating mods", ct => f.Services.Downloads.ApplyUpdatesAsync(ct));
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        var prompt = Assert.Single(f.Dialogs.Confirmations);
        Assert.Equal("Mod requirements", prompt.Title); Assert.Contains("Mod2", prompt.Message);
        Assert.Equal(includeDependencies ? 3 : 1, f.Services.Providers.State.Jobs.Count);
        foreach (var job in f.Services.Providers.State.Jobs.ToArray())
        {
            File.WriteAllBytes(Path.Combine(folder, job.File.FileName), server.Archives[int.Parse(job.File.ModId)]);
            await f.Services.Providers.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await f.Services.Session.ReloadAsync(CancellationToken.None); Dispatcher.UIThread.RunJobs();
        Assert.All(f.Services.Providers.State.Jobs, job => Assert.Equal(DownloadStatus.Complete, job.Status));
        var root = f.Services.Session.State.Mods.Single(mod => !mod.Superseded && mod.Name == "Mod3");
        Assert.NotEqual(old.Id, root.Id); Assert.True(root.InstalledAsDependency);
        Assert.Equal(new[] { "Mod2", "Mod1" }, root.Dependencies.Select(item => item.Name));
        Assert.All(f.Services.Session.State.Profiles, profile =>
        {
            Assert.Contains(profile.Entries, entry => entry.ModId == root.Id);
            Assert.Equal(includeDependencies ? 3 : 1, profile.Entries.Count);
        });
    }

    [AvaloniaFact]
    public async Task ResolveFromHostedDetailsCanChooseFilesAndConfirmWithoutClosingDetails()
    {
        var server = new NexusServer(); using var api = new HttpClient(new Handler(server.Respond));
        var window = new MainWindow();
        using var f = new Fixture(nexusApi: api, dialogs: new Shared.DialogService(() => window));
        await f.Services.Session.InitializeAsync(CancellationToken.None);
        await f.Services.Session.SetOnboardingPreferencesAsync(null, true, CancellationToken.None);
        await f.Shell.InitializeAsync(); await f.Services.Keys.SetAsync("nexusmods", "test-key");
        for (var id = 1; id <= 3; id++) server.Archives[id] = File.ReadAllBytes(f.Zip("Mod" + id, (ulong)id));
        var root = await f.Services.Library.ImportAsync(f.Source("Mod3", 3));
        await f.Services.Library.SetSourcesAsync(root.Id, [new("nexusmods", "3", "30", "1")]);
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        var details = new ModDetailsViewModel(root, f.Services);
        var dialog = new ModDetailsDialog { DataContext = details };
        dialog.FindControl<TabControl>("DetailsTabs")!.SelectedIndex = 1;
        window.DataContext = f.Shell; window.Show();
        var shown = window.ShowDialogAsync<object?>(dialog);
        try
        {
            var run = details.ResolveDependenciesCommand.ExecuteAsync();
            var choices = 0; var confirmations = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!run.IsCompleted)
            {
                timeout.Token.ThrowIfCancellationRequested();
                window.CaptureRenderedFrame()?.Dispose();
                if (window.GetVisualDescendants().OfType<ModFilesDialog>().FirstOrDefault() is { } files)
                { Assert.True(files.TryAccept()); choices++; }
                if (window.GetVisualDescendants().OfType<Shared.ConfirmationDialog>().FirstOrDefault() is { } confirm)
                { Assert.True(confirm.TryAccept()); confirmations++; }
                await Task.Delay(10, timeout.Token);
            }
            await run; window.CaptureRenderedFrame()?.Dispose();
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
            Assert.Equal(2, choices); Assert.Equal(1, confirmations);
            Assert.False(shown.IsCompleted);
            Assert.Same(dialog, Assert.Single(window.GetVisualDescendants().OfType<ModDetailsDialog>()));
            Assert.Equal(1, dialog.FindControl<TabControl>("DetailsTabs")!.SelectedIndex);
            Assert.Equal(2, details.Dependencies.Count);
            Assert.Equal(2, f.Services.Providers.State.Jobs.Count);
            dialog.Cancel(); await shown;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ResolveButtonRepairsRequirementsAndDistinguishesDependencyImports()
    {
        var server = new NexusServer(); using var api = new HttpClient(new Handler(server.Respond));
        using var f = new Fixture(nexusApi: api); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "test-key");
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await f.Services.Providers.SetDirectoriesAsync([folder]);
        for (var id = 1; id <= 3; id++) server.Archives[id] = File.ReadAllBytes(f.Zip("Mod" + id, (ulong)id));
        var root = await f.Services.Library.ImportAsync(Path.Combine(f.Root, "Mod3.zip"));
        var explicitMod = await f.Services.Library.ImportAsync(Path.Combine(f.Root, "Mod1.zip"));
        await f.Services.Library.SetSourcesAsync(root.Id, [new("nexusmods", "3", "30", "1")]);
        await f.Services.Library.SetSourcesAsync(explicitMod.Id, [new("nexusmods", "1", "10", "1")]);
        var profile = ProfileEditor.SetEnabled(ProfileEditor.Add(ProfileEditor.Add(f.Services.Session.ActiveProfile!, root), explicitMod), explicitMod.Id, false);
        await f.Services.Session.SaveProfileAsync(profile, false, CancellationToken.None);
        var details = new ModDetailsViewModel(root, f.Services);
        var dialog = new ModDetailsDialog { DataContext = details };
        dialog.FindControl<TabControl>("DetailsTabs")!.SelectedIndex = 1;
        var window = new Window { Content = dialog }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Assert.True(dialog.FindControl<Button>("ResolveDependenciesButton")!.IsVisible);
            await details.ResolveDependenciesCommand.ExecuteAsync();
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
            var job = Assert.Single(f.Services.Providers.State.Jobs); Assert.True(job.InstalledAsDependency);
            File.WriteAllBytes(Path.Combine(folder, job.File.FileName), server.Archives[2]);
            await f.Services.Providers.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(10));
            await f.Services.Session.ReloadAsync(CancellationToken.None); Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, f.Services.Session.ActiveProfile!.Entries.Count);
            Assert.All(f.Services.Session.ActiveProfile.Entries, entry => Assert.True(entry.Enabled));
            Assert.Equal(2, details.Dependencies.Count); Assert.All(details.Dependencies, item => Assert.Equal("In library", item.Status));
            var imported = f.Services.Session.State.Mods.Single(mod => mod.Name == "Mod2");
            Assert.True(imported.InstalledAsDependency);
            Assert.False(f.Services.Session.State.Mods.Single(mod => mod.Id == explicitMod.Id).InstalledAsDependency);
            var row = new ModRowView { DataContext = new ModListItem(imported, 0, false) };
            window.Content = row; window.CaptureRenderedFrame()?.Dispose();
            var icon = row.FindControl<Border>("DependencyIcon")!;
            Assert.True(icon.IsVisible); Assert.Equal("Installed as a dependency", ToolTip.GetTip(icon));
            row.DataContext = new ModListItem(root, 0, false); Dispatcher.UIThread.RunJobs(); Assert.False(icon.IsVisible);
        }
        finally { window.Close(); }
        // A second repair makes no duplicate requests or installations.
        await f.Services.Operations.RunAsync("Resolving again", ct => f.Services.Downloads.ResolveDependenciesAsync(root.Id, ct));
        Assert.Single(f.Dialogs.Confirmations); Assert.Single(f.Services.Providers.State.Jobs);
        Assert.Equal(3, f.Services.Session.State.Mods.Count);
    }

    [AvaloniaFact]
    public async Task AddingAnInstalledNexusModToAProfileAlsoAddsItsInstalledDependencies()
    {
        var server = new NexusServer(); using var api = new HttpClient(new Handler(server.Respond));
        using var f = new Fixture(nexusApi: api); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "test-key");
        for (var id = 1; id <= 3; id++)
        {
            await f.Services.Session.ImportAsync(f.Source("Mod" + id, (ulong)id), CancellationToken.None);
            var mod = f.Services.Session.State.Mods.Single(mod => mod.Name == "Mod" + id);
            await f.Services.Library.SetSourcesAsync(mod.Id, [new("nexusmods", id.ToString(), (id * 10).ToString(), "1")]);
        }
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        var model = Assert.IsType<Quartermaster.Gui.Profiles.ProfilesViewModel>(f.Shell.CurrentPage);
        model.ModToAdd = f.Services.Session.State.Mods.Single(mod => mod.Name == "Mod3");
        await model.AddCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal(3, model.Entries.Count); Assert.Equal(3, f.Services.Session.State.Mods.Count);
        Assert.Equal(new[] { "Mod1", "Mod2", "Mod3" }, model.Entries.Select(row => row.Name));
        Assert.Contains("already in your library", Assert.Single(f.Dialogs.Confirmations).Message);
        Assert.Empty(f.Services.Providers.State.Jobs); Assert.Empty(f.BrowserRequests);
    }

    [AvaloniaFact]
    public async Task ManagerLinkKeepsTheQueuedProfileDestinationForDependencies()
    {
        var server = new NexusServer(); using var api = new HttpClient(new Handler(server.Respond));
        using var download = new HttpClient(new Handler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(server.Archives[int.Parse(request.RequestUri!.AbsolutePath.Trim('/'))]) })));
        using var f = new Fixture(nexusApi: api, nexusDownloads: download); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "test-key");
        for (var id = 1; id <= 3; id++) server.Archives[id] = File.ReadAllBytes(f.Zip("Mod" + id, (ulong)id));
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await f.Services.Providers.SetDirectoriesAsync([folder]);
        var profile = f.Services.Session.ActiveProfile!.Id;
        var root = await f.Services.Providers.ResolveAsync("https://www.nexusmods.com/helldivers2/mods/3");
        await f.Services.Providers.QueueAsync(root.Files.Single(), profile);
        await f.Services.Operations.RunAsync("Receiving Nexus download", ct => f.Services.Downloads.AddLinkAsync("nxm://helldivers2/mods/3/files/30", ct));
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal(3, f.Services.Providers.State.Jobs.Count);
        Assert.All(f.Services.Providers.State.Jobs, job => Assert.Equal(profile, job.ProfileId));
        foreach (var job in f.Services.Providers.State.Jobs.ToArray())
        {
            if (job.File.ModId != "3") File.WriteAllBytes(Path.Combine(folder, job.File.FileName), server.Archives[int.Parse(job.File.ModId)]);
            await f.Services.Providers.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await f.Services.Session.ReloadAsync(CancellationToken.None); Dispatcher.UIThread.RunJobs();
        Assert.All(f.Services.Providers.State.Jobs, job => Assert.Equal(DownloadStatus.Complete, job.Status));
        Assert.Equal(3, f.Services.Session.ActiveProfile!.Entries.Count);
    }

    [AvaloniaFact]
    public async Task DecliningDependenciesOnlyQueuesRequestedModAndListsExternalRequirements()
    {
        var server = new NexusServer { External = true }; using var api = new HttpClient(new Handler(server.Respond));
        using var f = new Fixture(nexusApi: api); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "test-key");
        for (var id = 1; id <= 3; id++) server.Archives[id] = File.ReadAllBytes(f.Zip("Mod" + id, (ulong)id));
        f.Dialogs.Confirm = false;
        await f.Services.Operations.RunAsync("Installing mod", ct => f.Services.Downloads.AddLinkAsync("https://www.nexusmods.com/helldivers2/mods/3", ct));
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal("3", Assert.Single(f.Services.Providers.State.Jobs).File.ModId);
        Assert.Contains("External tool: https://example.com/tool", Assert.Single(f.Dialogs.Confirmations).Message);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchShowsVirusResultsAndUnknownWhenMetadataFails(bool fail)
    {
        var server = new NexusServer { FailScans = fail }; using var api = new HttpClient(new Handler(server.Respond));
        using var f = new Fixture(nexusApi: api); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "test-key"); f.Shell.Navigate(PageKind.Search);
        var model = Assert.IsType<SearchViewModel>(f.Shell.CurrentPage); model.Query = "Mod";
        await model.SearchCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal(fail ? new[] { "Virus scan: Unknown", "Virus scan: Unknown", "Virus scan: Unknown" }
            : new[] { "Virus scan: Passed", "Virus scan: Quarantined", "Virus scan: Awaiting report" }, model.Results.Select(result => result.VirusResult));
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(3, window.GetVisualDescendants().OfType<TextBlock>().Count(text => text.Name == "VirusScanResult" && text.IsVisible));
        }
        finally { window.Close(); }
    }
}
