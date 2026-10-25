#!/usr/bin/env bash
set -euo pipefail

icon_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../src/Quartermaster.Gui/Assets" && pwd)"

# icon.svg is the canonical source; alternate designs are kept separately.
magick -background none "$icon_root/icon.svg" -strip "PNG32:$icon_root/icon.png"
magick "$icon_root/icon.png" -define icon:auto-resize=256,128,64,48,32,24,16 "$icon_root/icon.ico"
