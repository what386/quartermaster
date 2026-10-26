# Quartermaster.FileMerger

.NET 10 helpers for detecting, merging, applying, and restoring HELLDIVERS 2
settings-file mods. The mod library uses these helpers through `ProfileFileMods`;
the GUI routes detected file payloads through them during deployment. The engine
never executes a mod's Lua, installer, or executable.

Source is grouped by responsibility:

- `Detection/`: package scanning and detected payload models.
- `Formats/`: LDLD framing, encryption, hashing, and target paths.
- `Merging/`: merge logic, conflict reports, and results.
- `Installation/`: apply plans, backups, journaling, and restoration.

Public types use the `Quartermaster.FileMerger` namespace.

## Detection and variant selection

```csharp
using Quartermaster.FileMerger;

var detection = await new FileModDetector().DetectAsync(zipOrDirectory, cancellationToken);
// Pick the desired candidates before merging. Mods contains every packaged variant,
// including mutually exclusive variants. Source identifies the container/file/resource.
var selected = detection.Mods.Single();
```

Detection accepts a ZIP, directory, individual archive patch, `.stream` bundle, or
`generated_*.dl_bin`. It reads `HD2FM1` payloads from Lua resource bodies or stream
frames and authenticates the sealed config. It reports the shared HD2 file-mod
installer separately in `InstallerFiles`; those scripts are not merge inputs.
An ordinary Lua/model/audio patch produces no settings-mod candidates. Detection
is bounded by `DetectionLimits` and rejects unsafe paths, symlinks, duplicate ZIP
paths, malformed frames, invalid resource ranges, and failed payload fingerprints.
Defaults allow 10,000 files, 256 MiB per file, and 2 GiB total declared input.

The detector does not interpret arbitrary Lua installers, legacy `SBSP1`, custom
executables, or proprietary toggle scripts. A bundle containing several HD2FM1
frames returns several candidates. Manifest option handling and UI selection are
caller responsibilities. Plain config candidates use their filename as the target
and have no package stock-fingerprint list.

## Merge and inspect

```csharp
var merged = ConfigMerger.Merge(
    "data/game/generated_entities.dl_bin", verifiedStockBytes,
    orderedMods, cancellationToken);
```

Inputs are ordered **lowest to highest priority; the last mod wins**. Callers
should convert their loadout priority into this order explicitly. Never use the
filesystem enumeration order as loadout priority.

Each package must recognize the supplied stock ciphertext by its HD2FM1
fingerprint, when present. All decrypted inputs must have the same seeded game
hash as stock. A mismatch fails the plan with the mod's name, source, and target;
the library does not attempt to translate old mods to a different game build.
For raw candidates, the caller must establish trusted stock beforehand: another
editor can preserve that same game hash, so it cannot identify pristine ciphertext.

The merger compares each candidate with stock. Known damage rows, keyed component
tables (recharge, jump pack, sensor, detector, wind-up, and laser designator), and
armor passive arrays are matched by stable record IDs. They can grow or shrink:
independent record additions and edits combine, row counts and hash lookup indexes
are rebuilt, and passive array offsets are recalculated. A deletion conflicts
with an edit to that record; an unchanged copy does not undo another mod's deletion.
Competing additions of the same ID use the highest-priority whole record.

Within records, and in unchanged-size unparsed blocks, complete 32-bit cells are
merged rather than individual bytes. Conflicts identify the final winning/losing
sources. `MergeConflict.RecordId` identifies a structured record, with a
record-relative `Offset` (`-1` for whole-record conflicts); otherwise `Offset` is
payload-relative. These are binary identities and offsets, not named stats.

Block positions may shift when a supported table grows. A single highest-priority
mod can also add or remove unique-type blocks when retained blocks keep their
order and framing has no inter-block gaps; retained payloads still merge against
stock. Ambiguous layouts, multiple structural edits, unknown resizing,
duplicate record IDs, invalid indexes/offsets, or unsafe combined passive arrays
remain exclusive: only the highest-priority candidate for that file is installed,
byte for byte; the others appear in `Skipped`. Other targets merge independently.
See [the developer documentation](../../docs/filemerger.md) for exact layouts and
limits; arbitrary new table formats or whole-block additions are not supported.

