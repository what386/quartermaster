"""Generate synthetic golden repairs with the pinned, local Python reference.

Python is only needed to regenerate these fixtures, never to build/run C# tests.
The slim module is stubbed because this checks unit repair, not archive decoding.
Two upstream write defects are normalized: stale resource sizes and trailing bytes.
"""
import importlib.util
import json
from pathlib import Path
import struct
import sys
import tempfile
import types

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
UNIT_TYPE = 16187218042980615487
slim = types.ModuleType("slim")
for name in ("slim_init", "is_slim_version", "load_package", "get_package_toc",
             "get_resource_from_bundle", "get_resource_from_package"):
    setattr(slim, name, lambda *args: None)
sys.modules["slim"] = slim
reference = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "examples/hd2-repatcher"
spec = importlib.util.spec_from_file_location(
    "update_unit_mods", reference / "update_unit_mods.py")
engine = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = engine
spec.loader.exec_module(engine)


def put32(data, offset, value):
    struct.pack_into("<I", data, offset, value)


def unit(lod_size, version, fill):
    lod = 0x80
    joint = lod + lod_size
    layouts = joint + 8
    data = bytearray(layouts + 8 + 8 + 16 * 20 + 16)
    for offset, value in ((0x2c, version), (0x30, lod), (0x34, joint),
                          (0x38, joint + 4), (0x5c, layouts),
                          (layouts, 1), (layouts + 4, 8)):
        put32(data, offset, value)
    data[lod:joint] = bytes([fill]) * lod_size
    data[joint:joint+8] = b"\x7b" * 8
    for i in range(16):
        put32(data, layouts + 16 + i * 20, i)
        put32(data, layouts + 20 + i * 20, 17 if i % 2 == 0 else 16)
    data[-16:] = b"\xab" * 16
    return bytes(data)


def patch(items):
    start = 104 + 80 * len(items)
    data = bytearray(start + sum(len(payload) for _, payload in items))
    struct.pack_into("<III", data, 0, 4026531857, 1, len(items))
    struct.pack_into("<QQ", data, 80, UNIT_TYPE, len(items))
    for i, (unit_id, payload) in enumerate(items):
        struct.pack_into("<QQQQQQQIIIIII", data, 104 + i * 80,
                         unit_id, UNIT_TYPE, start, 123, 456, 42, 43,
                         len(payload), 20, 30, 91, 92, i)
        data[start:start+len(payload)] = payload
        start += len(payload)
    return bytes(data)


cases = []
for name, old_size, new_size, version, missing in (
    ("old-layout-equal-lod", 8, 8, 1, False),
    ("modern-layout", 8, 8, 0xA4CD36, False),
    ("lod-growth", 8, 24, 1, False),
    ("lod-shrink", 24, 8, 1, False),
    ("missing-unit", 8, 8, 1, True),
):
    mod = unit(old_size, version, 0xdd)
    game = unit(new_size, 0xA4CD40, 0xee)
    items = [(99, mod), (1, mod)] if missing else [(1, mod)]
    source = patch(items)
    engine.game_resource_mapping = {1: ("unused", 0, len(game))}
    engine.get_data_from_original_file = lambda _: (game[0x2c:0x30], game[0x80:0x80+new_size], new_size)
    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "test.patch_0"
        path.write_bytes(source)
        code, _ = engine.update_patch_file(str(path))
        assert code == engine.UPDATE_SUCCESS
        expected = bytearray(path.read_bytes())
    # Correct only the two documented upstream serialization defects.
    expected = expected[:len(source) - (80 if missing else 0) + new_size - old_size]
    put32(expected, 104 + 56, len(mod) + new_size - old_size)
    cases.append({"name": name, "patch": source.hex(), "units": {"1": game.hex()},
                  "expected": expected.hex()})

destination = Path(__file__).with_name("Fixtures") / "repair-reference.json"
destination.parent.mkdir(exist_ok=True)
destination.write_text(json.dumps({
    "source": "hd2-repatcher commit 2222f6432ee3ead75a906b9a756618e4802a1ad5",
    "normalization": "Updated TOC resource sizes; truncated stale trailing bytes.",
    "cases": cases,
}, indent=2) + "\n")
print(f"Generated {len(cases)} Python reference cases")
