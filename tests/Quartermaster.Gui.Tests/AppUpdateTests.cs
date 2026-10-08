using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Settings;
using Quartermaster.Gui.Shared;
using Quartermaster.SelfUpdate;
using Xunit;

namespace Quartermaster.Gui.Tests;

public class AppUpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
    private sealed class UpdateFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "quartermaster-app-update-" + Guid.NewGuid().ToString("N"));
        public FakeDialogs Dialogs { get; } = new();
        public AppServices Services { get; }
        public int Requests { get; private set; }
        public int Shutdowns { get; private set; }
        public List<ProcessStartInfo> Starts { get; } = [];
        public string Version { get; set; } = "v0.4.1";
        public TaskCompletionSource? ResponseGate { get; set; }
        public bool BadChecksum { get; set; }
        private readonly HttpClient client;
        public UpdateFixture()
        {
            var options = new SelfUpdateOptions("0.4.0", "win-x64", Path.Combine(Root, "install"), Path.Combine(Root, "work"));
            Directory.CreateDirectory(Path.Combine(options.InstallDirectory, "winupdater"));
            File.WriteAllText(Path.Combine(options.InstallDirectory, options.ExecutableName), "old app");
            File.WriteAllText(Path.Combine(options.InstallDirectory, "winupdater", options.UpdaterName), "old helper");
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var name in new[] { options.ExecutableName, "winupdater/" + options.UpdaterName })
                { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("new binary"); }
            var package = bytes.ToArray();
            client = new(new Handler(async (request, ct) =>
            {
                var checksum = (BadChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(package))) + "  " + options.AssetName;
                if (request.RequestUri!.Host == "api.github.com")
                {
                    Requests++;
                    if (ResponseGate is not null) await ResponseGate.Task.WaitAsync(ct);
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            tag_name = Version,
                            html_url = $"https://github.com/{options.Repository}/releases/tag/{Version}",
                            body = "New features",
                            draft = false,
                            prerelease = false,
                            assets = new[]
                        {
                            new { name = options.AssetName, browser_download_url = $"https://github.com/{options.Repository}/releases/download/{Version}/{options.AssetName}", size = package.Length },
                            new { name = "SHA256SUMS.txt", browser_download_url = $"https://github.com/{options.Repository}/releases/download/{Version}/SHA256SUMS.txt", size = Encoding.UTF8.GetByteCount(checksum) }
                        }
                        }))
                    };
                }
                return new(HttpStatusCode.OK)
                {
                    Content = request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt")
                    ? new StringContent(checksum) : new ByteArrayContent(package)
                };
            }));
            Services = new(Path.Combine(Root, "data"), Dialogs, discover: () => [],
                appUpdateManager: () => new(options, client, client, info => { Starts.Add(info); return 12345; }),
                shutdownForUpdate: () => Shutdowns++);
        }
        public async Task InitializeAsync(bool automatic)
        {
            await new SettingsStore(Services.DataDirectory).SaveAsync(new(AllowAutomaticUpdate: automatic), CancellationToken.None);
            await Services.Session.InitializeAsync(CancellationToken.None);
        }
        public async ValueTask DisposeAsync()
        { await Services.DisposeAsync(); client.Dispose(); Directory.Delete(Root, true); }
    }

    [AvaloniaFact]
    public async Task StartupCheckingIsOptInAndDoesNotBlockUiDuringNetworkRequest()
    {
        await using var f = new UpdateFixture(); await f.InitializeAsync(false);
        await f.Services.AppUpdates.CheckAtStartupAsync(); Assert.Equal(0, f.Requests);
        await f.InitializeAsync(true);
        f.ResponseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = f.Services.AppUpdates.CheckAtStartupAsync();
        Assert.Equal(1, f.Requests); Assert.False(check.IsCompleted); Assert.False(f.Services.Operations.IsBusy);
        f.ResponseGate.SetResult(); await check;
        Assert.Single(f.Dialogs.AppUpdatePrompts); Assert.Empty(f.Starts);
    }

    [AvaloniaFact]
    public async Task StartupPromptWaitsForAnActiveOperationAndManualCheckCanBeCancelled()
    {
        await using var f = new UpdateFixture(); await f.InitializeAsync(true);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = f.Services.Operations.RunAsync("Manual mod check", _ => releaseOperation.Task);
        var startup = f.Services.AppUpdates.CheckAtStartupAsync();
        Assert.Empty(f.Dialogs.AppUpdatePrompts); Assert.False(startup.IsCompleted);
        releaseOperation.SetResult(); await operation; await startup;
        Assert.Single(f.Dialogs.AppUpdatePrompts);
        f.ResponseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = f.Services.AppUpdates.CheckAsync();
        f.Services.Operations.CancelCommand.Execute(null); await manual;
        Assert.Equal("Update check cancelled.", f.Services.AppUpdates.Status);
        Assert.Single(f.Dialogs.AppUpdatePrompts);
    }

    [AvaloniaFact]
    public async Task SkipPersistsForThatReleaseAndManualCheckCanStillOfferIt()
    {
        await using var f = new UpdateFixture(); await f.InitializeAsync(true);
        f.Dialogs.AppUpdateAnswer = AppUpdateChoice.Skip;
        await f.Services.AppUpdates.CheckAtStartupAsync();
        Assert.Equal("v0.4.1", (await new SettingsStore(f.Services.DataDirectory).LoadAsync(CancellationToken.None)).SkippedAppUpdateVersion);
        await f.Services.AppUpdates.CheckAtStartupAsync(); Assert.Single(f.Dialogs.AppUpdatePrompts);
        f.Dialogs.AppUpdateAnswer = AppUpdateChoice.Cancel;
        await f.Services.AppUpdates.CheckAsync(); Assert.Equal(2, f.Dialogs.AppUpdatePrompts.Count);
        f.Version = "v0.4.2";
        await f.Services.AppUpdates.CheckAtStartupAsync(); Assert.Equal(3, f.Dialogs.AppUpdatePrompts.Count);
        Assert.Empty(f.Starts);
    }

    [AvaloniaFact]
    public async Task CancelKeepsTheReleaseEligibleAndNoUpdateShowsStatusWithoutPrompt()
    {
        await using var f = new UpdateFixture(); await f.InitializeAsync(true);
        await f.Services.AppUpdates.CheckAtStartupAsync(); await f.Services.AppUpdates.CheckAtStartupAsync();
        Assert.Equal(2, f.Dialogs.AppUpdatePrompts.Count); Assert.Null(f.Services.Session.Settings.SkippedAppUpdateVersion);
        f.Version = "v0.4.0";
        await f.Services.AppUpdates.CheckAsync(); Assert.Equal(2, f.Dialogs.AppUpdatePrompts.Count);
        Assert.Equal("Quartermaster is up to date.", f.Services.AppUpdates.Status);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyVerifiedUpdatesLaunchAndRequestShutdown(bool badChecksum)
    {
        await using var f = new UpdateFixture(); await f.InitializeAsync(false);
        f.BadChecksum = badChecksum; f.Dialogs.AppUpdateAnswer = AppUpdateChoice.Update;
        await f.Services.AppUpdates.CheckAsync();
        Assert.Equal(badChecksum ? 0 : 1, f.Starts.Count); Assert.Equal(badChecksum ? 0 : 1, f.Shutdowns);
        Assert.Equal(badChecksum, f.Services.Operations.IsError);
        if (!badChecksum) Assert.Contains("runner", Assert.Single(f.Starts).FileName);
    }

    [AvaloniaFact]
    public async Task AutomaticUpdateSettingSharesARowWithManualCheckAndPersistsWithSave()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync(); f.Shell.Navigate(PageKind.Settings);
        var model = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            model.Search = "app updates"; Dispatcher.UIThread.RunJobs();
            var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            var toggle = view.FindControl<CheckBox>("AutomaticAppUpdateToggle")!;
            var button = view.FindControl<Button>("CheckAppUpdatesButton")!;
            Assert.True(toggle.IsEffectivelyVisible); Assert.True(button.IsEffectivelyVisible);
            Assert.Same(toggle.Parent, button.Parent); Assert.False(model.AllowAutomaticUpdate);
            toggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
            await model.SaveCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
            Assert.True((await new SettingsStore(f.Data).LoadAsync(CancellationToken.None)).AllowAutomaticUpdate);
            model.ResetCommand.Execute(null); Assert.False(model.AllowAutomaticUpdate);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task UpdatePromptWaitsForAnExistingDialog()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var existing = new ConfirmationDialog("Manual check", "An existing prompt", "Done");
            var first = window.ShowDialogAsync<bool>(existing);
            var service = new DialogService(() => window);
            var next = service.PromptAppUpdateAsync("v0.4.1", "Changes");
            Assert.False(next.IsCompleted);
            existing.Cancel(); await first;
            Dispatcher.UIThread.RunJobs();
            var prompt = Assert.Single(window.GetVisualDescendants().OfType<AppUpdateDialog>());
            prompt.Cancel(); Assert.Equal(AppUpdateChoice.Cancel, await next);
        }
        finally { window.Close(); }
    }
}
