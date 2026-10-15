"""Generate committed unit/culling goldens from an explicit pinned Rust checkout.

Usage: python3 generate_rust_reference.py /path/to/hd2-mod-crates
Development-only: C# tests read JSON and never invoke this script or Rust.
The small harness compiles the upstream unit modules without the desktop stack.
"""
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile

if len(sys.argv) != 2:
    raise SystemExit("Usage: generate_rust_reference.py /path/to/hd2-mod-crates")
reference = Path(sys.argv[1]).resolve()
unit_dir = reference / "crates/hd2-migrator-io/src/unit"
destination = Path(__file__).parent / "Fixtures/repatcher-rust-reference.json"
source_hashes = {'repatch.rs': '36b6b9125ca9b3d266bbbaa9fdd52056abaed26b0b15379b3b639441746f7f63', 'culling.rs': 'f8f1929f300bc00a69afe4578903137509c00509ee127c32433f028e33ea37de'}
for name, expected in source_hashes.items():
    if hashlib.sha256((unit_dir / name).read_bytes()).hexdigest() != expected:
        raise SystemExit(f"{name} does not match the pinned reference revision")

def unit(lod_size, version, fill):
    joint = 0x80 + lod_size
    layouts = joint + 8
    data = bytearray(layouts + 16 + 320 + 16)
    def put(offset, value):
        struct.pack_into("<I", data, offset, value)
    for offset, value in [(0x2c, version), (0x30, 0x80), (0x34, joint),
                          (0x38, joint + 4), (0x5c, layouts), (layouts, 1), (layouts + 4, 8)]:
        put(offset, value)
    data[0x80:joint] = bytes([fill]) * lod_size
    data[joint:joint+8] = b"\x7b" * 8
    for i in range(16):
        put(layouts + 16 + i * 20, i)
        put(layouts + 20 + i * 20, 17 if i % 2 == 0 else 16)
    data[-16:] = b"\xab" * 16
    return list(data)

inputs = [dict(name=name, mod=unit(old, version, 0x11), game=unit(new, 0xA4CD40, 0x22))
          for name, old, new, version in [("legacy-grow", 8, 24, 1), ("legacy-shrink", 24, 8, 1),
                                          ("modern-grow", 8, 24, 0xA4CD36), ("current-custom-lod", 24, 8, 0xA4CD40)]]

harness = r'''
use serde_json::{json, Value};
use std::io::Read;
pub type Result<T> = eyre::Result<T>;
mod archive {
    #[derive(Clone)]
    pub struct TocEntry { pub toc_data: Vec<u8>, pub gpu_data: std::sync::Arc<[u8]>, pub stream_data: std::sync::Arc<[u8]> }
    impl TocEntry { pub fn new(_: u64, _: u64) -> Self { Self { toc_data: vec![], gpu_data: vec![].into(), stream_data: vec![].into() } } }
}
mod unit {
    pub mod repatch;
    pub mod culling;
}
fn bytes(value: &Value) -> Vec<u8> { value.as_array().unwrap().iter().map(|v| v.as_u64().unwrap() as u8).collect() }
fn hex(bytes: &[u8]) -> String { bytes.iter().map(|b| format!("{b:02x}")).collect() }
fn main() -> Result<()> {
    let mut text = String::new(); std::io::stdin().read_to_string(&mut text)?;
    let input: Value = serde_json::from_str(&text)?;
    let mut cases = vec![];
    for case in input.as_array().unwrap() {
        let original = bytes(&case["mod"]); let game = bytes(&case["game"]);
        let mut updated = original.clone();
        unit::repatch::repatch_unit(&mut updated, &unit::repatch::LatestUnitParts::parse(&game)?)?;
        // Equipment-safe updates preserve the mod LOD after version/layout upgrades.
        let mut preserved = updated.clone();
        let start = u32::from_le_bytes(original[0x30..0x34].try_into()?) as usize;
        let end = u32::from_le_bytes(original[0x34..0x38].try_into()?) as usize;
        let next_start = u32::from_le_bytes(preserved[0x30..0x34].try_into()?) as usize;
        let next_end = u32::from_le_bytes(preserved[0x34..0x38].try_into()?) as usize;
        let delta = (end - start) as i64 - (next_end - next_start) as i64;
        for field in (0x34..0x74).step_by(4) {
            let offset = u32::from_le_bytes(preserved[field..field+4].try_into()?);
            if offset != 0 && offset > next_start as u32 {
                preserved[field..field+4].copy_from_slice(&u32::try_from(offset as i64 + delta)?.to_le_bytes());
            }
        }
        preserved.splice(next_start..next_end, original[start..end].iter().copied());
        cases.push(json!({"name": case["name"], "mod": hex(&original), "game": hex(&game),
                         "upstream": hex(&updated), "preserved": hex(&preserved)}));
    }
    println!("{}", json!({"commit":"2cac3768318a692d13b13ade10db6151508bd50f", "units":cases,
                          "culling":unit::culling::generate_fixtures()?}));
    Ok(())
}
'''

