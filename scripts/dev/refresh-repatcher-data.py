#!/usr/bin/env python3
"""Refresh bundled repair catalogs using immutable upstream revisions (Python 3)."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import tempfile
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


MIGRATOR = "eigeen/hd2-mod-crates"
INDEX = "Boxofbiscuits97/HD2SDK-CommunityEdition"
DATA = Path(__file__).resolve().parents[2] / "src/Quartermaster.Repatcher/Data"
SUPPLEMENTS = {
    "Armor": {
        "5717aeb23774a749": "BFM-16 Tanker",
        "5ad0c7bf483d382a": "BFM-220 Ironclad",
        "0c206dbe38b89339": "BFM-77 Reformer",
    },
    "Helmet": {
        "5fdb046d58a7c28b": "BFM-16 Tanker",
        "9fcde56b5a3667f2": "BFM-220 Ironclad",
        "16bf8d02fa2801b8": "BFM-77 Reformer",
    },
}
MAX_DOWNLOAD = 16 * 1024 * 1024


def download(url):
    headers = {"User-Agent": "Quartermaster-data-refresh", "Accept": "application/json"}
    # Authentication is optional; send it only to the GitHub API, never raw hosts.
    if url.startswith("https://api.github.com/") and os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    with urlopen(Request(url, headers=headers), timeout=60) as response:
        data = response.read(MAX_DOWNLOAD + 1)
    if len(data) > MAX_DOWNLOAD:
        raise ValueError("Download exceeds the 16 MiB catalog limit")
    return data


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key!r}")
        result[key] = value
    return result


def read_json(raw):
    return json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_object)


def revision(repository, ref):
    if re.fullmatch(r"[0-9a-fA-F]{40}", ref):
        return ref.lower()
    metadata = read_json(download(f"https://api.github.com/repos/{repository}/commits/{quote(ref, safe='')}"))
    sha = metadata.get("sha") if isinstance(metadata, dict) else None
    if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError(f"Could not resolve {repository} revision {ref!r}")
    return sha


def text(value):
    return isinstance(value, str) and bool(value.strip())


def validate_mapping(value, filename):
    if not isinstance(value, dict) or not value:
        raise ValueError(f"{filename}: expected a nonempty equipment mapping")
    for name, parts in value.items():
        if not text(name) or not isinstance(parts, dict) or not parts:
            raise ValueError(f"{filename}: invalid equipment entry {name!r}")
        for role, unit_id in parts.items():
            if not text(role) or type(unit_id) is not int or not 0 < unit_id <= 2**64 - 1:
                raise ValueError(f"{filename}: invalid role or unsigned 64-bit unit ID for {name!r}/{role!r}")
        if filename == "helmet_mappings.json" and "Helmet" not in parts:
            raise ValueError(f"{filename}: missing Helmet role for {name!r}")


def validate_index(value):
    if not isinstance(value, dict):
        raise ValueError("archivehashes.json: expected category/hash/name objects")
    for category in ("Armor", "Helmet"):
        if not isinstance(value.get(category), dict) or not value[category]:
            raise ValueError(f"archivehashes.json: missing or empty {category} category")
    for category, entries in value.items():
        if not text(category) or not isinstance(entries, dict):
            raise ValueError(f"archivehashes.json: invalid category {category!r}")
        seen = set()
        for archive, name in entries.items():
            if not re.fullmatch(r"[0-9a-fA-F]{1,16}", archive) or not text(name):
                raise ValueError(f"archivehashes.json: invalid archive/name in {category!r}")
            normalized = archive.lower().zfill(16)
            if normalized in seen:
                raise ValueError(f"archivehashes.json: duplicate archive hash {archive!r}")
            seen.add(normalized)


def encoded(value):
    return (json.dumps(value, ensure_ascii=False, indent=4) + "\n").encode("utf-8")


def prepare(migrator_ref, index_ref):
    migrator_sha = revision(MIGRATOR, migrator_ref)
    index_sha = revision(INDEX, index_ref)
    sources = [
        ("armor_mappings.merged.json", MIGRATOR, migrator_sha, "assets/armor_mappings.merged.json"),
        ("helmet_mappings.json", MIGRATOR, migrator_sha, "assets/helmet_mappings.json"),
        ("archivehashes.json", INDEX, index_sha, "hashlists/archivehashes.json"),
    ]
    values, provenance = {}, []
    for filename, repository, sha, path in sources:
        url = f"https://raw.githubusercontent.com/{repository}/{sha}/{path}"
        raw = download(url)
        value = read_json(raw)
        if filename == "archivehashes.json":
            validate_index(value)
            # Normalize hashes for ordinal archive lookup and retain reviewed entries
            # absent upstream. Upstream names always take precedence for an existing hash.
            value = {category: {key.lower().zfill(16): name for key, name in entries.items()} for category, entries in value.items()}
            for category, entries in SUPPLEMENTS.items():
                for archive, name in entries.items():
                    value[category].setdefault(archive, name)
        else:
            validate_mapping(value, filename)
        values[filename] = value
        provenance.append((filename, url, hashlib.sha256(raw).hexdigest()))
    for category, filename in (("Armor", "armor_mappings.merged.json"), ("Helmet", "helmet_mappings.json")):
        names = set(values["archivehashes.json"][category].values())
        if not names.intersection(values[filename]):
            raise ValueError(f"{category} index names do not match any bundled mappings")
        unmapped = names.difference(values[filename])
        print(f"{category}: {len(names)} names, {len(unmapped)} without canonical part mappings")
    files = {filename: encoded(value) for filename, value in values.items()}
    notes = ["# Repair data sources", "", "Generated by `scripts/dev/refresh-repatcher-data.py`.", "",
             "Downloads are pinned to the following immutable URLs. SHA-256 values describe the downloaded source bytes.", ""]
    for filename, url, digest in provenance:
        notes += [f"- `{filename}`: [{url}]({url})", f"  - Source SHA-256: `{digest}`",
                  f"  - Bundled SHA-256: `{hashlib.sha256(files[filename]).hexdigest()}`"]
    notes += ["", "Bundled files use UTF-8 JSON with normalized formatting. Archive hashes are lowercase and padded to 16 digits.",
              "The six reviewed BFM armor/helmet entries in the refresh script supplement absent hashes; upstream names take precedence.", ""]
    files["SOURCES.md"] = "\n".join(notes).encode("utf-8")
    return files


def publish(directory, files):
    """Stage the complete validated set; roll back replacements on a write failure."""
    directory.mkdir(parents=True, exist_ok=True)
    originals = {name: (directory / name).read_bytes() if (directory / name).exists() else None for name in files}
    with tempfile.TemporaryDirectory(prefix=".repatcher-data-", dir=directory.parent) as temporary:
        staging = Path(temporary)
        for name, raw in files.items():
            (staging / name).write_bytes(raw)
        replaced = []
        try:
            for name in files:
                os.replace(staging / name, directory / name)
                replaced.append(name)
        except OSError:
            for name in reversed(replaced):
                if originals[name] is None:
                    (directory / name).unlink()
                else:
                    backup = staging / name
                    backup.write_bytes(originals[name])
                    os.replace(backup, directory / name)
            raise


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--migrator-ref", default="HEAD", help="Mapping branch, tag, or commit (default: upstream HEAD)")
    parser.add_argument("--index-ref", default="HEAD", help="Community index branch, tag, or commit (default: upstream HEAD)")
    parser.add_argument("--output-dir", type=Path, default=DATA, help="Destination directory (default: bundled Repatcher Data)")
    parser.add_argument("--check", action="store_true", help="Report differences without writing; exit 1 when updates are available")
    args = parser.parse_args(argv)
    try:
        files = prepare(args.migrator_ref, args.index_ref)
        changed = {name: raw for name, raw in files.items() if not (args.output_dir / name).exists() or (args.output_dir / name).read_bytes() != raw}
        if not changed:
            print("Repatcher data is up to date.")
            return 0
        print(("Would update: " if args.check else "Updating: ") + ", ".join(changed))
        if args.check:
            return 1
        publish(args.output_dir, changed)
        print("Refresh complete. Review the data diff and run the repatcher tests before committing.")
        return 0
    except (OSError, ValueError, HTTPError, URLError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
