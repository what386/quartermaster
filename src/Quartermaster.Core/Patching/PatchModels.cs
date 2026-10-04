namespace Quartermaster.Core.Patching;

public enum PatchFileKind { Main, Stream, GpuResources }
public sealed record PatchFile(string RelativePath, PatchFileKind Kind, long Size, string Sha256);
public readonly record struct ResourceKey(ulong Id, ulong Type);

/// <summary>A patch and its opaque content-source identity; later entries have higher priority.</summary>
public sealed record SelectedPatch(Guid SourceId, Guid PatchSetId, string Archive,
    IReadOnlyList<PatchFile> Files, IReadOnlyList<ResourceKey> Resources)
{
    public string? SelectionHash { get; init; }
}

/// <summary>An ordered selection prepared by a caller. Core does not interpret the selection identity.</summary>
public sealed record DeploymentRequest(Guid SelectionId, IReadOnlyList<SelectedPatch> Patches)
{
    public string? SelectionName { get; init; }
}

public static class PatchValidation
{
    public static bool IsArchive(string archive) => archive.Length == 16 && archive.All(char.IsAsciiHexDigit);
    public static bool IsHash(string hash) => hash.Length == 64 && hash.All(char.IsAsciiHexDigit);
    public static bool IsRelativePath(string path) => !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') &&
        !path.Contains('\\') && !path.Contains(':') && !path.Split('/').Any(p => p is "" or "." or "..") && !path.Contains('\0');

    public static void Validate(DeploymentRequest request)
    {
        if (request.SelectionId == Guid.Empty) throw new ArgumentException("Selection ID must be nonempty.");
        var ids = new HashSet<(Guid Source, Guid Patch)>();
        foreach (var patch in request.Patches)
        {
            if (patch.SourceId == Guid.Empty || patch.PatchSetId == Guid.Empty || !ids.Add((patch.SourceId, patch.PatchSetId)) || !IsArchive(patch.Archive))
                throw new ArgumentException("Invalid or duplicate selected patch.");
            if (patch.SelectionHash is { } selection && !IsHash(selection)) throw new ArgumentException("Invalid source selection hash.");
            ValidateFiles(patch.Files);
        }
    }

    public static void ValidateFiles(IReadOnlyList<PatchFile> files)
    {
        if (files.Count(f => f.Kind == PatchFileKind.Main) != 1 || files.Select(f => f.Kind).Distinct().Count() != files.Count)
            throw new ArgumentException("Patch set must contain one main file and unique companions.");
        foreach (var file in files)
            if (!Enum.IsDefined(file.Kind) || !IsRelativePath(file.RelativePath) || file.Size < 0 || !IsHash(file.Sha256))
                throw new ArgumentException("Invalid patch-file metadata.");
    }
}
