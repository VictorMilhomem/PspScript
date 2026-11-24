#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${PSPSCRIPT_INSTALL_DIR:-$HOME/.local/share/pspscript}"
BIN_DIR="${PSPSCRIPT_BIN_DIR:-$HOME/.local/bin}"
RID="${PSPSCRIPT_RID:-linux-x64}"

DO_PULL=false
DO_TEST=false

usage() {
    cat <<EOF
Usage: ./update.sh [options]

Rebuilds and atomically updates the globally installed PSPScript compiler
(default: $INSTALL_DIR -> $BIN_DIR/pspscript).

Options:
  --pull        Run 'git pull --ff-only' in the repository before rebuilding
  --test        Run the xUnit test suite before updating the installed binary
  -h, --help    Show this help message

Environment variables:
  PSPSCRIPT_INSTALL_DIR   Install directory (default: \$HOME/.local/share/pspscript)
  PSPSCRIPT_BIN_DIR       Symlink directory (default: \$HOME/.local/bin)
  PSPSCRIPT_RID           Target .NET RID   (default: linux-x64)
EOF
}

for arg in "$@"; do
    case "$arg" in
        --pull)
            DO_PULL=true
            ;;
        --test)
            DO_TEST=true
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Error: Unknown option '$arg'. Run './update.sh --help' for usage." >&2
            exit 1
            ;;
    esac
done

if [ "$DO_PULL" = true ]; then
    echo "Pulling latest changes (fast-forward only)..."
    git -C "$SCRIPT_DIR" pull --ff-only
fi

if [ "$DO_TEST" = true ]; then
    echo "Running PSPScript test suite..."
    dotnet test "$SCRIPT_DIR/tests/PspScript.Tests/PspScript.Tests.csproj"
fi

mkdir -p "$(dirname "$INSTALL_DIR")" "$BIN_DIR"

STAGING_DIR="${INSTALL_DIR}.staging.$$"
BACKUP_DIR="${INSTALL_DIR}.backup.$$"

cleanup() {
    rm -rf "$STAGING_DIR"
    if [ -d "$BACKUP_DIR" ]; then
        if [ ! -d "$INSTALL_DIR" ]; then
            mv "$BACKUP_DIR" "$INSTALL_DIR"
            echo "Update failed; restored previous installation at: $INSTALL_DIR" >&2
        else
            rm -rf "$BACKUP_DIR"
        fi
    fi
}
trap cleanup EXIT

echo "Building updated self-contained PSPScript compiler ($RID, .NET runtime bundled)..."

dotnet publish "$SCRIPT_DIR/PspScript.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "$STAGING_DIR"

echo "Verifying staged binary..."
"$STAGING_DIR/PspScript" --help >/dev/null

if [ -d "$INSTALL_DIR" ]; then
    mv "$INSTALL_DIR" "$BACKUP_DIR"
fi
mv "$STAGING_DIR" "$INSTALL_DIR"
ln -sfn "$INSTALL_DIR/PspScript" "$BIN_DIR/pspscript"

if [ -d "$BACKUP_DIR" ]; then
    rm -rf "$BACKUP_DIR"
fi

GIT_INFO=""
if command -v git >/dev/null 2>&1 && git -C "$SCRIPT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    BRANCH="$(git -C "$SCRIPT_DIR" rev-parse --abbrev-ref HEAD)"
    SHA="$(git -C "$SCRIPT_DIR" rev-parse --short HEAD)"
    GIT_INFO=" ($BRANCH @ $SHA)"
fi

echo ""
echo "Updated PSPScript${GIT_INFO} at: $INSTALL_DIR"
echo "Symlinked CLI binary:            $BIN_DIR/pspscript -> $INSTALL_DIR/PspScript"
