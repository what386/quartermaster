#!/usr/bin/env bash

set -euo pipefail

# ============================================================================
# .NET cross-platform build / publish script
#
# Project-specific configuration
# ============================================================================

PROJECT_NAME="Quartermaster"

# ============================================================================
# Generic configuration
# ============================================================================

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

PROJECT="$REPO_ROOT/src/Quartermaster.Gui/Quartermaster.Gui.csproj"
DIST_ROOT="$REPO_ROOT/dist/$PROJECT_NAME"

CONFIGURATION="Release"

SELF_CONTAINED=false
SINGLE_FILE=false
READY_TO_RUN=false
TRIM=false

RUNTIME="all"
CLEAN=true

# Supported release targets.
RUNTIMES=(
    "linux-x64"
    "win-x64"
)

# ============================================================================
# Helpers
# ============================================================================

usage() {
    cat <<EOF
Usage:
  $(basename "$0") [OPTIONS]

Build and publish $PROJECT_NAME for one or more platforms.

Options:

  -c, --configuration NAME
      Build configuration.

      Default: Release

  -r, --runtime RUNTIME
      Runtime to publish for.

      Available:
        linux-x64
        win-x64
        all

      Default: all

  -s, --self-contained
      Produce self-contained applications.

      The target machine does not need the .NET runtime installed.

  -f, --framework-dependent
      Produce framework-dependent applications.

      The target machine must have the appropriate .NET runtime installed.

      This is the default.

  --single-file
      Bundle the application into a single executable.

      This does not imply self-contained publishing.

  --ready-to-run
      Precompile eligible .NET assemblies using ReadyToRun.

      This can improve startup time at the cost of larger artifacts and
      longer publish times.

  -t, --trim
      Enable .NET assembly trimming.

      This can significantly reduce application size but may break
      applications that depend on reflection or dynamic assembly loading.

  --no-clean
      Don't remove existing distribution output before building.

  -h, --help
      Show this help.

Examples:

  # Build all platforms, framework-dependent
  $(basename "$0")

  # Build all platforms, self-contained
  $(basename "$0") --self-contained

  # Build all platforms as single-file self-contained executables
  $(basename "$0") --self-contained --single-file

  # Build all platforms as self-contained ReadyToRun executables
  $(basename "$0") --self-contained --ready-to-run

  # Recommended portable distribution build
  $(basename "$0") --self-contained --single-file --ready-to-run

  # Build everything with trimming
  $(basename "$0") --self-contained --single-file --ready-to-run --trim

  # Build only Linux x64
  $(basename "$0") --runtime linux-x64

  # Build Windows x64
  $(basename "$0") --runtime win-x64 --self-contained

  # Build Debug
  $(basename "$0") --configuration Debug

Output:

  dist/$PROJECT_NAME/<configuration>/<runtime>/

For example:

  dist/$PROJECT_NAME/Release/linux-x64/
  dist/$PROJECT_NAME/Release/win-x64/
EOF
}

error() {
    echo "error: $*" >&2
    exit 1
}

# ============================================================================
# Parse arguments
# ============================================================================

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration)
            [[ $# -ge 2 ]] || error "$1 requires a value"
            CONFIGURATION="$2"
            shift 2
            ;;

        -r|--runtime)
            [[ $# -ge 2 ]] || error "$1 requires a value"
            RUNTIME="$2"
            shift 2
            ;;

        -s|--self-contained)
            SELF_CONTAINED=true
            shift
            ;;

        -f|--framework-dependent)
            SELF_CONTAINED=false
            shift
            ;;

        --single-file)
            SINGLE_FILE=true
            shift
            ;;

        --ready-to-run)
            READY_TO_RUN=true
            shift
            ;;

        -t|--trim)
            TRIM=true
            shift
            ;;

        --no-clean)
            CLEAN=false
            shift
            ;;

        -h|--help)
            usage
            exit 0
            ;;

        *)
            error "unknown option '$1'"
            ;;
    esac
done

# ============================================================================
# Validate environment
# ============================================================================

command -v dotnet >/dev/null 2>&1 || \
    error "dotnet was not found in PATH"

[[ -f "$PROJECT" ]] || \
    error "project not found: $PROJECT"

# ============================================================================
# Resolve runtimes
# ============================================================================

if [[ "$RUNTIME" == "all" ]]; then
    SELECTED_RUNTIMES=("${RUNTIMES[@]}")