The conventional 8–15-byte zero-pad/hash-correction trailer is excluded from mod
comparisons and regenerated after rebuilding. The stock MurmurHash64A is retained
without approximating gameplay values. Framing, supported record structures,
counts, indexes and offsets are checked before and after correction; sealed-box
output is decrypted again for verification. Encryption is randomized, so compare
plaintext or the game hash rather than ciphertext from different encryptions.

`GameConfigCodec` and `ConfigHash` expose the underlying primitives for tooling.
`Encrypt` only encrypts; `EncryptPreservingHash` validates LDLD and adds the hash
correction when needed. Preserving a hash establishes format compatibility, not
in-game correctness of arbitrary edited values.

## Apply and restore

```csharp
// gameDirectory is the installation root containing data/game, not the data folder.
var installer = new FileModInstaller(applicationStateDirectory);
var plan = await installer.PrepareAsync(gameDirectory, orderedMods, cancellationToken);
// Inspect plan.Conflicts and plan.Skipped before deciding whether to apply.
await installer.ApplyAsync(plan, cancellationToken);

// Reapplying rebuilds from verified originals, not from the previous merged file.
// Preparing with fewer/no mods restores targets that are no longer selected.
await installer.RestoreAsync(gameDirectory, cancellationToken);
```

Keep the chosen state directory stable, preferably outside the game installation.
It contains per-installation state, an exclusive operation lock, and SHA-256
addressed `.original` backups. Backups are retained after restore for recovery.
Preparing does not write game files. Apply checks the state revision and all
current file hashes again, validates every output, saves verified originals,
stages every target, journals the operation, and then atomically replaces each
file. An ordinary failure rolls back published files; the journal enables restore
after process interruption. This is recoverable multi-file publication, not a
filesystem-wide transaction or a guarantee against power-loss damage.

Only existing `data/game/generated_<name>.dl_bin` targets can be written. Target
names are normalized to lowercase to avoid aliases on Windows. Unexpected
managed-file contents, corrupt backups, stale plans, and symlinks fail before
publication. Restore never overwrites a newer game file with an old backup.
Caller-selected game and state roots may use directory symlinks or junctions;
they are resolved before deriving installation identities or managed paths.
Links within managed config, journal, and backup paths remain rejected.
After a game update, a new package can adopt the new stock file only if all incoming
packages explicitly recognize its fingerprint. Raw configs cannot authorize that
adoption. An interrupted journal requires restore before a new plan is prepared.

Use only unmodified plans returned by the same installer's `PrepareAsync`.
The caller is responsible for ensuring the game and other file-editing tools are
not running, retaining state/backups, and presenting conflicts/skipped candidates.
No processes are launched, stopped, or contacted by these helpers.

## Dependencies and checks

The merge/detection/filesystem logic is C#. SpaceWizards.Sodium uses bundled
libsodium native assets for authenticated sealed-box encryption on supported
Windows, Linux, and macOS architectures. There is no Rust layer. See
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) for format research and licenses.

Run `dotnet test tests/Quartermaster.FileMerger.Tests`. Tests generate synthetic
LDLD configs, sealed payloads, patches, and ZIPs. They do not require `examples/`,
installed game files, SHODAN, or a game launch. Local example validation is a
separate read-only smoke check; the examples are intentionally uncommitted.

## Extending table support

Add verified layouts to [`Data/config-tables.json`](Data/config-tables.json), which
is embedded at build time. The adjacent schema provides editor validation.
Existing `id-rows`, `entity-keyed`, `recharge`, and `armor-passive` handlers retain their C#
structural checks. New table structures still need a handler. See the
[registry contract and extension workflow](../../docs/filemerger.md#table-registry)
for variants, bounds, metadata, custom registries, and required test coverage.
