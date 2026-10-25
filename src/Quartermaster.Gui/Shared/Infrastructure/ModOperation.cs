namespace Quartermaster.Gui.Shared;

/// <summary>Adds the affected mod at boundaries where lower-level errors lack its identity.</summary>
internal static class ModOperation
{
    private sealed class ModException(string name, Exception cause)
        : InvalidOperationException(Localizer.Interpolate($"Mod '{name}': {cause.Message}"), cause)
    {
        public string ModName { get; } = name;
    }

    public static async Task RunAsync(string name, Func<Task> action, Guid? modId = null)
    {
        await RunAsync(name, async () => { await action(); return true; }, modId);
    }

    public static async Task<T> RunAsync<T>(string name, Func<Task<T>> action, Guid? modId = null)
    {
        try { return await action(); }
        catch (OperationCanceledException) { throw; }
        catch (ModException ex) when (ex.ModName == name) { throw; }
        catch (Exception ex)
        {
            var failure = new ModException(name, ex);
            if (modId is { } id) failure.Data["Quartermaster.ModId"] = id;
            throw failure;
        }
    }
}
