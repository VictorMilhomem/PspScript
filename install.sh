#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${PSPSCRIPT_INSTALL_DIR:-$HOME/.local/share/pspscript}"
BIN_DIR="${PSPSCRIPT_BIN_DIR:-$HOME/.local/bin}"
VSCODE_EXT_DIR="${PSPSCRIPT_VSCODE_EXT_DIR:-$HOME/.vscode/extensions}"
RID="${PSPSCRIPT_RID:-linux-x64}"
INSTALL_VSCODE=true

usage() {
    cat <<EOF
Usage: ./install.sh [options]

Builds and installs the self-contained PSPScript compiler (with .NET runtime,
StdLib, Runtime, and Makefile) and the bundled VS Code extension.

Options:
  --no-vscode   Skip installing the bundled VS Code extension (vscode-pspscript)
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
        --no-vscode)
            INSTALL_VSCODE=false
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Error: Unknown option '$arg'. Run './install.sh --help' for usage." >&2
            exit 1
            ;;
    esac
done

echo "Building self-contained PSPScript compiler ($RID, .NET runtime bundled)..."

rm -rf "$INSTALL_DIR"
mkdir -p "$INSTALL_DIR" "$BIN_DIR"

dotnet publish "$SCRIPT_DIR/PspScript.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "$INSTALL_DIR"

ln -sfn "$INSTALL_DIR/PspScript" "$BIN_DIR/pspscript"

EXT_SRC_DIR="$SCRIPT_DIR/vscode-pspscript"
VSCODE_LINK_PATH=""
if [ "$INSTALL_VSCODE" = true ] && [ -d "$EXT_SRC_DIR" ]; then
    echo ""
    echo "Installing VS Code extension (vscode-pspscript)..."
    if command -v npm >/dev/null 2>&1; then
        npm --prefix "$EXT_SRC_DIR" install --omit=dev --no-audit --no-fund
    else
        echo "Warning: 'npm' not found; skipping vscode-languageclient install." >&2
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

echo ""
echo "Installed PSPScript to: $INSTALL_DIR"
echo "Symlinked CLI binary:   $BIN_DIR/pspscript -> $INSTALL_DIR/PspScript"
if [ -n "$VSCODE_LINK_PATH" ]; then
    echo "Linked VS Code ext:     $VSCODE_LINK_PATH -> $EXT_SRC_DIR"
    echo "Note: Reload VS Code ('Developer: Reload Window') to activate the extension."
fi

case ":$PATH:" in
    *":$BIN_DIR:"*)
        echo "Ready! Run 'pspscript --help' from any directory."
        ;;
    *)
        echo "Note: '$BIN_DIR' is not currently in your PATH."
        echo "Add this line to your shell profile (~/.bashrc or ~/.zshrc):"
        echo "  export PATH=\"$BIN_DIR:\$PATH\""
        ;;
esac
