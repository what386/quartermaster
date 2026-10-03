using Quartermaster.Core.Patching;
using System.Text.RegularExpressions;

namespace Quartermaster.Core.Patching;

public static partial class PatchNames
{
    public static bool TryParse(string name, out string archive, out int slot, out PatchFileKind kind)
    {
        var match = Pattern().Match(name);
        archive = ""; slot = 0; kind = PatchFileKind.Main;
        if (!match.Success || (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, out slot))) return false;
        archive = match.Groups[1].Value.ToLowerInvariant();
        kind = match.Groups[3].Value.ToLowerInvariant() switch
        { ".stream" => PatchFileKind.Stream, ".gpu_resources" => PatchFileKind.GpuResources, _ => PatchFileKind.Main };
        return true;
    }
    [GeneratedRegex(@"^([0-9a-f]{16})\.patch(?:_([0-9]+))?(\.stream|\.gpu_resources)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

public static class PatchFiles
{
    public static string Suffix(PatchFileKind kind) => kind switch
    {
        PatchFileKind.Main => "",
        PatchFileKind.Stream => ".stream",
        PatchFileKind.GpuResources => ".gpu_resources",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static string Name(string archive, int slot, PatchFileKind kind) => $"{archive}.patch_{slot}{Suffix(kind)}";
}
