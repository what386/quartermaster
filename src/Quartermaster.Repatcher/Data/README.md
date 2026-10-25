# Bundled repair data

These three JSON assets come from `eigeen/hd2-mod-crates` commit
`2cac3768318a692d13b13ade10db6151508bd50f`:

- `armor_mappings.merged.json`: canonical armor unit IDs and logical part roles.
- `helmet_mappings.json`: canonical helmet unit IDs.
- `archivehashes.json`: category, archive hash, and equipment name index.

The index also includes the six BFM-16 Tanker, BFM-220 Ironclad, and BFM-77 Reformer
armor/helmet archive entries supplied by the EXP73.4 reference setup script. The
rest of that script's mutable community-index download is deliberately not used.
No catalogs are downloaded by the shipped application.

The upstream README credits armor/helmet mappings to @大紫 and the supplied
reference credits the archive-name index to HD2SDK Community Edition. See
`../THIRD_PARTY_NOTICES.txt` for source and license attribution.

## Refreshing

From the repository root, using Python 3:

```sh
python3 scripts/dev/refresh-repatcher-data.py --check
python3 scripts/dev/refresh-repatcher-data.py
# Or with just:
just refresh-repatcher-data
```

The script fetches the latest armor/helmet mappings from `eigeen/hd2-mod-crates`
and the latest archive-name index from `Boxofbiscuits97/HD2SDK-CommunityEdition`.
It resolves each source to a commit before downloading files, validates all three
catalogs before replacing any, and records immutable source URLs and source/bundled
SHA-256 hashes in `SOURCES.md`. That file describes subsequent refreshes; the
baseline above describes the initial bundle.

Valid archive hashes are normalized to lowercase with leading zeros to reach 16
digits. Duplicate hashes after normalization are rejected.

The six BFM supplements are retained only for hashes absent upstream. An upstream
name for an existing hash takes precedence. Unmapped new equipment is allowed;
the script reports mapping coverage and rejects catalogs with no matching equipment
names in either required category.

Use `--migrator-ref COMMIT` and `--index-ref COMMIT` to reproduce specific revisions.
Both options also accept branches and tags. `--output-dir DIRECTORY` allows a
separate preview destination. `--check` writes nothing and exits 1 when files or
provenance differ, 0 when current, and 2 on an error. Set `GITHUB_TOKEN` optionally
for authenticated GitHub API requests if unauthenticated rate limits are reached.

Review the resulting diff and run `dotnet test tests/Quartermaster.Repatcher.Tests`
before committing refreshed assets. The refresh script's checks can be run with
`python3 -m unittest discover -s scripts/dev`.

New or unclassified equipment retains its own LOD data; unfamiliar units are
never eligible for current-game arm culling solely from a guessed role.
