#!/usr/bin/env python3
"""Check that every localization catalog has the same string keys as English."""

import json
from pathlib import Path
import sys


CATALOGS = Path(__file__).resolve().parents[1] / "src/Quartermaster.Gui/Localization/Catalogs"


def load_catalog(path: Path) -> dict:
    try:
        with path.open(encoding="utf-8") as stream:
            catalog = json.load(stream)
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"{path.name}: {error}") from error

    strings = catalog.get("strings") if isinstance(catalog, dict) else None
    if not isinstance(strings, dict) or not all(
        isinstance(key, str) and isinstance(value, str)
        for key, value in strings.items()
    ):
        raise ValueError(f"{path.name}: expected a strings object containing string values")
    return strings


def main() -> int:
    english_path = CATALOGS / "en.json"
    try:
        english = load_catalog(english_path)
    except ValueError as error:
        print(error, file=sys.stderr)
        return 1

    failed = False
    for path in sorted(CATALOGS.glob("*.json")):
        if path == english_path:
            continue
        try:
            strings = load_catalog(path)
        except ValueError as error:
            print(error, file=sys.stderr)
            failed = True
            continue

        missing = sorted(english.keys() - strings.keys())
        extra = sorted(strings.keys() - english.keys())
        if missing or extra:
            failed = True
            print(f"{path.name}: localization keys do not match en.json")
            for key in missing:
                print(f"  missing: {key!r}")
            for key in extra:
                print(f"  extra:   {key!r}")

    if failed:
        return 1
    print("Localization catalogs match en.json.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
