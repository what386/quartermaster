namespace Quartermaster.Gui.Shared;

/// <summary>Adds the affected mod at boundaries where lower-level errors lack its identity.</summary>
internal static class ModOperation
{
    private sealed class ModException(string name, Exception cause)
        : InvalidOperationException(Localizer.Interpolate($"Mod '{name}': {cause.Message}"), cause)
    {
        public string ModName { get; } = name;
    }

    public static async Task RunAsync(string name, Func<Task> action)
    {
        await RunAsync(name, async () => { await action(); return true; });
    }

    public static async Task<T> RunAsync<T>(string name, Func<Task<T>> action)
    {
        try { return await action(); }
        catch (OperationCanceledException) { throw; }
        catch (ModException ex) when (ex.ModName == name) { throw; }
        catch (Exception ex) { throw new ModException(name, ex); }
    }
}
