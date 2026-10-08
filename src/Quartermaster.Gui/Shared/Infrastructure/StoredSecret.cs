namespace Quartermaster.Gui.Shared;

internal static class StoredSecret
{
    public const string Mask = "***************************";

    public static bool HasReplacement(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim() != Mask;
}
