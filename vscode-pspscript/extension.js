const fs = require("fs");
const os = require("os");
const path = require("path");
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

/** @type {LanguageClient | undefined} */
let client;

/**
 * Returns the file modification timestamp in ms, or -1 if the file does not exist.
 * @param {string} filePath
 */
function getMtimeMs(filePath) {
  try {
    return fs.statSync(filePath).mtimeMs;
  } catch {
    return -1;
  }
}

/**
 * Resolves the command and arguments used to launch `PspScript --lsp`.
 * Priority:
 *  1. User setting `pspscript.serverPath` (native binary or `.dll`)
 *  2. Newest binary (by mtime) among:
 *     - `~/.local/share/pspscript/PspScript` (installed by `./install.sh` / `./update.sh`)
 *     - `<repo>/bin/Release/net10.0/PspScript.dll`
 *     - `<repo>/bin/Debug/net10.0/PspScript.dll`
 *  3. Fallback `dotnet run --project <repo>/PspScript.csproj -- --lsp` or `pspscript --lsp`
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
  const installedBinary = path.join(os.homedir(), ".local", "share", "pspscript", "PspScript");
  const releaseDll = path.join(repoRoot, "bin", "Release", "net10.0", "PspScript.dll");
  const debugDll = path.join(repoRoot, "bin", "Debug", "net10.0", "PspScript.dll");

  const candidates = [
    {
      mtime: getMtimeMs(installedBinary),
      exec: { command: installedBinary, args: ["--lsp"] }
    },
    {
      mtime: getMtimeMs(releaseDll),
      exec: { command: "dotnet", args: [releaseDll, "--lsp"] }
    },
    {
      mtime: getMtimeMs(debugDll),
      exec: { command: "dotnet", args: [debugDll, "--lsp"] }
    }
  ].filter((c) => c.mtime > 0);

  if (candidates.length > 0) {
    candidates.sort((a, b) => b.mtime - a.mtime);
    return candidates[0].exec;
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
