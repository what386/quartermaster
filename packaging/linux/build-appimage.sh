#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
    echo "Usage: $0 PUBLISH_DIRECTORY OUTPUT_DIRECTORY APPIMAGETOOL RUNTIME" >&2
    exit 1
fi
packaging_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
publish_dir="$(cd -- "$1" && pwd)"
mkdir -p "$2"
output_dir="$(cd -- "$2" && pwd)"
tool="$(realpath -- "$3")"
runtime="$(realpath -- "$4")"
[[ -f "$publish_dir/Quartermaster.Gui" && -f "$publish_dir/icon.png" ]] || {
    echo "The Linux publish directory must contain Quartermaster.Gui and icon.png." >&2
    exit 1
}
app_dir="$(mktemp -d "$output_dir/quartermaster-appdir.XXXXXX")"
trap 'rm -rf -- "$app_dir"' EXIT
mkdir -p "$app_dir/usr/lib/quartermaster" "$app_dir/usr/bin" \
    "$app_dir/usr/share/applications" "$app_dir/usr/share/icons/hicolor/512x512/apps"
cp -a "$publish_dir/." "$app_dir/usr/lib/quartermaster/"
install -m 755 "$packaging_dir/AppRun" "$app_dir/AppRun"
chmod +x "$app_dir/usr/lib/quartermaster/Quartermaster.Gui"
ln -s ../lib/quartermaster/Quartermaster.Gui "$app_dir/usr/bin/Quartermaster.Gui"
install -m 644 "$packaging_dir/quartermaster.desktop" "$app_dir/quartermaster.desktop"
install -m 644 "$publish_dir/icon.png" "$app_dir/quartermaster.png"
cp "$app_dir/quartermaster.desktop" "$app_dir/usr/share/applications/"
cp "$app_dir/quartermaster.png" "$app_dir/usr/share/icons/hicolor/512x512/apps/"
ln -s quartermaster.png "$app_dir/.DirIcon"
# Extraction mode avoids requiring FUSE on build hosts and CI runners.
# Remove an earlier control file so a missing generator cannot leave a stale one.
rm -f -- "$output_dir/Quartermaster-linux-x64.AppImage.zsync"
# zsyncmake writes its output in the working directory.
(
    cd -- "$output_dir"
    APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 "$tool" --no-appstream \
        --updateinformation 'gh-releases-zsync|what386|quartermaster|latest|Quartermaster-linux-x64.AppImage.zsync' \
        --runtime-file "$runtime" "$app_dir" "$output_dir/Quartermaster-linux-x64.AppImage"
)
[[ -s "$output_dir/Quartermaster-linux-x64.AppImage.zsync" ]] || {
    echo "AppImage packaging did not generate the .zsync update file." >&2
    exit 1
}
chmod +x "$output_dir/Quartermaster-linux-x64.AppImage"
