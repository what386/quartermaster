using System.Diagnostics;

namespace Quartermaster.SelfUpdate;

internal static class LinuxUpdate
{
    public static int Apply(SelfUpdateOptions options, PreparedUpdate update, Func<ProcessStartInfo, int> launch)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Live replacement requires Linux.");
        UpdatePaths.ValidateRoots(options);
        UpdatePaths.CheckLinks(update.StagedDirectory);
        UpdatePaths.RequireRegularFile(Path.Combine(update.StagedDirectory, options.ExecutableName));
        var backup = Path.Combine(options.InstallDirectory, ".quartermaster-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var changes = new List<(string Target, string Original, bool Existed)>();
        var createdDirectories = new List<string>();
        var restarted = false;
        try
        {
            // Copy everything onto the installation filesystem before replacing any live files.
            var files = Directory.GetFiles(update.StagedDirectory, "*", SearchOption.AllDirectories);
            foreach (var source in files)
            {
                UpdatePaths.RequireRegularFile(source);
                var relative = Path.GetRelativePath(update.StagedDirectory, source);
                var target = Path.Combine(options.InstallDirectory, relative);
                UpdatePaths.CheckLinks(target);
                if (Directory.Exists(target)) throw new IOException("A directory occupies an update file path.");
                var incoming = Path.Combine(backup, "new", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(incoming)!);
                File.Copy(source, incoming);
                if (File.Exists(target)) File.SetUnixFileMode(incoming, File.GetUnixFileMode(target));
                if (relative == options.ExecutableName)
                    File.SetUnixFileMode(incoming, File.GetUnixFileMode(incoming) | UnixFileMode.UserExecute);
            }
            foreach (var source in files)
            {
                var relative = Path.GetRelativePath(update.StagedDirectory, source);
                var target = Path.Combine(options.InstallDirectory, relative);
                UpdatePaths.CheckLinks(target);
                var missing = new Stack<string>();
                for (var parent = Path.GetDirectoryName(target)!; !Directory.Exists(parent); parent = Path.GetDirectoryName(parent)!)
                    missing.Push(parent);
                while (missing.TryPop(out var parent)) { Directory.CreateDirectory(parent); createdDirectories.Add(parent); }
                var original = Path.Combine(backup, "old", relative);
                var existed = File.Exists(target);
                if (existed)
                {
                    UpdatePaths.RequireRegularFile(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Move(target, original);
                }
                changes.Add((target, original, existed));
                // Rename a new inode into place; never truncate the running executable.
                File.Move(Path.Combine(backup, "new", relative), target);
            }
            var pid = launch(new ProcessStartInfo(Path.Combine(options.InstallDirectory, options.ExecutableName))
            { UseShellExecute = false, WorkingDirectory = options.InstallDirectory });
            if (pid <= 0) throw new IOException("Could not restart Quartermaster.");
            restarted = true;
            // Once the new process runs, cleanup errors must not roll back its installation.
            try { File.AppendAllText(update.LogPath, "Quartermaster update completed.\n"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return pid;
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var (target, original, existed) in changes.AsEnumerable().Reverse())
                {
                    if (existed) File.Move(original, target, overwrite: true);
                    else File.Delete(target);
                }
                foreach (var directory in createdDirectories.AsEnumerable().Reverse()) Directory.Delete(directory);
            }
            catch (Exception rollback)
            {
                throw new AggregateException($"Update failed; recovery files remain at {backup}.", failure, rollback);
            }
            Directory.Delete(backup, true);
            throw;
        }
        finally
        {
            if (restarted)
                try { Directory.Delete(backup, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
