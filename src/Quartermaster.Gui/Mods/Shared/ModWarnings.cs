using Quartermaster.Core.Deployment;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui.Mods;

internal static class ModWarnings
{
    public static IReadOnlyDictionary<Guid, string> ForProfile(LibraryState state, Profile? profile, ConflictReport? report = null)
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
                warnings[id].Add(Localizer.Interpolate($"Requires {dependency.Name} ({(disabled.Length > 0 ? Localizer.Text("disabled") : Localizer.Text("missing from this profile"))})."));
                foreach (var mod in disabled) warnings[mod.Id].Add(Localizer.Interpolate($"Required by enabled mod {mods[id].Name}."));
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
                warnings[id].Add(Localizer.Interpolate($"Clashes with {clashes.Key.Others} in {clashes.Key.Archive} ({ModPresentation.Count(clashes.Count(), "overlapping resource")}); {mods[clashes.Key.WinningSourceId].Name} wins."));
        }
        return warnings.ToDictionary(pair => pair.Key, pair => string.Join("\n", pair.Value.Distinct()));
    }

    public static string ForLibraryMod(LibraryState state, Mod mod, IReadOnlyDictionary<Guid, string> profileWarnings)
    {
        var missing = mod.Dependencies.Where(dependency => dependency.CanInstall && !state.Mods.Any(installed => !installed.Superseded && ModDependencyMatching.Matches(installed, dependency)))
            .Select(dependency => Localizer.Interpolate($"Requires {dependency.Name} (missing from your library)."));
        return string.Join("\n", missing.Prepend(profileWarnings.GetValueOrDefault(mod.Id, "")).Where(text => text.Length > 0).Distinct());
    }

    private static bool IsBingusSharedLoader(Mod mod)
    {
        if (mod.Sources.Any(source => source.Provider == "nexusmods" && source.ModId == "16292" ||
            source.Provider == "github" && source.ModId.Equals("CowboyBingus/BingusSharedLoader", StringComparison.OrdinalIgnoreCase))) return true;
        var name = string.Concat(mod.Name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return name == "bingussharedloader" || System.Text.RegularExpressions.Regex.IsMatch(name, @"^bingussharedloaderv?\d");
    }
}