else
    SELECTED_RUNTIMES=()

    for candidate in "${RUNTIMES[@]}"; do
        if [[ "$candidate" == "$RUNTIME" ]]; then
            SELECTED_RUNTIMES+=("$candidate")
            break
        fi
    done

    if [[ "${#SELECTED_RUNTIMES[@]}" -eq 0 ]]; then
        error "unsupported runtime '$RUNTIME'

Supported runtimes:
$(printf '  %s\n' "${RUNTIMES[@]}")"
    fi
fi

# ============================================================================
# Get .NET version
# ============================================================================

DOTNET_VERSION="$(dotnet --version)"

# ============================================================================
# Display configuration
# ============================================================================

echo
echo "========================================"
echo ".NET Build"
echo "========================================"
echo
echo "Project:        $PROJECT_NAME"
echo "Project file:   $PROJECT"
echo "Configuration:  $CONFIGURATION"
echo "Self-contained: $SELF_CONTAINED"
echo "Single-file:    $SINGLE_FILE"
echo "ReadyToRun:     $READY_TO_RUN"
echo "Trimmed:        $TRIM"
echo "Runtimes:       ${SELECTED_RUNTIMES[*]}"
echo ".NET SDK:       $DOTNET_VERSION"
echo

# ============================================================================
# Clean
# ============================================================================

OUTPUT_ROOT="$DIST_ROOT/$CONFIGURATION"

if [[ "$CLEAN" == true ]]; then
    echo "==> Cleaning $OUTPUT_ROOT"
    rm -rf "$OUTPUT_ROOT"
fi

mkdir -p "$OUTPUT_ROOT"

# ============================================================================
# Publish
# ============================================================================

FAILED=()
SUCCEEDED=()

for runtime in "${SELECTED_RUNTIMES[@]}"; do
    OUTPUT="$OUTPUT_ROOT/$runtime"

    echo
    echo "========================================"
    echo "Publishing: $runtime"
    echo "========================================"

    mkdir -p "$OUTPUT"

    PUBLISH_ARGS=(
        publish
        "$PROJECT"
        --configuration "$CONFIGURATION"
        --runtime "$runtime"
        --self-contained "$SELF_CONTAINED"
        --output "$OUTPUT"
        --property:PublishSingleFile="$SINGLE_FILE"
        --property:PublishReadyToRun="$READY_TO_RUN"
        --property:PublishTrimmed="$TRIM"
    )

    if dotnet "${PUBLISH_ARGS[@]}"; then
        SUCCEEDED+=("$runtime")
        echo
        echo "==> $runtime succeeded"
    else
        FAILED+=("$runtime")
        echo
        echo "==> $runtime FAILED" >&2
    fi
done

# ============================================================================
# Build metadata
# ============================================================================

METADATA="$OUTPUT_ROOT/build-info.txt"

{
    echo "Project: $PROJECT_NAME"
    echo "Project file: $PROJECT"
    echo "Configuration: $CONFIGURATION"
    echo "Self-contained: $SELF_CONTAINED"
    echo "Single-file: $SINGLE_FILE"
    echo "ReadyToRun: $READY_TO_RUN"
    echo "Trimmed: $TRIM"
    echo ".NET SDK: $DOTNET_VERSION"
    echo "Built: $(date --iso-8601=seconds 2>/dev/null || date)"
    echo
    echo "Successful runtimes:"
    printf '  %s\n' "${SUCCEEDED[@]}"

    if [[ "${#FAILED[@]}" -gt 0 ]]; then
        echo
        echo "Failed runtimes:"
        printf '  %s\n' "${FAILED[@]}"
    fi
} > "$METADATA"

# ============================================================================
# Summary
# ============================================================================

echo
echo "========================================"
echo "Build Summary"
echo "========================================"
echo

if [[ "${#SUCCEEDED[@]}" -gt 0 ]]; then
    echo "Succeeded:"
    printf '  â %s\n' "${SUCCEEDED[@]}"
fi

if [[ "${#FAILED[@]}" -gt 0 ]]; then
    echo
    echo "Failed:"
    printf '  â %s\n' "${FAILED[@]}"
fi

echo
echo "Output:"
echo "  $OUTPUT_ROOT"
echo
echo "Metadata:"
echo "  $METADATA"
echo

if [[ "${#FAILED[@]}" -gt 0 ]]; then
    exit 1
fi
