using Quartermaster.Core.Deployment;
using Quartermaster.Core.Patching;
using Quartermaster.Gui.Services;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui.Mods;

internal static class DeploymentMessages
{
    public static IReadOnlyDictionary<Guid, string> ForProfile(LibrarySession session, Profile profile)
    {
        var mods = session.State.Mods.ToDictionary(mod => mod.Id);
        var inspection = session.Inspection;
        var plan = DeploymentPlanner.Create(ProfilePatches.Resolve(session.State, profile));
        var currentFiles = ProfileFileMods.Selection(session.State, profile);
        var previousFiles = session.FileModHealth.Selection;
        string Name(Guid id) => mods.TryGetValue(id, out var mod) ? mod.Name : id.ToString();
        var hasSelectionHistory = previousFiles.Count > 0 || session.FileModHealth.Signature is null;
        var added = currentFiles.Where(item => hasSelectionHistory && previousFiles.All(old => old.ModId != item.ModId)).Select(item => Name(item.ModId)).ToArray();
        var removed = previousFiles.Where(item => currentFiles.All(next => next.ModId != item.ModId)).Select(item => Name(item.ModId)).ToArray();
        var common = currentFiles.Select(item => item.ModId).Intersect(previousFiles.Select(item => item.ModId)).ToHashSet();
        var reordered = !currentFiles.Where(item => common.Contains(item.ModId)).Select(item => item.ModId)
            .SequenceEqual(previousFiles.Where(item => common.Contains(item.ModId)).Select(item => item.ModId));
        var result = new Dictionary<Guid, string>();
        foreach (var entry in profile.Entries)
        {
            var mod = mods[entry.ModId]; var issues = new List<string>();
            if (session.GameDirectory.Length == 0)
                issues.Add(Localizer.Text("Choose the Helldivers 2 game folder in Settings to check installed files."));
            else if (inspection is null)
                issues.Add(Localizer.Interpolate($"Deployment could not be verified in {session.GameDirectory}.\n{session.DeploymentProblem}\nCheck the game folder and access permissions in Settings, then retry."));
            else if (inspection.Ledger.Status != DeploymentStatus.Complete)
                issues.Add(inspection.Ledger.Status == DeploymentStatus.Deploying
                    ? Localizer.Text("The previous deployment did not finish. Purge patches, then apply this loadout again.")
                    : Localizer.Text("Deployment could not be verified because its saved record is unreadable or invalid. Purge patches, then apply this loadout again."));
            else
            {
                var owned = inspection.Ledger.Files.Where(file => file.SourceId == mod.Id).ToArray();
                var damaged = owned.Where(file => inspection.Files.All(tracked => tracked.Name != file.Name || tracked.Status != ManagedFileStatus.Present)).ToArray();
                foreach (var file in damaged)
                {
                    var status = inspection.Files.FirstOrDefault(tracked => tracked.Name == file.Name)?.Status;
                    issues.Add(status is null or ManagedFileStatus.Missing
                        ? Localizer.Interpolate($"Missing deployed file:\n  {file.Name}\nPurge patches, then apply this loadout to restore it.")
                        : Localizer.Interpolate($"Deployed file changed outside Quartermaster:\n  {file.Name}\nPurge patches, then apply this loadout to replace it."));
                }
                var sets = PatchSelection.Select(mod, entry);
                var selected = sets.Where(set => set.ContentKind == ModContentKind.ArchivePatch).ToArray();
                if (!entry.Enabled)
                {
                    if (owned.Length > 0 || session.FileModHealth.AppliedModIds.Contains(mod.Id))
                        issues.Add(Localizer.Text("This mod is disabled here, but its files are still installed. Apply this loadout or purge to remove them."));
                }
                else
                {
                    if (selected.Length > 0 && owned.Length == 0)
                        issues.Add(Localizer.Text("This mod's selected archive patches are not installed. Apply this loadout to install them."));
                    else if (selected.Length > 0 && owned.Length > 0)
                    {
                        if (owned.Any(file => file.SelectionHash != PatchSelection.OptionsHash(mod, entry)))
                            issues.Add(Localizer.Text("This mod's options changed since deployment. Apply this loadout to install the selected options."));
                        var originals = selected.SelectMany(set => set.Files.Select(file => (set.Id, File: file))).ToDictionary(item => (item.Id, item.File.Kind), item => item.File);
                        if (owned.Where(file => !file.GeneratedByRepair).Any(file => originals.TryGetValue((file.PatchSetId, file.Kind), out var source) && source.Sha256 != file.SourceSha256))
                            issues.Add(Localizer.Text("This mod's source files changed since deployment. Apply this loadout to install the current version."));
                        var planned = plan.Patches.Where(patch => patch.SourceId == mod.Id).SelectMany(patch => patch.Files.Select(file => (patch.PatchSetId, file.Kind, Name: PatchFiles.Name(patch.Archive, patch.Slot, file.Kind)))).ToArray();
                        if (planned.Any(file => owned.Any(old => old.PatchSetId == file.PatchSetId && old.Kind == file.Kind && old.Name != file.Name)))
                            issues.Add(Localizer.Text("This mod's archive priority changed because the loadout order or enabled mods changed. Apply this loadout to update its priority."));
                        if (originals.Count != owned.Count(file => !file.GeneratedByRepair) || owned.Where(file => !file.GeneratedByRepair).Any(file => !originals.ContainsKey((file.PatchSetId, file.Kind))))
                            issues.Add(Localizer.Text("This mod's selected patch variants differ from the installed variants. Apply this loadout to replace them."));
                    }
                    else if (selected.Length == 0 && owned.Length > 0)
                        issues.Add(Localizer.Text("Archive patches from an earlier option selection are still installed. Apply this loadout to remove them."));

                    if (mod.IsFileBased && !sets.Any(set => set.ContentKind == ModContentKind.FileMod) && session.FileModHealth.AppliedModIds.Contains(mod.Id))
                        issues.Add(Localizer.Text("File edits from an earlier option selection are still installed. Apply this loadout to remove them."));
                    if (mod.IsFileBased && sets.Any(set => set.ContentKind == ModContentKind.FileMod))
                    {
                        if (!session.FileModHealth.Complete)
                        {
                            if (session.FileModHealth.InspectionError is { } error)
                                issues.Add(Localizer.Interpolate($"The file-mod deployment could not be inspected.\nReason: {error}\nResolve this file-access or deployment-record problem before applying file mods again."));
                            foreach (var file in session.FileModHealth.Files.Where(file => !file.Matches))
                                issues.Add(file.Exists
                                    ? Localizer.Interpolate($"Merged config changed outside Quartermaster:\n  {file.Target}\nIts installed content no longer matches the last merge. If the game was updated, update your file mods. If the change was unexpected, verify game files in Steam before applying again.")
                                    : Localizer.Interpolate($"Missing merged config:\n  {file.Target}\nVerify the game files in Steam to restore it, then apply this loadout."));
                            if (session.FileModHealth.InspectionError is null && session.FileModHealth.Files.All(file => file.Matches))
                                issues.Add(Localizer.Text("The file-mod deployment did not finish. Purge file mods, then apply this loadout again."));
                        }
                        else if (!session.FileModSelectionMatches(profile))
                        {
                            var old = previousFiles.FirstOrDefault(item => item.ModId == mod.Id);
                            var next = currentFiles.First(item => item.ModId == mod.Id);
                            if (old is not null && old.OptionsHash != next.OptionsHash)
                                issues.Add(Localizer.Text("This mod's file-mod options changed since deployment. Apply this loadout to rebuild the merged configs."));
                            else if (old is not null && old.SourceHash != next.SourceHash)
                                issues.Add(Localizer.Text("This mod's config source files changed since deployment. Apply this loadout to rebuild the merged configs."));
                            if (reordered) issues.Add(Localizer.Text("The file mods' effective priority order changed. Apply this loadout to rebuild the merged configs in the new order."));
                            if (added.Length > 0) issues.Add(Localizer.Interpolate($"File mods not included in the installed merge:\n  {string.Join("\n  ", added)}\nApply this loadout to include them."));
                            if (removed.Length > 0) issues.Add(Localizer.Interpolate($"File mods still included in the installed merge:\n  {string.Join("\n  ", removed)}\nApply this loadout to remove their edits."));
                            if (previousFiles.Count == 0 && session.FileModHealth.Signature is not null)
                                issues.Add(Localizer.Text("The installed file-mod merge belongs to a different selection. Its older deployment record has no option or order details. Apply this loadout to rebuild it."));
                        }
                        else if (!session.FileModHealth.AppliedModIds.Contains(mod.Id))
                            issues.Add(Localizer.Text("This mod was skipped in the installed file-mod merge. See its error tooltip for the conflicting mod and config file."));
                    }
                }
            }
            if (issues.Count > 0) result[mod.Id] = Localizer.Interpolate($"Mod:\n  {mod.Name}\n\n{string.Join("\n\n", issues.Distinct())}");
        }
        return result;
    }

