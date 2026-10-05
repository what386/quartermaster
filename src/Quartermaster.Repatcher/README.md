# Quartermaster.Repatcher

Native .NET 10 translation of the unit-repair engine in
[hd2-repatcher](https://github.com/RaidingForPants/hd2-repatcher), based on commit
`2222f6432ee3ead75a906b9a756618e4802a1ad5`. It has no Python or Avalonia runtime
dependency. DSAR blocks use
[K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4).

## Usage

```csharp
using Quartermaster.Repatcher;
using Quartermaster.Repatcher.Archives;

// Indexing performs synchronous disk I/O; run it off the UI thread.
var archives = await Task.Run(() => GameArchives.Open(gameDataDirectory, cancellationToken));
var repatcher = new Repatcher(archives);

var result = await repatcher.RepairFolderAsync(
    importedModDirectory,
    stagingDirectory,
    new Progress<FileRepair>(file => ReportRepair(file)),
    cancellationToken);

if (result.HasErrors)
{
    // Inspect result.Files before deploying any staged files.
}
```

`RepairFileAsync(source, destination)` handles a single patch set. The destination
main file and companions must not exist. Main patches are discovered by the
filename pattern `<16 hex characters>.patch` or `.patch_<decimal slot>`;
`.stream` and `.gpu_resources` are companions, never standalone repair inputs.
Nested directories and their relative paths are preserved by folder repair.

The engine creates ordinary independent files in staging, preserves companion
bytes, and publishes the main file last. It does not change imported originals
or deploy into the game. Folder repair requires staging outside the source tree.
Individual failures are reported and remaining patches are processed;
cancellation propagates. A batch may contain successful staged patches alongside
failures, so callers must decide whether to deploy it. Publication is not a
crash-proof multi-file transaction; abandoned staging can be discarded.

`RepairPatch(ReadOnlyMemory<byte>)` repairs bytes without filesystem writes.
`IUnitResourceSource` allows another installed-resource reader or test fixture
to supply current unit data. Sources must support concurrent reads if the engine
is used concurrently. Input game assets must remain stable during indexing/repair;
reopen the archive index after game updates.

## Implemented behavior

- Read-only indexing of legacy, compressed DSAR, and bundled/slim installations.
- Bounds-checked little-endian patch/type/resource tables with 64-bit IDs/offsets.
- Raw and LZ4 DSAR chunks, including resource reads across chunk and bundle boundaries.
- Units already matching the installed format version are preserved byte for byte,
  including custom LOD tables and mesh references. LOD differences alone do not
  indicate that a mod needs repatching.
- Unit version and LOD replacement from installed game resources when versions differ.
- Upstream's pre-`0xA4CD36` vertex-format upgrade (`format > 16` becomes `format + 4`).
- Adjustment of the sixteen unit offset fields starting at `0x34`.
- Removal of TOC entries for units absent from the installed game's index,
  reported through `RemovedUnits`. Their unreferenced payload bytes are retained,
  matching the reference engine; companions remain unchanged.
- Structured `Updated`, `NoUnits`, `Corrupted`, and batch `Failed` results.

Compared with the Python implementation, the port updates resource data sizes
after LOD replacement, truncates shrinking output correctly, validates the entire
type table, and keeps game/archive state scoped to an instance. Unknown TOC fields,
companion offsets, and unaffected bytes are preserved. Conflicting duplicate
installed units fail indexing instead of choosing an arbitrary copy.
`RepairedUnits` counts units whose bytes actually changed.

## Validation and limits

Run `dotnet test Quartermaster.slnx`. Tests use synthetic assets for all three
archive layouts, compressed/raw chunks, split bundles, malformed inputs,
staging, cancellation, missing units, and LOD growth/shrinkage.

The checked-in JSON golden fixtures are produced by the original Python repair
function. Expected outputs normalize only its stale resource sizes and trailing
bytes after shrinkage. Regenerate them with:

```sh
python3 tests/Quartermaster.Repatcher.Tests/generate_reference.py /path/to/hd2-repatcher
```

The script stubs archive imports and injects synthetic current-unit data, so it
needs no Python LZ4 package. C# tests consume the JSON without running Python.

The engine has been checked against a local slim game installation and the
Impatient Diver example mod; see [local validation results](../../docs/repatcher-validation.md).
These are file-level checks, not an in-game loading test. Impatient Diver contains
no unit resources, so unit repairs were exercised with temporary patches derived
from actual installed unit data.
Unknown compression modes and malformed/overlapping binary layouts are rejected.
Individual buffers are limited to .NET array sizes; full patch files and the slim
bundle index are processed in memory. Archive access decompresses only intersecting
chunks. Recovered Echelon-specific string rebuilding and additional format-upgrade
rules are outside this upstream unit-engine port.

Source and dependency license notices are distributed in `THIRD_PARTY_NOTICES.txt`.
