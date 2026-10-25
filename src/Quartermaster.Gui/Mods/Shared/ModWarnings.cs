using Quartermaster.Core.Deployment;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui.Mods;

internal static class ModWarnings
{
    public static IReadOnlyDictionary<Guid, string> ForProfile(LibraryState state, Profile? profile, ConflictReport? report = null,
        IReadOnlyDictionary<Guid, string>? diagnostics = null)
    {
        if (profile is null) return new Dictionary<Guid, string>();
        var warnings = profile.Entries.ToDictionary(entry => entry.ModId, _ => new List<string>());
        var mods = state.Mods.ToDictionary(mod => mod.Id);
        var enabled = profile.Entries.Where(entry => entry.Enabled).Select(entry => entry.ModId).ToHashSet();
        foreach (var id in enabled)
        {
            foreach (var dependency in mods[id].Dependencies.Where(dependency => dependency.CanInstall))
            {
                var matches = state.Mods.Where(mod => ModDependencyMatching.Matches(mod, dependency)).ToArray();
                if (matches.Any(mod => enabled.Contains(mod.Id))) continue;
                var disabled = matches.Where(mod => warnings.ContainsKey(mod.Id)).ToArray();
                var instruction = disabled.Length > 0
                    ? Localizer.Interpolate($"Enable {dependency.Name} in this loadout.")
                    : matches.Any(mod => !mod.Superseded)
                        ? Localizer.Interpolate($"Add {dependency.Name} from Library to this loadout and enable it.")
                        : Localizer.Interpolate($"Download or import {dependency.Name}, then add it to this loadout and enable it.");
                warnings[id].Add(Localizer.Interpolate($"Dependency:\n  {dependency.Name}\n\n{instruction}\nDependency information comes from the mod's metadata and may be outdated."));
                foreach (var mod in disabled) warnings[mod.Id].Add(Localizer.Interpolate($"Required by:\n  {mods[id].Name}\n\nEnable {mod.Name} in this loadout, or disable {mods[id].Name} if you do not want to use it."));
            }
        }
        var last = ProfileEditor.InDeploymentOrder(profile).LastOrDefault(entry => entry.Enabled)?.ModId;
        foreach (var id in enabled.Where(id => id != last && IsBingusSharedLoader(mods[id])))
            warnings[id].Add(Localizer.Interpolate($"Bingus Shared Loader should load last. Move it to the {(profile.Priority == PriorityDirection.FirstWins ? Localizer.Text("top") : Localizer.Text("bottom"))} of the enabled mods."));
        report ??= ConflictAnalyzer.Analyze(ProfilePatches.Resolve(state, profile));
        foreach (var id in enabled)
        {
            foreach (var clashes in report.Resources.Where(conflict => conflict.SourceIds.Contains(id))
                .GroupBy(conflict => (conflict.Archive, conflict.WinningSourceId, Others: string.Join(", ", conflict.SourceIds.Where(other => other != id).Select(other => mods[other].Name).Order()))))
            {
                var winner = mods[clashes.Key.WinningSourceId].Name;
                var losing = clashes.SelectMany(clash => clash.SourceIds).Distinct().Where(other => other != clashes.Key.WinningSourceId)
                    .Select(other => "  " + mods[other].Name);
                warnings[id].Add(Localizer.Interpolate($"Archive:\n  {clashes.Key.Archive}\n\nApplied mod:\n  {winner}\n\nOverridden mods:\n{string.Join("\n", losing)}\n\nThese mods replace the same game assets in this archive. Only {winner}'s replacements for those assets are used. Unrelated assets are kept.\n\nDisable a conflicting mod or choose different options to keep its replacements. Reordering only changes which mod wins."));
            }
        }
        foreach (var (id, message) in diagnostics ?? new Dictionary<Guid, string>())
            if (enabled.Contains(id) && warnings.TryGetValue(id, out var messages) && message.Length > 0) messages.Add(message);
        return warnings.ToDictionary(pair => pair.Key, pair => string.Join("\n\n", pair.Value.Distinct()));
    }

    public static string ForLibraryMod(LibraryState state, Mod mod, IReadOnlyDictionary<Guid, string> profileWarnings,
        IReadOnlyDictionary<Guid, string>? diagnostics = null)
    {
        var missing = mod.Dependencies.Where(dependency => dependency.CanInstall && !state.Mods.Any(installed => !installed.Superseded && ModDependencyMatching.Matches(installed, dependency)))
            .Select(dependency => Localizer.Interpolate($"Dependency:\n  {dependency.Name}\n\nDownload or import {dependency.Name}, then add it to the same loadout and enable it.\nDependency information comes from the mod's metadata and may be outdated."));
        return string.Join("\n\n", missing.Prepend(profileWarnings.GetValueOrDefault(mod.Id, ""))
            .Prepend(diagnostics?.GetValueOrDefault(mod.Id, "") ?? "").Where(text => text.Length > 0).Distinct());
    }

    private static bool IsBingusSharedLoader(Mod mod)
    {
        if (mod.Sources.Any(source => source.Provider == "nexusmods" && source.ModId == "16292" ||
            source.Provider == "github" && source.ModId.Equals("CowboyBingus/BingusSharedLoader", StringComparison.OrdinalIgnoreCase))) return true;
        var name = string.Concat(mod.Name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return name == "bingussharedloader" || System.Text.RegularExpressions.Regex.IsMatch(name, @"^bingussharedloaderv?\d");
    }
}
