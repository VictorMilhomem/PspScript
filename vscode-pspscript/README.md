# PSPScript VS Code Extension

Syntax highlighting, bracket/comment configuration, file icon, and code snippets for **PSPScript (`.psps`)**.

## Local Installation (Fastest)

Symlink the extension folder directly into your local VS Code extensions directory and reload VS Code (`Ctrl+Shift+P` -> `Developer: Reload Window`):

```bash
mkdir -p ~/.vscode/extensions
ln -sfn "$(pwd)/vscode-pspscript" ~/.vscode/extensions/pspscript-0.2.0
```

## Package as `.vsix` (Optional)

```bash
cd vscode-pspscript
npx @vscode/vsce package
code --install-extension pspscript-0.2.0.vsix
```
