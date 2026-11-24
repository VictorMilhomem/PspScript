const fs = require("fs");
const path = require("path");
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

/** @type {LanguageClient | undefined} */
let client;

/**
 * Resolves the command and arguments used to launch `PspScript --lsp`.
 * Priority:
 *  1. User setting `pspscript.serverPath` (native binary or `.dll`)
 *  2. Auto-detected `../bin/Release/net10.0/PspScript.dll` or `../bin/Debug/net10.0/PspScript.dll`
 *  3. Fallback `dotnet run --project <repo>/PspScript.csproj -- --lsp`
 * @param {vscode.ExtensionContext} context
 */
function resolveServerExecutable(context) {
  const config = vscode.workspace.getConfiguration("pspscript");
  const customPath = (config.get("serverPath") || "").trim();

  if (customPath) {
    if (customPath.endsWith(".dll")) {
      return { command: "dotnet", args: [customPath, "--lsp"] };
    }
    return { command: customPath, args: ["--lsp"] };
  }

  // Follow symlinks so `~/.vscode/extensions/pspscript-0.2.0` resolves back to the repo
  let extDir = context.extensionPath;
  try {
    extDir = fs.realpathSync(extDir);
  } catch {
    // Keep original path if realpath fails
  }

  const repoRoot = path.resolve(extDir, "..");
  const releaseDll = path.join(repoRoot, "bin", "Release", "net10.0", "PspScript.dll");
  if (fs.existsSync(releaseDll)) {
    return { command: "dotnet", args: [releaseDll, "--lsp"] };
  }

  const debugDll = path.join(repoRoot, "bin", "Debug", "net10.0", "PspScript.dll");
  if (fs.existsSync(debugDll)) {
    return { command: "dotnet", args: [debugDll, "--lsp"] };
  }

  const csproj = path.join(repoRoot, "PspScript.csproj");
  if (fs.existsSync(csproj)) {
    return { command: "dotnet", args: ["run", "--project", csproj, "--", "--lsp"] };
  }

  return { command: "pspscript", args: ["--lsp"] };
}

/**
 * @param {vscode.ExtensionContext} context
 */
function activate(context) {
  const exec = resolveServerExecutable(context);

  const serverOptions = {
    command: exec.command,
    args: exec.args,
    transport: TransportKind.stdio
  };

  const clientOptions = {
    documentSelector: [{ scheme: "file", language: "pspscript" }],
    synchronize: {
      fileEvents: vscode.workspace.createFileSystemWatcher("**/*.psps")
    }
  };

  client = new LanguageClient(
    "pspscriptLanguageServer",
    "PSPScript Language Server",
    serverOptions,
    clientOptions
  );

  client.start();
}

function deactivate() {
  if (!client) {
    return undefined;
  }
  return client.stop();
}

module.exports = {
  activate,
  deactivate
};
