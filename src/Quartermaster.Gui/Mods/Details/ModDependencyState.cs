using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui.Mods;

internal static class ModDependencyState
{
    public static string Status(ModDependency dependency, LibraryState state, Guid? profileId)
    {
        if (!dependency.CanInstall) return "Manual installation";
        var installed = state.Mods.Where(mod => !mod.Superseded && ModDependencyMatching.Matches(mod, dependency)).ToArray();
        if (installed.Length == 0) return "Missing";
        if (profileId is null) return "Installed";
        var entries = state.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Entries ?? [];
        var matching = entries.Where(entry => installed.Any(mod => mod.Id == entry.ModId)).ToArray();
        return matching.Any(entry => entry.Enabled) ? "Installed" : matching.Length > 0 ? "Disabled" : "Not in this profile";
    }

    public static int MissingCount(Mod mod, LibraryState state, Guid? profileId) =>
        mod.Dependencies.Count(dependency => dependency.CanInstall && Status(dependency, state, profileId) != "Installed");

    public static string ActionLabel(Mod mod, LibraryState state, Guid? profileId) =>
        $"{(profileId is null ? "Install missing dependencies" : "Add dependencies to profile")} ({MissingCount(mod, state, profileId)})";
}
