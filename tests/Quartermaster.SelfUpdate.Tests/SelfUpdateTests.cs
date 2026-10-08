using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Quartermaster.SelfUpdate.Tests;

public class SelfUpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class Progress(Action<SelfUpdateProgress> report) : IProgress<SelfUpdateProgress>
    { public void Report(SelfUpdateProgress value) => report(value); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Quartermaster self-update ü " + Guid.NewGuid().ToString("N"));
        public SelfUpdateOptions Options { get; }
        public byte[] Package { get; set; }
        public string Checksums { get; set; }
        public string Latest { get; set; } = "v0.4.1";
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<ProcessStartInfo> Starts { get; } = [];
        public Func<ProcessStartInfo, int>? Launcher { get; set; }
        public HttpClient Api { get; }
        public HttpClient Downloads { get; }
        public Fixture(string current = "0.4.0", string rid = "win-x64", Func<SelfUpdateOptions, SelfUpdateOptions>? configure = null)
        {
            Options = new(current, rid, Path.Combine(Root, "installed"), Path.Combine(Root, "work"));
            if (configure is not null) Options = configure(Options);
            Directory.CreateDirectory(Path.Combine(Options.InstallDirectory, "winupdater"));
            File.WriteAllText(Path.Combine(Options.InstallDirectory, Options.ExecutableName), "installed application");
            File.WriteAllText(Path.Combine(Options.InstallDirectory, "winupdater", Options.UpdaterName), "installed helper");
            Package = Archive((Options.ExecutableName, "new application", 0), ("winupdater/" + Options.UpdaterName, "new helper", 0));
            Checksums = Hash(Package) + "  " + Options.AssetName + "\n";
            Api = new(new Handler(request =>
            {
                Assert.Equal($"https://api.github.com/repos/{Options.Repository}/releases/latest", request.RequestUri!.AbsoluteUri);
                Assert.Contains("Quartermaster", request.Headers.UserAgent.ToString());
                return new(Status)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        tag_name = Latest,
                        html_url = $"https://github.com/{Options.Repository}/releases/tag/{Latest}",
                        body = "Release notes",
                        draft = Draft,
                        prerelease = Prerelease,
                        published_at = "2026-10-07T00:00:00Z",
                        assets = new[]
                    {
                        new { name = Options.AssetName, browser_download_url = Url(Options.AssetName).AbsoluteUri, size = Package.Length },
                        new { name = "SHA256SUMS.txt", browser_download_url = Url("SHA256SUMS.txt").AbsoluteUri, size = Encoding.UTF8.GetByteCount(Checksums) },
                        new { name = "Quartermaster-other-runtime.zip", browser_download_url = Url("Quartermaster-other-runtime.zip").AbsoluteUri, size = 1 }
                    }
                    }))
                };
            }));
            Downloads = new(new Handler(request => new(HttpStatusCode.OK)
            { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt") ? Encoding.UTF8.GetBytes(Checksums) : Package) }));
        }
        public Uri Url(string name) => new($"https://github.com/{Options.Repository}/releases/download/{Latest}/{name}");
        public AppRelease Release() => new(Latest, new($"https://github.com/{Options.Repository}/releases/tag/{Latest}"), "Notes", null,
            new(Options.AssetName, Url(Options.AssetName), Package.Length), new("SHA256SUMS.txt", Url("SHA256SUMS.txt"), Encoding.UTF8.GetByteCount(Checksums)));
        public SelfUpdateManager Manager(SelfUpdateOptions? options = null) => new(options ?? Options, Api, Downloads, info =>
        { Starts.Add(info); return Launcher?.Invoke(info) ?? 12345; });
        public void SetArchive(params (string Name, string Contents, int Attributes)[] extra)
        {
            Package = Archive([(Options.ExecutableName, "new application", 0), ("winupdater/" + Options.UpdaterName, "new helper", 0), .. extra]);
            Checksums = Hash(Package) + "  " + Options.AssetName + "\n";
        }
        public void AssertUnchanged()
        {
            Assert.Equal("installed application", File.ReadAllText(Path.Combine(Options.InstallDirectory, Options.ExecutableName)));
            Assert.Equal("installed helper", File.ReadAllText(Path.Combine(Options.InstallDirectory, "winupdater", Options.UpdaterName)));
            Assert.Empty(Starts);
        }
        public void AssertNoStaging()
        { if (Directory.Exists(Options.WorkDirectory)) Assert.Empty(Directory.EnumerateDirectories(Options.WorkDirectory)); }
        public void Dispose() { Api.Dispose(); Downloads.Dispose(); Directory.Delete(Root, true); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Archive(params (string Name, string Contents, int Attributes)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name); if (file.Attributes != 0) entry.ExternalAttributes = file.Attributes;
                using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes(file.Contents));
            }
        return output.ToArray();
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task ChoosesExactBundledAssetForEverySupportedPlatform(string rid)
    {
        using var f = new Fixture(rid: rid); using var manager = f.Manager();
        var release = Assert.IsType<AppRelease>(await manager.CheckAsync());
        Assert.Equal(f.Options.AssetName, release.Package.Name); Assert.Equal("Release notes", release.Notes);
    }

    [Theory]
    [InlineData("win-arm64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    public void RejectsUnsupportedReleaseTargets(string rid)
    {
        using var f = new Fixture(rid: rid);
        Assert.Throws<PlatformNotSupportedException>(() => f.Manager());
    }

    [Theory]
    [InlineData("0.4.0", "v0.4.1", true)]
    [InlineData("0.4.0+abc", "v0.4.0", false)]
    [InlineData("0.5.0", "v0.4.9", false)]
    [InlineData("0.4.1-beta.2", "v0.4.1", true)]
    [InlineData("0.4.1-beta.10", "v0.4.0", false)]
    public async Task VersionComparisonDoesNotDowngradeOrTreatBuildMetadataAsAnUpdate(string current, string latest, bool available)
    {
        using var f = new Fixture(current) { Latest = latest }; using var manager = f.Manager();
        Assert.Equal(available, await manager.CheckAsync() is not null);
    }

    [Theory]
    [InlineData(true, false, HttpStatusCode.OK)]
    [InlineData(false, true, HttpStatusCode.OK)]
    [InlineData(false, false, HttpStatusCode.NotFound)]
    public async Task UnpublishedPrereleaseOrAbsentReleaseIsNotOffered(bool draft, bool prerelease, HttpStatusCode status)
    {
        using var f = new Fixture { Draft = draft, Prerelease = prerelease, Status = status }; using var manager = f.Manager();
        Assert.Null(await manager.CheckAsync());
    }

    [Fact]
    public async Task VerifiedPreparationCopiesCurrentHelperAndBuildsAQuotedIndependentHandoff()
    {
        using var f = new Fixture(); using var manager = f.Manager(); var phases = new List<SelfUpdatePhase>();
        using var update = await manager.PrepareAsync(f.Release(), new Progress(value => phases.Add(value.Phase)));
        Assert.Equal("new application", File.ReadAllText(Path.Combine(update.StagedDirectory, f.Options.ExecutableName)));
        Assert.Equal("installed helper", File.ReadAllText(update.UpdaterPath));
        Assert.Equal("new helper", File.ReadAllText(Path.Combine(update.StagedDirectory, "winupdater", f.Options.UpdaterName)));
        Assert.False(File.Exists(Path.Combine(update.Directory, "release.zip")));
        Assert.Contains(SelfUpdatePhase.Verifying, phases); Assert.Contains(SelfUpdatePhase.Extracting, phases); Assert.Equal(SelfUpdatePhase.Ready, phases.Last());
        f.AssertUnchanged();
        Assert.Equal(12345, manager.Start(update));
        var start = Assert.Single(f.Starts); Assert.False(start.UseShellExecute); Assert.False(start.RedirectStandardError);
        Assert.Equal(update.UpdaterPath, start.FileName);
        Assert.Equal(new[] { Environment.ProcessId.ToString(), update.StagedDirectory, f.Options.ExecutableName, "60000", update.LogPath }, start.ArgumentList.Where((_, index) => index != 2));
        Assert.Equal("installed application", File.ReadAllText(Path.Combine(start.ArgumentList[2], f.Options.ExecutableName)));
        Assert.Throws<InvalidOperationException>(() => manager.Start(update));
        update.Dispose(); Assert.True(Directory.Exists(update.StagedDirectory));
    }

    [Fact]
    public async Task LinuxReplacesTheLiveExecutableWithoutAHelperAndRestarts()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(rid: "linux-x64");
        Directory.Delete(Path.Combine(f.Options.InstallDirectory, "winupdater"), true);
        f.Package = Archive((f.Options.ExecutableName, "new application", 0), ("assets/data", "new data", 0));
        f.Checksums = Hash(f.Package) + "  " + f.Options.AssetName;
        using var manager = f.Manager();
        using var update = await manager.PrepareAsync(f.Release());
        Assert.Equal("", update.UpdaterPath);
        var executable = Path.Combine(f.Options.InstallDirectory, f.Options.ExecutableName);
        using var runningInode = File.OpenRead(executable);
        Assert.Equal(12345, manager.Start(update));
        Assert.Equal("new application", File.ReadAllText(executable));
        using var reader = new StreamReader(runningInode);
        Assert.Equal("installed application", reader.ReadToEnd());
        Assert.Equal("new data", File.ReadAllText(Path.Combine(f.Options.InstallDirectory, "assets", "data")));
        Assert.True((File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) != 0);
        var start = Assert.Single(f.Starts);
        Assert.Equal(executable, start.FileName); Assert.Empty(start.ArgumentList);
        Assert.Contains("Quartermaster update completed.", File.ReadAllText(update.LogPath));
        Assert.Throws<InvalidOperationException>(() => manager.Start(update));
    }

    [Fact]
    public async Task LinuxCanReplaceAnExecutingBinaryAndLaunchItsReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(rid: "linux-x64");
        var executable = Path.Combine(f.Options.InstallDirectory, f.Options.ExecutableName);
        File.Copy("/bin/sleep", executable, overwrite: true);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry(f.Options.ExecutableName).Open())
            entry.Write(File.ReadAllBytes("/bin/true"));
        f.Package = archiveBytes.ToArray();
        f.Checksums = Hash(f.Package) + "  " + f.Options.AssetName;
        using var running = Process.Start(new ProcessStartInfo(executable) { ArgumentList = { "30" }, UseShellExecute = false })!;
        Process? restarted = null;
        f.Launcher = info => { restarted = Process.Start(info)!; return restarted.Id; };
        try
        {
            using var manager = f.Manager(); using var update = await manager.PrepareAsync(f.Release());
            Assert.True(manager.Start(update) > 0);
            Assert.False(running.HasExited);
            Assert.NotNull(restarted);
            Assert.True(restarted.WaitForExit(5000)); Assert.Equal(0, restarted.ExitCode);
            Assert.Equal(File.ReadAllBytes("/bin/true"), File.ReadAllBytes(executable));
        }
        finally
        {
            if (!running.HasExited) running.Kill();
            running.WaitForExit(); restarted?.Dispose();
        }
    }

    [Fact]
    public async Task LinuxRestartFailureRestoresFilesAndRemovesNewDirectories()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(rid: "linux-x64");
        f.Package = Archive((f.Options.ExecutableName, "new application", 0), ("assets/data", "new data", 0));
        f.Checksums = Hash(f.Package) + "  " + f.Options.AssetName;
        f.Launcher = _ => throw new IOException("restart failed");
        using var manager = f.Manager();
        using var update = await manager.PrepareAsync(f.Release());
        Assert.Throws<IOException>(() => manager.Start(update));
        Assert.Equal("installed application", File.ReadAllText(Path.Combine(f.Options.InstallDirectory, f.Options.ExecutableName)));
        Assert.False(Directory.Exists(Path.Combine(f.Options.InstallDirectory, "assets")));
        Assert.Empty(Directory.GetDirectories(f.Options.InstallDirectory, ".quartermaster-update-*"));
        f.Launcher = null;
        Assert.Equal(12345, manager.Start(update));
    }

    [Fact]
    public async Task LinuxRejectsLinkedInstallationFilesBeforeReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(rid: "linux-x64");
        f.SetArchive(("linked", "replacement", 0));
        var outside = Path.Combine(f.Root, "outside"); File.WriteAllText(outside, "untouched");
        File.CreateSymbolicLink(Path.Combine(f.Options.InstallDirectory, "linked"), outside);
        using var manager = f.Manager(); using var update = await manager.PrepareAsync(f.Release());
        Assert.Throws<IOException>(() => manager.Start(update));
        Assert.Equal("untouched", File.ReadAllText(outside)); f.AssertUnchanged();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong")]
    [InlineData("digest")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    public async Task UnverifiedDownloadsAreDiscarded(string failure)
    {
        using var f = new Fixture();
        if (failure == "missing") f.Checksums = Hash(f.Package) + "  another.zip\n";
        if (failure == "duplicate") f.Checksums += f.Checksums;
        if (failure == "wrong") f.Checksums = new string('0', 64) + "  " + f.Options.AssetName;
        var release = f.Release();
        if (failure == "digest") release = release with { Package = release.Package with { Sha256 = new string('0', 64) } };
        if (failure == "truncated") f.Package = f.Package[..^1];
        if (failure == "oversized") f.Package = [.. f.Package, 1];
        using var manager = f.Manager();
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.PrepareAsync(release));
        f.AssertNoStaging(); f.AssertUnchanged();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/escape")]
    [InlineData("a\\..\\escape")]
    [InlineData("CON")]
    [InlineData("file:stream")]
    [InlineData("trailing.")]
    [InlineData("Quartermaster.Gui.exe")]
    [InlineData("folder")]
    [InlineData("folder/child")]
    public async Task UnsafeAndConflictingArchiveEntriesAreRejected(string name)
    {
        using var f = new Fixture();
        if (name == "folder") f.SetArchive(("folder/child", "file", 0), ("folder", "file", 0));
        else if (name == "folder/child") f.SetArchive(("folder", "file", 0), ("folder/child", "file", 0));
        else f.SetArchive((name, "unsafe", 0));
        using var manager = f.Manager();
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.PrepareAsync(f.Release()));
        f.AssertNoStaging(); f.AssertUnchanged(); Assert.False(File.Exists(Path.Combine(f.Root, "escape")));
    }

    [Fact]
    public async Task SymlinksAndMissingExecutablesAreRejected()
    {
        using var f = new Fixture(); using var manager = f.Manager();
        f.SetArchive(("symlink", "target", unchecked((int)0xA1FF0000)));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.PrepareAsync(f.Release())); f.AssertNoStaging();
        f.Package = Archive((f.Options.ExecutableName, "app", 0)); f.Checksums = Hash(f.Package) + "  " + f.Options.AssetName;
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.PrepareAsync(f.Release())); f.AssertNoStaging(); f.AssertUnchanged();
    }

    [Fact]
    public async Task DownloadAndExtractionLimitsAreEnforced()
    {
        using var f = new Fixture();
        using var smallDownload = f.Manager(f.Options with { MaxDownloadBytes = 1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => smallDownload.PrepareAsync(f.Release()));
        using var smallExtraction = f.Manager(f.Options with { MaxExtractedBytes = 1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => smallExtraction.PrepareAsync(f.Release()));
        using var smallCount = f.Manager(f.Options with { MaxFiles = 1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => smallCount.PrepareAsync(f.Release()));
        f.AssertNoStaging(); f.AssertUnchanged();
    }

    [Fact]
    public async Task CancellationAndLaunchFailureAllowSafeDiscard()
    {
        using var f = new Fixture(); using var manager = f.Manager(); using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.PrepareAsync(f.Release(), new Progress(value => cancel.Cancel()), cancel.Token));
        f.AssertNoStaging(); f.AssertUnchanged();
        var update = await manager.PrepareAsync(f.Release());
        f.Launcher = _ => throw new IOException("launch failed");
        Assert.Throws<IOException>(() => manager.Start(update));
        update.Dispose(); f.AssertNoStaging();
        Assert.Throws<ArgumentException>(() => manager.Start(update));
    }

    [Fact]
    public async Task PreparedUpdateIsOwnedAndDisposedUnlessHandedOff()
    {
        using var f = new Fixture(); using var manager = f.Manager(); using var other = f.Manager();
        var update = await manager.PrepareAsync(f.Release());
        Assert.Throws<ArgumentException>(() => other.Start(update));
        update.Dispose(); f.AssertNoStaging(); f.AssertUnchanged();
    }

    [Fact]
    public async Task ForeignAssetAndOverlappingWorkDirectoryAreRejected()
    {
        using var f = new Fixture(); using var manager = f.Manager(); var release = f.Release();
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.PrepareAsync(release with { Package = release.Package with { DownloadUrl = new("https://example.com/package.zip") } }));
        using var overlap = f.Manager(f.Options with { WorkDirectory = Path.Combine(f.Options.InstallDirectory, "updates") });
        await Assert.ThrowsAsync<ArgumentException>(() => overlap.PrepareAsync(release)); f.AssertUnchanged();
    }

    [Fact]
    public async Task LaterStartupCleansOnlyCompletedUpdatesAndRetainsTheirLogs()
    {
        using var f = new Fixture(); using var manager = f.Manager();
        using var completed = await manager.PrepareAsync(f.Release()); manager.Start(completed);
        await File.WriteAllTextAsync(completed.LogPath, "Quartermaster update completed.\n");
        using var later = f.Manager(f.Options with { CurrentVersion = "0.4.1" });
        Assert.Equal(0, await manager.CleanupCompletedAsync()); // Still running the older version.
        Assert.Equal(1, await later.CleanupCompletedAsync());
        Assert.Contains("completed", await File.ReadAllTextAsync(Path.Combine(f.Options.WorkDirectory, Path.GetFileName(completed.Directory) + ".log")));
        Assert.False(Directory.Exists(completed.Directory));
        using var failedManager = f.Manager(); using var failed = await failedManager.PrepareAsync(f.Release()); failedManager.Start(failed);
        await File.WriteAllTextAsync(failed.LogPath, "Quartermaster update failed: failure\n");
        Assert.Equal(0, await later.CleanupCompletedAsync()); Assert.True(Directory.Exists(failed.Directory));
    }
}
