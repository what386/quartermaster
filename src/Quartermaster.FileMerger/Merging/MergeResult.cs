namespace Quartermaster.FileMerger;

public sealed record MergeConflict(string Target, uint BlockType, int BlockIndex, int Offset, string LosingMod, string WinningMod);
public sealed record SkippedFileMod(string Target, string Name, string Reason);
public sealed record MergeResult(string Target, byte[] Content, IReadOnlyList<MergeConflict> Conflicts, IReadOnlyList<SkippedFileMod> Skipped);

