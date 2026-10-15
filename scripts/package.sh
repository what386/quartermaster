#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

usage() {
    cat <<'EOF'
Usage: scripts/package.sh [OPTIONS]

Build local release artifacts using the settings in .github/workflows/release.yml.
Defaults to the host platform. Outputs ZIPs, AppImages, installers, and SHA256SUMS.txt in dist/;
unpacked applications and build tools are kept under dist/package/.

Options:
  -r, --runtime RID          linux-x64, win-x64, or all
  --updater-toolchain FILE   CMake toolchain for cross-building the Windows updater
  --inno-compiler FILE       Existing ISCC.exe (otherwise install the pinned CI version)
  --skip-installer           Skip the Windows installer (required for Windows on Linux)
  -h, --help                Show this help

Requirements: .NET 10 SDK, Python 3; Linux AppImages also need curl and sha256sum.
Pinned AppImage tools are downloaded automatically; no FUSE setup is required to build.
Windows packages also need CMake and a C++
compiler. Windows installer builds require Windows and PowerShell 7 (pwsh).
Run from Git Bash on Windows, with the Visual Studio C++ build tools installed.
This script builds artifacts locally; it does not create tags or upload releases.
EOF
}

error() {
    echo "error: $*" >&2
    exit 1
}
require() { command -v "$1" >/dev/null 2>&1 || error "$1 was not found in PATH"; }

case "$(uname -s)" in
Linux*)
    host=linux
    runtime=linux-x64
    ;;
MINGW* | MSYS* | CYGWIN*)
    host=windows
    runtime=win-x64
    ;;
*)
    host=other
    runtime=all
    ;;
esac
toolchain=""
inno_compiler=""
skip_installer=false
while [[ $# -gt 0 ]]; do
    case "$1" in
    -r | --runtime | --updater-toolchain | --inno-compiler)
        [[ $# -ge 2 && -n "$2" ]] || error "$1 requires a value"
        case "$1" in
        -r | --runtime) runtime="$2" ;;
        --updater-toolchain) toolchain="$2" ;;
        --inno-compiler) inno_compiler="$2" ;;
        esac
        shift 2
        ;;
    --skip-installer)
        skip_installer=true
        shift
        ;;
    -h | --help)
        usage
        exit 0
        ;;
    *) error "unknown option '$1'" ;;
    esac
done
case "$runtime" in
all) runtimes=(linux-x64 win-x64) ;;
linux-x64 | win-x64) runtimes=("$runtime") ;;
*) error "unsupported runtime '$runtime'" ;;
esac
require dotnet
if command -v python3 >/dev/null 2>&1; then python=python3; else
    require python
    python=python
fi
for rid in "${runtimes[@]}"; do
    if [[ "$rid" == linux-x64 ]]; then
        [[ "$host" == linux ]] || error "Linux AppImage packaging requires a Linux host"
        require curl
        require sha256sum
    fi
    if [[ "$rid" == win-x64 ]]; then
        require cmake
        if [[ "$host" != windows ]]; then
            [[ -f "$toolchain" ]] || error "Windows cross-builds require --updater-toolchain FILE"
            [[ "$skip_installer" == true ]] || error "Windows installers require Windows; use --skip-installer when cross-building"
        fi
        if [[ "$skip_installer" == false ]]; then require pwsh; fi
    fi
done
if [[ -n "$toolchain" ]]; then
    [[ -f "$toolchain" ]] || error "toolchain not found: $toolchain"
    toolchain="$(cd -- "$(dirname -- "$toolchain")" && pwd)/$(basename -- "$toolchain")"
fi

project=src/Quartermaster.Gui/Quartermaster.Gui.csproj
work_root="$repo_root/dist/package"
mkdir -p "$work_root"
artifacts=()
for rid in "${runtimes[@]}"; do
    publish_dir="$work_root/$rid/publish"
    archive="$repo_root/dist/Quartermaster-$rid-bundled.zip"
    echo "==> Publishing $rid"
    # Start with a fresh payload so obsolete files never enter the release ZIP.
    "$python" - "$publish_dir" "$archive" <<'PY'
