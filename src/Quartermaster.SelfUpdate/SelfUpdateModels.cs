using System.Runtime.InteropServices;

namespace Quartermaster.SelfUpdate;

public sealed record SelfUpdateOptions(string CurrentVersion, string RuntimeIdentifier, string InstallDirectory, string WorkDirectory)
{
    public string Repository { get; init; } = "what386/quartermaster";
    public long MaxDownloadBytes { get; init; } = 512L * 1024 * 1024;
    public long MaxExtractedBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxFiles { get; init; } = 4096;
    public string ExecutableName => "Quartermaster.Gui" + (RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "");
    public bool UsesWindowsUpdater => RuntimeIdentifier == "win-x64";
    public string UpdaterName => "Quartermaster.Updater" + (RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "");
    public string AssetName => $"Quartermaster-{RuntimeIdentifier}-bundled.zip";

    public static SelfUpdateOptions ForCurrentApplication(string currentVersion, string workDirectory)
    {
        var system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" :
            throw new PlatformNotSupportedException("Self-update supports Windows and Linux.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Self-update supports x64 applications.");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the application executable.");
        var options = new SelfUpdateOptions(currentVersion, $"{system}-x64", Path.GetDirectoryName(executable)!, workDirectory);
        if (!Path.GetFileName(executable).Equals(options.ExecutableName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Self-update is available for portable releases. Launch the published Quartermaster executable.");
        return options;
    }
}

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256 = null);
public sealed record AppRelease(string Version, Uri Page, string Notes, DateTimeOffset? PublishedAt, ReleaseAsset Package, ReleaseAsset Checksums);
public enum SelfUpdatePhase { Downloading, Verifying, Extracting, Ready }
public sealed record SelfUpdateProgress(SelfUpdatePhase Phase, long Bytes = 0, long? TotalBytes = null, string? File = null);

/// <summary>Dispose an abandoned preparation; a launched update retains its staging area and log.</summary>
public sealed class PreparedUpdate : IDisposable
{
    internal PreparedUpdate(SelfUpdateManager owner, AppRelease release, string directory, string stagedDirectory, string updaterPath, string logPath)
    { Owner = owner; Release = release; Directory = directory; StagedDirectory = stagedDirectory; UpdaterPath = updaterPath; LogPath = logPath; }
    internal SelfUpdateManager Owner { get; }
    internal bool Started { get; set; }
    internal bool Disposed { get; set; }
    public AppRelease Release { get; }
    public string Directory { get; }
    public string StagedDirectory { get; }
    public string UpdaterPath { get; }
    public string LogPath { get; }
    public void Dispose() => Owner.Discard(this);
}
