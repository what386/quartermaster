using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Quartermaster.SelfUpdate;

/// <summary>Prepares portable application updates. The caller owns prompting and clean GUI shutdown.</summary>
public sealed partial class SelfUpdateManager : IDisposable
{
    private const int MetadataLimit = 1024 * 1024;
    private readonly SelfUpdateOptions options;
    private readonly HttpClient api;
    private readonly HttpClient downloads;
    private readonly bool ownsApi;
    private readonly bool ownsDownloads;
    private readonly Func<ProcessStartInfo, int> launch;
    private readonly ReleaseVersion installedVersion;
    private readonly object handoffLock = new();
    private bool handedOff;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed record GitHubAsset(
        string Name,
        string BrowserDownloadUrl,
        long Size,
        string? Digest
    );

    private sealed record GitHubRelease(
        string TagName,
        string HtmlUrl,
        string? Body,
        bool Draft,
        bool Prerelease,
        DateTimeOffset? PublishedAt,
        GitHubAsset[] Assets
    );

    public SelfUpdateManager(
        SelfUpdateOptions options,
        HttpClient? api = null,
        HttpClient? downloads = null,
        Func<ProcessStartInfo, int>? launch = null
    )
    {
        if (!RepositoryPattern().IsMatch(options.Repository) || options.Repository.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Invalid release repository.", nameof(options));
        if (options.RuntimeIdentifier is not ("win-x64" or "linux-x64"))
            throw new PlatformNotSupportedException(
                "No portable release exists for this runtime identifier."
            );
        if (
            options.MaxDownloadBytes <= 0
            || options.MaxExtractedBytes <= 0
            || options.MaxFiles <= 0
        )
            throw new ArgumentException("Update limits must be positive.", nameof(options));
        installedVersion = ReleaseVersion.Parse(options.CurrentVersion);
        this.options = options with
        {
            InstallDirectory = UpdatePaths.ResolveRoot(options.InstallDirectory),
            WorkDirectory = UpdatePaths.ResolveRoot(options.WorkDirectory),
        };
        this.api = api ?? new HttpClient();
        ownsApi = api is null;
        this.downloads = downloads ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        ownsDownloads = downloads is null;
        this.launch =
            launch
            ?? (
                info =>
                {
                    using var process =
                        Process.Start(info)
                        ?? throw new IOException("Could not launch the update process.");
                    return process.Id;
                }
            );
    }

    /// <summary>Returns the newest published stable release only when it is newer than the installed version.</summary>
    public async Task<AppRelease?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{options.Repository}/releases/latest"
        );
        request.Headers.UserAgent.ParseAdd("Quartermaster");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await api.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            )
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var release =
            JsonSerializer.Deserialize<GitHubRelease>(
                await ReadLimitedAsync(response, MetadataLimit, ct).ConfigureAwait(false),
                Json
            ) ?? throw new InvalidDataException("GitHub returned an empty release.");
        if (release.Draft || release.Prerelease)
            return null;
        if (string.IsNullOrWhiteSpace(release.TagName) || release.Assets is null)
            throw new InvalidDataException("GitHub returned invalid release metadata.");
        if (ReleaseVersion.Parse(release.TagName).CompareTo(installedVersion) <= 0)
            return null;
        var page = new Uri(
            $"https://github.com/{options.Repository}/releases/tag/{Uri.EscapeDataString(release.TagName)}"
        );
        if (
            !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var reportedPage)
            || reportedPage.AbsoluteUri != page.AbsoluteUri
        )
            throw new InvalidDataException(
                "The release page belongs to another repository or version."
            );
        ReleaseAsset Asset(string name)
        {
            var assets = release.Assets.Where(asset => asset.Name == name).ToArray();
            if (assets.Length != 1)
                throw new InvalidDataException(
                    $"The release must contain exactly one {name} asset."
                );
            var asset = assets[0];
            var result = new ReleaseAsset(
                name,
                new Uri(asset.BrowserDownloadUrl),
                asset.Size,
                Digest(asset.Digest)
            );
            ValidateAsset(
                result,
                release.TagName,
                name,
                name == "SHA256SUMS.txt" ? MetadataLimit : options.MaxDownloadBytes
            );
            return result;
        }
        return new(
            release.TagName,
            page,
            release.Body ?? "",
            release.PublishedAt,
            Asset(options.AssetName),
            Asset("SHA256SUMS.txt")
        );
    }

    public async Task<PreparedUpdate> PrepareAsync(
        AppRelease release,
        IProgress<SelfUpdateProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (ReleaseVersion.Parse(release.Version).CompareTo(installedVersion) <= 0)
            throw new InvalidOperationException(
                "The release is not newer than the installed application."
            );
        ValidateAsset(
            release.Package,
            release.Version,
            options.AssetName,
            options.MaxDownloadBytes
        );
        ValidateAsset(release.Checksums, release.Version, "SHA256SUMS.txt", MetadataLimit);
        UpdatePaths.ValidateRoots(options);
        var installedHelper = Path.Combine(
            options.InstallDirectory,
            "winupdater",
            options.UpdaterName
        );
        UpdatePaths.RequireRegularFile(
            Path.Combine(options.InstallDirectory, options.ExecutableName)
        );
        if (options.UsesWindowsUpdater) UpdatePaths.RequireRegularFile(installedHelper);
        Directory.CreateDirectory(options.WorkDirectory);
        UpdatePaths.CheckLinks(options.WorkDirectory);
        var directory = Path.Combine(
            options.WorkDirectory,
            "update-" + Guid.NewGuid().ToString("N")
        );
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var checksums = await GetAssetBytesAsync(release.Checksums, ct).ConfigureAwait(false);
            var checksum = ChecksumFor(Encoding.UTF8.GetString(checksums), options.AssetName);
            if (
                release.Package.Sha256 is { } digest
                && !digest.Equals(checksum, StringComparison.OrdinalIgnoreCase)
            )
                throw new InvalidDataException("Release digest and SHA256SUMS.txt disagree.");
            var archive = Path.Combine(directory, "release.zip");
            await DownloadAsync(release.Package, archive, checksum, progress, ct)
                .ConfigureAwait(false);
            var staged = Path.Combine(directory, "staged");
            await ReleaseExtraction
                .ExtractAsync(archive, staged, options, progress, ct)
                .ConfigureAwait(false);
            var helper = "";
            if (options.UsesWindowsUpdater)
            {
                var runner = Path.Combine(directory, "runner");
                Directory.CreateDirectory(runner);
                helper = Path.Combine(runner, options.UpdaterName);
                File.Copy(installedHelper, helper);
            }
            File.Delete(archive);
            await File.WriteAllTextAsync(Path.Combine(directory, "version.txt"), release.Version, ct).ConfigureAwait(false);
            var log = Path.Combine(directory, "update.log");
            await File.WriteAllTextAsync(log, "", ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(SelfUpdatePhase.Ready));
            return new(this, release, directory, staged, helper, log);
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }

    /// <summary>After this returns, close the GUI cleanly. Cancellation is checked before the handoff only.</summary>
    public int Start(PreparedUpdate update, CancellationToken ct = default)
    {
        lock (handoffLock)
        {
            if (!ReferenceEquals(update.Owner, this) || update.Disposed)
                throw new ArgumentException(
                    "The preparation does not belong to this updater or was disposed.",
                    nameof(update)
                );
            if (handedOff || update.Started)
                throw new InvalidOperationException(
                    "An application update has already been launched."
                );
            ct.ThrowIfCancellationRequested();
            if (!options.UsesWindowsUpdater)
            {
                var restarted = LinuxUpdate.Apply(options, update, launch);
                update.Started = true;
                handedOff = true;
                return restarted;
            }
            UpdatePaths.RequireRegularFile(update.UpdaterPath);
            UpdatePaths.RequireRegularFile(
                Path.Combine(update.StagedDirectory, options.ExecutableName)
            );
            var info = new ProcessStartInfo(update.UpdaterPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(update.UpdaterPath)!,
            };
            foreach (
                var argument in new[]
                {
                    Environment.ProcessId.ToString(
                        global::System.Globalization.CultureInfo.InvariantCulture
                    ),
                    update.StagedDirectory,
                    options.InstallDirectory,
                    options.ExecutableName,
                    "60000",
                    update.LogPath,
                }
            )
                info.ArgumentList.Add(argument);
            ct.ThrowIfCancellationRequested();
            var pid = launch(info);
            if (pid <= 0)
                throw new IOException("Could not launch the update process.");
            update.Started = true;
            handedOff = true;
            return pid;
        }
    }

    internal void Discard(PreparedUpdate update)
    {
        lock (handoffLock)
        {
            if (update.Disposed) return;
            if (!update.Started && Directory.Exists(update.Directory)) Directory.Delete(update.Directory, true);
            update.Disposed = true;
        }
    }

    /// <summary>Call on a later startup. Only completed updates at or below the running version are removed; logs are retained.</summary>
    public async Task<int> CleanupCompletedAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(options.WorkDirectory)) return 0;
        UpdatePaths.CheckLinks(options.WorkDirectory);
        var count = 0;
        foreach (var directory in Directory.EnumerateDirectories(options.WorkDirectory, "update-*"))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory)[7..], "N", out _)) continue;
            try
            {
                UpdatePaths.CheckLinks(directory);
                var version = Path.Combine(directory, "version.txt"); var log = Path.Combine(directory, "update.log");
                UpdatePaths.RequireRegularFile(version); UpdatePaths.RequireRegularFile(log);
                if (new FileInfo(version).Length > 256 || new FileInfo(log).Length > MetadataLimit) continue;
                if (ReleaseVersion.Parse(await File.ReadAllTextAsync(version, ct).ConfigureAwait(false)).CompareTo(installedVersion) > 0) continue;
                var text = await File.ReadAllTextAsync(log, ct).ConfigureAwait(false);
                if (!text.Split('\n').Any(line => line.TrimEnd('\r') == "Quartermaster update completed.")) continue;
                var retainedLog = Path.Combine(options.WorkDirectory, Path.GetFileName(directory) + ".log");
                UpdatePaths.CheckLinks(retainedLog);
                File.Copy(log, retainedLog, overwrite: true);
                // Try the helper first so a Windows sharing violation leaves the receipt available for retry.
                var runner = Path.Combine(directory, "runner");
                UpdatePaths.CheckLinks(runner);
                if (Directory.Exists(runner)) Directory.Delete(runner, true);
                Directory.Delete(directory, true); count++;
            }
            catch (IOException) { } // A still-exiting helper may briefly hold its executable or log open.
            catch (FormatException) { } // Ignore folders without a recognized version receipt.
        }
        return count;
    }

    private void ValidateAsset(ReleaseAsset asset, string version, string name, long limit)
    {
        var expected = new Uri(
            $"https://github.com/{options.Repository}/releases/download/{Uri.EscapeDataString(version)}/{name}"
        );
        if (
            asset.Name != name
            || !asset.DownloadUrl.IsAbsoluteUri || asset.DownloadUrl.AbsoluteUri != expected.AbsoluteUri
            || asset.Size <= 0
            || asset.Size > limit
        )
            throw new InvalidDataException("Release asset URL, name, or size is invalid.");
        if (asset.Sha256 is { } digest && (digest.Length != 64 || !digest.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid release checksum.");
    }

    private static string? Digest(string? value) =>
        value is null ? null
        : value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        && value.Length == 71
        && value[7..].All(Uri.IsHexDigit)
            ? value[7..]
        : throw new InvalidDataException("Invalid GitHub release digest.");

    private static string ChecksumFor(string text, string name)
    {
        var hashes = text.Split('\n')
            .Select(line =>
                line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
            )
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == name)
            .Select(parts => parts[0])
            .ToArray();
        if (hashes.Length != 1 || hashes[0].Length != 64 || !hashes[0].All(Uri.IsHexDigit))
            throw new InvalidDataException("Missing, duplicate, or invalid package checksum.");
        return hashes[0];
    }

    private async Task<byte[]> GetAssetBytesAsync(ReleaseAsset asset, CancellationToken ct)
    {
        using var response = await downloads
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadLimitedAsync(response, MetadataLimit, ct).ConfigureAwait(false);
        if (
            bytes.LongLength != asset.Size
            || asset.Sha256 is { } digest
                && !digest.Equals(
                    Convert.ToHexString(SHA256.HashData(bytes)),
                    StringComparison.OrdinalIgnoreCase
                )
        )
            throw new InvalidDataException("Release checksum file size or digest does not match.");
        return bytes;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpResponseMessage response,
        int limit,
        CancellationToken ct
    )
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit)
                throw new InvalidDataException("Release metadata exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private async Task DownloadAsync(
        ReleaseAsset asset,
        string destination,
        string checksum,
        IProgress<SelfUpdateProgress>? progress,
        CancellationToken ct
    )
    {
        using var response = await downloads
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            65536,
            useAsync: true
        );
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var buffer = new byte[65536];
        int count;
        progress?.Report(new(SelfUpdatePhase.Downloading, 0, asset.Size));
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > asset.Size)
                throw new InvalidDataException("Release download exceeds its advertised size.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            progress?.Report(new(SelfUpdatePhase.Downloading, total, asset.Size));
        }
        progress?.Report(new(SelfUpdatePhase.Verifying, total, asset.Size));
        if (
            total != asset.Size
            || !checksum.Equals(
                Convert.ToHexString(hash.GetHashAndReset()),
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new InvalidDataException(
                "Release download is incomplete or its checksum does not match."
            );
    }

    public void Dispose()
    {
        if (ownsApi)
            api.Dispose();
        if (ownsDownloads)
            downloads.Dispose();
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex RepositoryPattern();
}
