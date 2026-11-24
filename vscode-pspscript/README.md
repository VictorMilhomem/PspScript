# PSPScript VS Code Extension & Language Server (LSP)

Syntax highlighting, real-time LSP diagnostics & autocomplete, hover documentation, file icon, and code snippets for **PSPScript (`.psps`)**.

## Installation

### Prerequisites
- **.NET 10 SDK** (`dotnet` on `PATH`)
- **Node.js & npm** (to install `vscode-languageclient` for the extension)

### Local Installation (Recommended)

From the root of the `PspScript` repository:

```bash
# 1. Build the PSPScript compiler (includes the built-in `--lsp` Language Server)
dotnet build

# 2. Install the VS Code extension's Node dependency (`vscode-languageclient`)
cd vscode-pspscript && npm install && cd ..

# 3. Symlink the extension into VS Code's extensions folder
mkdir -p ~/.vscode/extensions
ln -sfn "$(pwd)/vscode-pspscript" ~/.vscode/extensions/pspscript-0.2.0
```

Reload VS Code (`Ctrl+Shift+P` → `Developer: Reload Window`) and open any `.psps` file.

### Custom Server Path (Optional)

By default, the extension auto-detects `bin/Release/net10.0/PspScript.dll` or `bin/Debug/net10.0/PspScript.dll` relative to the repository. If you packaged or moved the extension, set `pspscript.serverPath` in VS Code Settings (`settings.json`):

```json
{
  "pspscript.serverPath": "/path/to/PspScript/bin/Release/net10.0/PspScript.dll"
}
```

## Package as `.vsix` (Optional)

```bash
cd vscode-pspscript
npm install
npx @vscode/vsce package
code --install-extension pspscript-0.2.0.vsix
```
