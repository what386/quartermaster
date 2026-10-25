namespace Quartermaster.FileMerger;

/// <summary>One variant of one settings-file mod. Content is the sealed game file, not installer code.</summary>
public sealed record FileMod(string Name, string Target, ReadOnlyMemory<byte> Content, string Source,
    IReadOnlyList<string> StockFingerprints, string? Version = null);

public sealed record FileModDetection(IReadOnlyList<FileMod> Mods, IReadOnlyList<string> InstallerFiles);
