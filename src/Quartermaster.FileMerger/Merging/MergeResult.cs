namespace Quartermaster.FileMerger;

public sealed record MergeConflict(string Target, uint BlockType, int BlockIndex, int Offset, string LosingMod, string WinningMod)
{
    // For structured tables this identifies the record; Offset is then relative to that record.
    // Offset -1 denotes a whole-record addition/removal conflict.
    public string? RecordId { get; init; }
    public string? LosingSource { get; init; }
    public string? WinningSource { get; init; }
}
public sealed record SkippedFileMod(string Target, string Name, string Reason)
{
    public string? Source { get; init; }
    public string? WinningMod { get; init; }
    public string? WinningSource { get; init; }
}
public sealed record MergeResult(string Target, byte[] Content, IReadOnlyList<MergeConflict> Conflicts, IReadOnlyList<SkippedFileMod> Skipped);