with tempfile.TemporaryDirectory(prefix="quartermaster-rust-reference-") as directory:
    work = Path(directory)
    (work / "src/unit").mkdir(parents=True)
    (work / "Cargo.toml").write_text('''[package]
name = "quartermaster-reference-fixtures"
version = "0.0.0"
edition = "2024"
[dependencies]
byteorder = "1.5.0"
eyre = "0.6.12"
serde = { version = "1.0.228", features = ["derive"] }
serde_json = "1.0.149"
''')
    shutil.copyfile(unit_dir / "repatch.rs", work / "src/unit/repatch.rs")
    culling = (unit_dir / "culling.rs").read_text()
    helpers = culling[culling.index("    fn synthetic_entry("):].rsplit("}", 1)[0]
    generator = r'''
const UNIT_ID: u64 = 16187218042980615487;
pub fn generate_fixtures() -> crate::Result<Vec<serde_json::Value>> {
    let mut result = vec![];
    for (name, target_culling, target_hash, bone) in [
        ("replace-and-import", vec![false,true,true], 2, 0),
        ("remove-culling", vec![false], 1, -1),
        ("reuse-transform", vec![false,true], 1, -1)] {
        let mut patch = synthetic_entry(&[false,true], &[1,2,3,4,5,6,7,8]);
        let mut target = synthetic_entry_with_lod(&target_culling, &[9,10,11,12,13,14,15,16], bone);
        for entry in [&mut patch, &mut target] {
            LE::write_u32(&mut entry.toc_data[0x2c..], 0xA4CD40);
            LE::write_u32(&mut entry.toc_data[0x30..], 0x74);
        }
        LE::write_u32(&mut target.toc_data[synthetic_transform_hash_offset()..], target_hash);
        let output = replace_patch_culling_with_target(&patch, &target)?;
        result.push(serde_json::json!({"name":name,
            "patch":crate::hex(&patch.toc_data),"patchGpu":crate::hex(&patch.gpu_data),
            "game":crate::hex(&target.toc_data),"gameGpu":crate::hex(&target.gpu_data),
            "expected":crate::hex(&output.toc_data),"expectedGpu":crate::hex(&output.gpu_data)}));
    }
    Ok(result)
}
'''
    (work / "src/unit/culling.rs").write_text(culling + "\n" + helpers + generator)
    (work / "src/main.rs").write_text(harness)
    result = subprocess.run(["cargo", "run", "--quiet", "--manifest-path", str(work / "Cargo.toml")],
                            input=json.dumps(inputs), text=True, capture_output=True)
    if result.returncode:
        raise SystemExit(result.stderr)
    output = json.loads(result.stdout)
    output["sourceSha256"] = source_hashes
    destination.write_text(json.dumps(output, indent=2) + "\n")
    print(destination)
