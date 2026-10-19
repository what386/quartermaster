#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 1 ]] || {
    echo "Usage: $0 DESTINATION" >&2
    exit 1
}
mkdir -p "$1"
destination="$(cd -- "$1" && pwd)"

download() {
    local name="$1" url="$2" checksum="$3"
    if [[ -f "$destination/$name" ]] && printf '%s  %s\n' "$checksum" "$destination/$name" | sha256sum --check --status; then
        return
    fi
    local temporary
    temporary="$(mktemp "$destination/download.XXXXXX")"
    if ! curl --fail --location --retry 3 --output "$temporary" "$url" ||
        ! printf '%s  %s\n' "$checksum" "$temporary" | sha256sum --check --status; then
        rm -f -- "$temporary"
        echo "Could not download and verify $name." >&2
        return 1
    fi
    mv -- "$temporary" "$destination/$name"
}

# Official release assets, pinned by version and SHA-256 (GitHub release digests).
download appimagetool-x86_64.AppImage \
    https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage \
    ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
download runtime-x86_64 \
    https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64 \
    2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
chmod +x "$destination/appimagetool-x86_64.AppImage"
printf '%s\n' "$destination/appimagetool-x86_64.AppImage"
