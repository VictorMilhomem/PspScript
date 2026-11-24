#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${PSPSCRIPT_INSTALL_DIR:-$HOME/.local/share/pspscript}"
BIN_DIR="${PSPSCRIPT_BIN_DIR:-$HOME/.local/bin}"
RID="${PSPSCRIPT_RID:-linux-x64}"

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

echo ""
echo "Installed PSPScript to: $INSTALL_DIR"
echo "Symlinked CLI binary:   $BIN_DIR/pspscript -> $INSTALL_DIR/PspScript"

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