import shutil
import sys
from pathlib import Path
publish, archive = map(Path, sys.argv[1:])
if publish.exists():
    shutil.rmtree(publish)
publish.mkdir(parents=True)
archive.unlink(missing_ok=True)
PY
    publish_properties=(
        -p:SelfContained=true
        -p:PublishSingleFile=true
        -p:PublishReadyToRun=true
        -p:IncludeNativeLibrariesForSelfExtract=true
        "-p:NuGetLockFilePath=obj/package-$rid.packages.lock.json"
    )
    dotnet restore "$project" --runtime "$rid" -m:1 "${publish_properties[@]}"
    dotnet publish "$project" --configuration Release --runtime "$rid" \
        --self-contained true --no-restore --output "$publish_dir" \
        "${publish_properties[@]}" -m:1 -p:UseSharedCompilation=false \
        -p:DebugType=None -p:DebugSymbols=false

    if [[ "$rid" == win-x64 ]]; then
        echo "==> Building Windows updater"
        updater_build="$work_root/win-x64/updater-build"
        configure=(cmake -S winupdater -B "$updater_build"
            -DCMAKE_BUILD_TYPE=MinSizeRel -DBUILD_TESTING=OFF)
        if [[ -n "$toolchain" ]]; then
            configure+=("-DCMAKE_TOOLCHAIN_FILE=$toolchain")
        else
            configure+=(-A x64)
        fi
        "${configure[@]}"
        cmake --build "$updater_build" --config MinSizeRel
        cmake --install "$updater_build" --config MinSizeRel --prefix "$publish_dir"
        [[ -f "$publish_dir/winupdater/Quartermaster.Updater.exe" ]] || error "Windows updater is missing from the publish directory"
    fi
    [[ -f "$publish_dir/icon.png" ]] || error "icon.png is missing from the publish directory"

    echo "==> Archiving $rid"
    "$python" - "$publish_dir" "$archive" <<'PY'
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile
publish, destination = map(Path, sys.argv[1:])
with ZipFile(destination, 'w', compression=ZIP_DEFLATED) as archive:
    for file in sorted(publish.rglob('*')):
        if file.is_file():
            archive.write(file, file.relative_to(publish))
PY
    artifacts+=("$archive")

    if [[ "$rid" == linux-x64 ]]; then
        echo "==> Building Linux AppImage"
        appimage_tool="$(bash packaging/linux/install-appimagetool.sh "$work_root/appimage-tools")"
        bash packaging/linux/build-appimage.sh "$publish_dir" "$repo_root/dist" \
            "$appimage_tool" "$work_root/appimage-tools/runtime-x86_64"
        artifacts+=("$repo_root/dist/Quartermaster-linux-x64.AppImage")
    fi

    if [[ "$rid" == win-x64 && "$skip_installer" == false ]]; then
        echo "==> Building Windows installer"
        if [[ -z "$inno_compiler" ]]; then
            inno_compiler="$(pwsh -NoProfile -File packaging/windows/install-inno.ps1 \
                -Destination "$work_root/inno-setup")"
            inno_compiler="${inno_compiler//$'\r'/}"
        fi
        version="$(dotnet msbuild "$project" -getProperty:Version -nologo)"
        version="${version//$'\r'/}"
        pwsh -NoProfile -File packaging/windows/build-installer.ps1 \
            -Version "$version" -PublishDirectory "$publish_dir" \
            -OutputDirectory "$repo_root/dist" -CompilerPath "$inno_compiler"
        artifacts+=("$repo_root/dist/Quartermaster-win-x64-setup.exe")
    fi
done

"$python" - "$repo_root/dist/SHA256SUMS.txt" "${artifacts[@]}" <<'PY'
import hashlib
import sys
from pathlib import Path
with Path(sys.argv[1]).open('w', encoding='utf-8', newline='\n') as checksums:
    for name in sys.argv[2:]:
        artifact = Path(name)
        with artifact.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        checksums.write(f'{digest}  {artifact.name}\n')
PY
printf 'Created: %s\n' "${artifacts[@]}" "$repo_root/dist/SHA256SUMS.txt"
