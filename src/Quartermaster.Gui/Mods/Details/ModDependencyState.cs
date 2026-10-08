using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui.Mods;

internal enum DependencyAvailability { Manual, Missing, Installed, Disabled, OutsideProfile }

internal static class ModDependencyState
{
    private static DependencyAvailability Availability(ModDependency dependency, LibraryState state, Guid? profileId)
    {
        if (!dependency.CanInstall) return DependencyAvailability.Manual;
        var installed = state.Mods.Where(mod => !mod.Superseded && ModDependencyMatching.Matches(mod, dependency)).ToArray();
        if (installed.Length == 0) return DependencyAvailability.Missing;
        if (profileId is null) return DependencyAvailability.Installed;
        var entries = state.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Entries ?? [];
        var matching = entries.Where(entry => installed.Any(mod => mod.Id == entry.ModId)).ToArray();
        return matching.Any(entry => entry.Enabled) ? DependencyAvailability.Installed :
            matching.Length > 0 ? DependencyAvailability.Disabled : DependencyAvailability.OutsideProfile;
    }

    public static string Status(ModDependency dependency, LibraryState state, Guid? profileId) => Availability(dependency, state, profileId) switch
    {
        DependencyAvailability.Manual => Localizer.Text("Manual installation"),
        DependencyAvailability.Missing => Localizer.Text("Missing"),
        DependencyAvailability.Installed => Localizer.Text("Installed"),
        DependencyAvailability.Disabled => Localizer.Text("Disabled"),
        _ => Localizer.Text("Not in this profile")
    };

    public static int MissingCount(Mod mod, LibraryState state, Guid? profileId) =>
        mod.Dependencies.Count(dependency => dependency.CanInstall && Availability(dependency, state, profileId) != DependencyAvailability.Installed);

    public static string ActionLabel(Mod mod, LibraryState state, Guid? profileId) => profileId is null
        ? Localizer.Interpolate($"Install missing dependencies ({MissingCount(mod, state, profileId)})")
        : Localizer.Interpolate($"Add dependencies to profile ({MissingCount(mod, state, profileId)})");
}