    public static string? Launch(LibrarySession session, Profile profile)
    {
        var inspection = session.Inspection;
        if (inspection is null) return Localizer.Interpolate($"Could not inspect the game folder:\n  {session.GameDirectory}\n\n{session.DeploymentProblem}\n\nCheck the game folder and access permissions in Settings, then retry.");
        var sections = new List<string>();
        if (inspection.Ledger.Status != DeploymentStatus.Complete)
            sections.Add(inspection.Ledger.Status == DeploymentStatus.Deploying
                ? Localizer.Text("The previous deployment did not finish. Purge patches, then apply this loadout before running the game.")
                : Localizer.Text("The deployment record is unreadable or invalid, so installed mods could not be verified. Purge patches, then apply this loadout before running the game."));
        foreach (var group in inspection.Files.Where(file => file.Status != ManagedFileStatus.Present).GroupBy(file => inspection.Ledger.Files.First(owned => owned.Name == file.Name).SourceId))
        {
            var name = session.State.Mods.FirstOrDefault(mod => mod.Id == group.Key)?.Name ?? group.Key.ToString();
            sections.Add(Localizer.Interpolate($"Damaged deployment for {name}:\n{string.Join("\n", group.Select(file => "  " + file.Name + (file.Status == ManagedFileStatus.Missing ? Localizer.Text(" (missing)") : Localizer.Text(" (modified)"))))}\nPurge patches, then apply this loadout to restore these files."));
        }
        if (inspection.UntrackedSlots.Count > 0)
            sections.Add(Localizer.Interpolate($"Unmanaged archive patches were found:\n{string.Join("\n", inspection.UntrackedSlots.Select(slot => $"  {slot.Archive}.patch_{slot.Slot}"))}\nQuartermaster cannot identify the mods that created them. Purge patches, then apply this loadout to remove them."));
        var mismatches = ForProfile(session, profile);
        if (sections.Count == 0) sections.AddRange(mismatches.Values);
        else if (!session.FileModHealth.Complete)
            sections.AddRange(session.FileModHealth.Files.Where(file => !file.Matches).Select(file => file.Exists
                ? Localizer.Interpolate($"Modified merged config:\n  {file.Target}\nResolve the external change before reapplying file mods.")
                : Localizer.Interpolate($"Missing merged config:\n  {file.Target}\nVerify the game files in Steam to restore it, then apply this loadout.")));
        if (!session.FileModHealth.Complete && mismatches.Count == 0)
            sections.Add(Localizer.Interpolate($"File-mod deployment could not be verified.\n{session.DeploymentProblem}\nResolve the config issue, then purge or reapply this loadout."));
        var selectedIds = profile.Entries.Where(entry => entry.Enabled).Select(entry => entry.ModId).ToHashSet();
        var extra = inspection.Ledger.Files.Select(file => file.SourceId).Concat(session.FileModHealth.AppliedModIds)
            .Distinct().Where(id => !selectedIds.Contains(id)).Select(id => session.State.Mods.FirstOrDefault(mod => mod.Id == id)?.Name ?? id.ToString()).ToArray();
        if (extra.Length > 0) sections.Add(Localizer.Interpolate($"Installed mods outside this loadout:\n  {string.Join("\n  ", extra)}\nApply this loadout to remove them."));
        if (inspection.Ledger.SelectionId != profile.Id || inspection.Ledger.Signature != DeploymentPlanner.Create(ProfilePatches.Resolve(session.State, profile)).Signature)
            sections.Add(Localizer.Interpolate($"The selected loadout is not deployed.\nSelected loadout: {profile.Name}\nInstalled loadout: {session.DeployedProfileName}\nApply the selected loadout before running, or continue with the installed mods."));
        return sections.Count == 0 ? null : string.Join("\n\n", sections.Distinct());
    }
}
