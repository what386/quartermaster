using System.Text.Json;

namespace Quartermaster.FileMerger;

public sealed record PlannedFileWrite(string Target, string ExpectedCurrentSha256, ReadOnlyMemory<byte> Original,
    ReadOnlyMemory<byte> Content, bool Restore);
public sealed record FileMergePlan(string GameDirectory, string? StateRevision, IReadOnlyList<PlannedFileWrite> Files,
    IReadOnlyList<MergeConflict> Conflicts, IReadOnlyList<SkippedFileMod> Skipped);
public sealed record InstalledConfigStatus(string Target, bool Matches);
public sealed record FileInstallInspection(bool Complete, IReadOnlyList<InstalledConfigStatus> Files);

/// <summary>Plans, applies and restores generated config files. State and content-addressed original backups are
/// kept outside the game directory. Prepare is read-only with respect to the game; apply revalidates its snapshot.</summary>
public sealed class FileModInstaller
{
    private readonly string stateDirectory;
    public FileModInstaller(string stateDirectory) => this.stateDirectory = Path.GetFullPath(stateDirectory);

    public async Task<FileInstallInspection> InspectAsync(string gameDirectory, CancellationToken ct = default)
    {
        var game = GameRoot(gameDirectory); var home = StateRoot(game);
        await using var lease = Lock(home);
        var (state, _) = await ReadState(home, game, ct).ConfigureAwait(false);
        if (state is null) return new(true, []);
        var files = new List<InstalledConfigStatus>();
        foreach (var file in state.Files)
        {
            var path = TargetPath(game, file.Target);
            files.Add(new(file.Target, File.Exists(path) && FileModPaths.Hash(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)) == file.AppliedSha256));
        }
        return new(state.Complete, files.ToArray());
    }
    private sealed record InstalledFile(string Target, string OriginalSha256, string AppliedSha256, string PreviousSha256);
    private sealed record InstallState(int Version, string GameDirectory, bool Complete, InstalledFile[] Files);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public async Task<FileMergePlan> PrepareAsync(string gameDirectory, IReadOnlyList<FileMod> orderedMods, CancellationToken ct = default)
    {
        var game = GameRoot(gameDirectory); var home = StateRoot(game);
        await using var lease = Lock(home);
        var (state, revision) = await ReadState(home, game, ct).ConfigureAwait(false);
        if (state is { Complete: false }) throw new IOException("A file-mod operation was interrupted. Restore file mods before applying a new plan.");
        var mods = orderedMods.GroupBy(mod => FileModPaths.ValidateTarget(mod.Target), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var targets = mods.Keys.Concat(state?.Files.Select(file => file.Target) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var writes = new List<PlannedFileWrite>(); var conflicts = new List<MergeConflict>(); var skipped = new List<SkippedFileMod>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var path = TargetPath(game, target);
            var current = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var currentHash = FileModPaths.Hash(current);
            var previous = state?.Files.SingleOrDefault(file => file.Target == target);
            byte[] original;
            if (previous is null) original = current;
            else if (currentHash == previous.AppliedSha256 || currentHash == previous.OriginalSha256)
                original = await ReadBackup(home, previous, ct).ConfigureAwait(false);
            else
            {
                // An updated mod may identify a new stock game file after a game update. Never restore the old
                // backup over it; only adopt it when all incoming packages explicitly recognize its fingerprint.
                if (!mods.TryGetValue(target, out var incoming) || incoming.Any(mod => mod.StockFingerprints.Count == 0 ||
                    !mod.StockFingerprints.Contains(FileModPaths.Fingerprint(current), StringComparer.OrdinalIgnoreCase)))
                    throw Changed(target);
                original = current;
            }
            if (!mods.TryGetValue(target, out var selected))
                writes.Add(new(target, currentHash, original, original, true));
            else
            {
                var result = ConfigMerger.Merge(target, original, selected, ct);
                writes.Add(new(target, currentHash, original, result.Content, false));
                conflicts.AddRange(result.Conflicts); skipped.AddRange(result.Skipped);
            }
        }
        return new(game, revision, writes.ToArray(), conflicts.ToArray(), skipped.ToArray());
    }

    public async Task ApplyAsync(FileMergePlan plan, CancellationToken ct = default)
    {
        var game = GameRoot(plan.GameDirectory); var home = StateRoot(game);
        await using var lease = Lock(home);
        await ApplyLocked(plan, game, home, ct).ConfigureAwait(false);
    }

    /// <summary>Restores verified originals, including recovery after an interrupted apply. A newer/external file
    /// is never replaced by an old backup. All targets and backups are checked before any game file is changed.</summary>
    public async Task RestoreAsync(string gameDirectory, CancellationToken ct = default)
    {
        var game = GameRoot(gameDirectory); var home = StateRoot(game);
        await using var lease = Lock(home);
        var (state, revision) = await ReadState(home, game, ct).ConfigureAwait(false);
        if (state is null || state.Files.Length == 0) return;
        var writes = new List<PlannedFileWrite>();
        foreach (var file in state.Files)
        {
            var current = await File.ReadAllBytesAsync(TargetPath(game, file.Target), ct).ConfigureAwait(false);
            var hash = FileModPaths.Hash(current);
            if (hash != file.AppliedSha256 && hash != file.OriginalSha256 && (state.Complete || hash != file.PreviousSha256)) throw Changed(file.Target);
            var original = await ReadBackup(home, file, ct).ConfigureAwait(false);
            writes.Add(new(file.Target, hash, original, original, true));
        }
        await ApplyLocked(new(game, revision, writes.ToArray(), [], []), game, home, ct).ConfigureAwait(false);
    }

    private async Task ApplyLocked(FileMergePlan plan, string game, string home, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (previous, revision) = await ReadState(home, game, ct).ConfigureAwait(false);
        if (plan.StateRevision != revision) throw new IOException("File-mod state changed after planning. Prepare a new merge plan.");
        if (plan.Files.Select(file => FileModPaths.ValidateTarget(file.Target)).Distinct(StringComparer.Ordinal).Count() != plan.Files.Count)
            throw new InvalidDataException("A merge plan contains duplicate config targets.");
        if ((previous?.Files ?? []).Any(file => !plan.Files.Any(write => write.Target == file.Target)))
            throw new InvalidDataException("The merge plan omits a previously managed config. Prepare a complete plan instead.");
        var staged = new List<(PlannedFileWrite File, string Path, string Temporary, byte[] Before)>();
        var pending = new List<InstalledFile>();
        var published = new List<int>();
        var journalWritten = false;
        try
        {
            foreach (var file in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = TargetPath(game, file.Target);
                var before = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                if (FileModPaths.Hash(before) != file.ExpectedCurrentSha256) throw Changed(file.Target);
                var originalPlain = GameConfigCodec.Decrypt(file.Original.Span);
                ConfigDocument.Parse(originalPlain);
                var outputPlain = GameConfigCodec.Decrypt(file.Content.Span);
                ConfigDocument.Parse(outputPlain);
                if (ConfigHash.Compute(originalPlain) != ConfigHash.Compute(outputPlain)) throw new InvalidDataException($"Invalid merged game hash: {file.Target}");
                if (file.Restore && !file.Original.Span.SequenceEqual(file.Content.Span)) throw new InvalidDataException("Restore content differs from its original backup.");
                var originalHash = FileModPaths.Hash(file.Original.Span);
                var backup = BackupPath(home, originalHash);
                if (File.Exists(backup))
                {
                    if (FileModPaths.Hash(await File.ReadAllBytesAsync(backup, ct).ConfigureAwait(false)) != originalHash)
                        throw new IOException($"File-mod original backup is corrupt: {file.Target}");
                }
                else await AtomicWrite(backup, file.Original, ct).ConfigureAwait(false);
                var temporary = path + ".quartermaster-" + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add((file, path, temporary, before));
                await File.WriteAllBytesAsync(temporary, file.Content.ToArray(), ct).ConfigureAwait(false);
                if (FileModPaths.Hash(await File.ReadAllBytesAsync(temporary, ct).ConfigureAwait(false)) != FileModPaths.Hash(file.Content.Span))
                    throw new IOException($"Staged config verification failed: {file.Target}");
                pending.Add(new(file.Target, originalHash, FileModPaths.Hash(file.Content.Span), file.ExpectedCurrentSha256));
            }
            ct.ThrowIfCancellationRequested();
            // Journal before publication: a process crash can always be recovered with RestoreAsync.
            await SaveState(home, new(1, game, false, pending.ToArray()), ct).ConfigureAwait(false);
            journalWritten = true;
            for (var index = 0; index < staged.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var item = staged[index]; FileModDetector.RejectLink(item.Path);
                if (FileModPaths.Hash(await File.ReadAllBytesAsync(item.Path, ct).ConfigureAwait(false)) != item.File.ExpectedCurrentSha256) throw Changed(item.File.Target);
                File.Move(item.Temporary, item.Path, true);
                published.Add(index);
                if (FileModPaths.Hash(await File.ReadAllBytesAsync(item.Path, ct).ConfigureAwait(false)) != pending[index].AppliedSha256)
                    throw new IOException($"Applied config verification failed: {item.File.Target}");
            }
            var installed = pending.Where((_, index) => !staged[index].File.Restore).ToArray();
            await SaveState(home, new(1, game, true, installed), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            if (journalWritten)
            {
                try
                {
                    foreach (var index in published.AsEnumerable().Reverse())
                    {
                        var item = staged[index];
                        if (FileModPaths.Hash(await File.ReadAllBytesAsync(item.Path).ConfigureAwait(false)) != pending[index].AppliedSha256) throw Changed(item.File.Target);
                        await AtomicWrite(item.Path, item.Before, CancellationToken.None).ConfigureAwait(false);
                    }
                    await SaveState(home, previous ?? new(1, game, true, []), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollback)
                { throw new AggregateException("File-mod apply failed; recovery is incomplete. Restore file mods before applying again.", failure, rollback); }
            }
            throw;
        }
        finally
        {
            foreach (var item in staged) if (File.Exists(item.Temporary)) File.Delete(item.Temporary);
        }
    }

    private static IOException Changed(string target) => new($"Config '{target}' changed outside Quartermaster or was updated by the game. Refusing to overwrite it with an old file.");
    private static string GameRoot(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        FileModDetector.RejectLink(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Game installation directory does not exist.");
        return path;
    }
    private string StateRoot(string game)
    {
        var key = OperatingSystem.IsWindows() ? game.ToUpperInvariant() : game;
        var home = Path.Combine(stateDirectory, FileModPaths.Hash(System.Text.Encoding.UTF8.GetBytes(key)));
        FileModDetector.RejectLink(home);
        return home;
    }
    private static FileStream Lock(string home)
    {
        FileModDetector.RejectLink(home); Directory.CreateDirectory(home);
        var path = Path.Combine(home, "operation.lock"); FileModDetector.RejectLink(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static string TargetPath(string game, string target)
    {
        var path = Path.Combine(game, FileModPaths.ValidateTarget(target).Replace('/', Path.DirectorySeparatorChar));
        FileModDetector.RejectLink(path);
        return path;
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string BackupPath(string home, string hash)
    {
        if (!IsHash(hash)) throw new InvalidDataException("Invalid file-mod backup hash.");
        var path = Path.Combine(home, hash + ".original"); FileModDetector.RejectLink(path); return path;
    }
    private static async Task<byte[]> ReadBackup(string home, InstalledFile file, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(BackupPath(home, file.OriginalSha256), ct).ConfigureAwait(false);
        if (FileModPaths.Hash(bytes) != file.OriginalSha256) throw new IOException($"File-mod original backup is corrupt: {file.Target}");
        return bytes;
    }
    private static async Task<(InstallState? State, string? Revision)> ReadState(string home, string game, CancellationToken ct)
    {
        var path = Path.Combine(home, "state.json"); FileModDetector.RejectLink(path);
        if (!File.Exists(path)) return (null, null);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        InstallState state;
        try { state = JsonSerializer.Deserialize<InstallState>(bytes) ?? throw new InvalidDataException("Invalid file-mod state."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid file-mod state JSON.", ex); }
        if (state.Version != 1 || !string.Equals(state.GameDirectory, game, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || state.Files is null ||
            state.Files.Any(file => file is null || file.Target is null || !IsHash(file.OriginalSha256) || !IsHash(file.AppliedSha256) || !IsHash(file.PreviousSha256)) ||
            state.Files.Select(file => FileModPaths.ValidateTarget(file.Target)).Distinct(StringComparer.Ordinal).Count() != state.Files.Length)
            throw new InvalidDataException("Invalid file-mod state target, version or fingerprints.");
        return (state, FileModPaths.Hash(bytes));
    }
    private static Task SaveState(string home, InstallState state, CancellationToken ct) =>
        AtomicWrite(Path.Combine(home, "state.json"), JsonSerializer.SerializeToUtf8Bytes(state, Json), ct);
    private static async Task AtomicWrite(string path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        FileModDetector.RejectLink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content.ToArray(), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); FileModDetector.RejectLink(path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
