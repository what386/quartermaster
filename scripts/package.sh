#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

usage() {
    cat <<'EOF'
Usage: scripts/package.sh [OPTIONS]

Build local release artifacts using the settings in .github/workflows/release.yml.
Defaults to the host platform. Outputs ZIPs, AppImages with .zsync files, installers, and SHA256SUMS.txt in dist/;
unpacked applications and build tools are kept under dist/package/.

Options:
  -r, --runtime RID          linux-x64, win-x64, or all
  -f, --format FORMAT        all (default), zip, appimage, or setup
  --updater-toolchain FILE   CMake toolchain for cross-building the Windows updater
  --inno-compiler FILE       Existing ISCC.exe (otherwise install the pinned CI version)
  --skip-installer           Skip Windows setup when building --format all
  -h, --help                Show this help

Requirements: .NET 10 SDK, Python 3; Linux AppImages also need curl and sha256sum.
Pinned AppImage tools are downloaded automatically; no FUSE setup is required to build.
Windows packages also need CMake and a C++
compiler. Windows installer builds require Windows and PowerShell 7 (pwsh).
Run from Git Bash on Windows, with the Visual Studio C++ build tools installed.
This script builds artifacts locally; it does not create tags or upload releases.

Examples:
  scripts/package.sh --format appimage        Linux AppImage only (on Linux)
  scripts/package.sh --format setup           Windows setup only (on Windows)
  scripts/package.sh --format zip             Host platform's portable ZIP only
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
format=all
runtime_explicit=false
while [[ $# -gt 0 ]]; do
    case "$1" in
    -r | --runtime | -f | --format | --updater-toolchain | --inno-compiler)
        [[ $# -ge 2 && -n "$2" ]] || error "$1 requires a value"
        case "$1" in
        -r | --runtime) runtime="$2"; runtime_explicit=true ;;
        -f | --format) format="$2" ;;
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
case "$format" in
all | zip) ;;
appimage)
    if [[ "$runtime_explicit" == false ]]; then runtime=linux-x64; fi
    [[ "$runtime" == linux-x64 ]] || error "--format appimage requires --runtime linux-x64"
    ;;
setup)
    if [[ "$runtime_explicit" == false ]]; then runtime=win-x64; fi
    [[ "$runtime" == win-x64 ]] || error "--format setup requires --runtime win-x64"
    [[ "$skip_installer" == false ]] || error "--format setup conflicts with --skip-installer"
    ;;
*) error "unsupported format '$format' (expected all, zip, appimage, or setup)" ;;
esac

select_formats() {
    build_zip=false
    build_appimage=false
    build_setup=false
    if [[ "$format" == all || "$format" == zip ]]; then build_zip=true; fi
    if [[ "$1" == linux-x64 && ("$format" == all || "$format" == appimage) ]]; then build_appimage=true; fi
    if [[ "$1" == win-x64 && ("$format" == all || "$format" == setup) && "$skip_installer" == false ]]; then build_setup=true; fi
}

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
    select_formats "$rid"
    if [[ "$build_appimage" == true ]]; then
        [[ "$host" == linux ]] || error "Linux AppImage packaging requires a Linux host"
        require curl
        require sha256sum
    fi
    if [[ "$rid" == win-x64 ]]; then
        if [[ "$build_setup" == true && "$host" != windows ]]; then
            error "Windows setup packages require Windows and PowerShell 7; use --format zip when cross-building"
        fi
        require cmake
        if [[ "$host" != windows ]]; then
            [[ -f "$toolchain" ]] || error "Windows cross-builds require --updater-toolchain FILE"
        fi
        if [[ "$build_setup" == true ]]; then require pwsh; fi
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
    select_formats "$rid"
    publish_dir="$work_root/$rid/publish"
    archive="$repo_root/dist/Quartermaster-$rid-bundled.zip"
    echo "==> Publishing $rid"
    # Start with a fresh payload so obsolete files never enter release packages.
    "$python" - "$publish_dir" <<'PY'
import shutil
import sys
from pathlib import Path
publish = Path(sys.argv[1])
if publish.exists():
    shutil.rmtree(publish)
publish.mkdir(parents=True)
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

    if [[ "$build_zip" == true ]]; then
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
    fi

    if [[ "$build_appimage" == true ]]; then
        echo "==> Building Linux AppImage"
        appimage_tool="$(bash packaging/linux/install-appimagetool.sh "$work_root/appimage-tools")"
        bash packaging/linux/build-appimage.sh "$publish_dir" "$repo_root/dist" \
            "$appimage_tool" "$work_root/appimage-tools/runtime-x86_64"
        artifacts+=("$repo_root/dist/Quartermaster-linux-x64.AppImage"
            "$repo_root/dist/Quartermaster-linux-x64.AppImage.zsync")
    fi

    if [[ "$build_setup" == true ]]; then
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
