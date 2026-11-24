#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${PSPSCRIPT_INSTALL_DIR:-$HOME/.local/share/pspscript}"
BIN_DIR="${PSPSCRIPT_BIN_DIR:-$HOME/.local/bin}"
VSCODE_EXT_DIR="${PSPSCRIPT_VSCODE_EXT_DIR:-$HOME/.vscode/extensions}"
RID="${PSPSCRIPT_RID:-linux-x64}"

DO_PULL=false
DO_TEST=false
UPDATE_VSCODE=true

usage() {
    cat <<EOF
Usage: ./update.sh [options]

Rebuilds and atomically updates the globally installed PSPScript compiler
(default: $INSTALL_DIR -> $BIN_DIR/pspscript) and updates the bundled
VS Code extension ($VSCODE_EXT_DIR).

Options:
  --pull        Run 'git pull --ff-only' in the repository before rebuilding
  --test        Run the xUnit test suite before updating the installed binary
  --no-vscode   Skip updating the bundled VS Code extension (vscode-pspscript)
  -h, --help    Show this help message

Environment variables:
  PSPSCRIPT_INSTALL_DIR      Install directory      (default: \$HOME/.local/share/pspscript)
  PSPSCRIPT_BIN_DIR          Symlink directory      (default: \$HOME/.local/bin)
  PSPSCRIPT_VSCODE_EXT_DIR   VS Code extensions dir (default: \$HOME/.vscode/extensions)
  PSPSCRIPT_RID              Target .NET RID        (default: linux-x64)
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
        --no-vscode)
            UPDATE_VSCODE=false
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

EXT_SRC_DIR="$SCRIPT_DIR/vscode-pspscript"
VSCODE_LINK_PATH=""
if [ "$UPDATE_VSCODE" = true ] && [ -d "$EXT_SRC_DIR" ]; then
    echo ""
    echo "Updating VS Code extension (vscode-pspscript)..."
    if command -v npm >/dev/null 2>&1; then
        npm --prefix "$EXT_SRC_DIR" install --omit=dev --no-audit --no-fund
    else
        echo "Warning: 'npm' not found; skipping vscode-languageclient update." >&2
    fi

    EXT_VERSION="$(sed -n 's/^[[:space:]]*"version":[[:space:]]*"\([^"]*\)".*/\1/p' "$EXT_SRC_DIR/package.json" | head -n 1)"
    EXT_VERSION="${EXT_VERSION:-0.2.0}"

    mkdir -p "$VSCODE_EXT_DIR"
    for old_link in "$VSCODE_EXT_DIR"/pspscript-*; do
        if [ -L "$old_link" ]; then
            rm -f "$old_link"
        fi
    done

    VSCODE_LINK_PATH="$VSCODE_EXT_DIR/pspscript-$EXT_VERSION"
    ln -sfn "$EXT_SRC_DIR" "$VSCODE_LINK_PATH"
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
if [ -n "$VSCODE_LINK_PATH" ]; then
    echo "Updated VS Code ext:             $VSCODE_LINK_PATH -> $EXT_SRC_DIR"
    echo "Note: Reload VS Code ('Developer: Reload Window') to pick up extension/LSP updates."
fi
