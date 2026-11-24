using System.Text.Json;
using PspScript.Ast;
using PspScript.Parsing;
using PspScript.Semantics;

namespace PspScript.Lsp;

internal sealed record CachedDocumentAnalysis(
    ProgramNode Program,
    SymbolTable Symbols,
    IReadOnlyList<ResolvedImportInfo> Imports
);

/// <summary>
/// PSPScript Language Server implementing LSP over JSON-RPC 2.0 (stdio or in-memory streams).
/// Provides real-time syntax &amp; semantic diagnostics, context-aware autocomplete, hover info,
/// go-to-definition, signature help, and hierarchical document symbols.
/// </summary>
public sealed class PspLanguageServer
{
    private readonly LspTransport _transport;
    private readonly Dictionary<string, string> _openDocuments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedDocumentAnalysis> _cachedAnalysis = new(StringComparer.Ordinal);
    private bool _shutdownRequested;

    public PspLanguageServer(Stream input, Stream output)
    {
        _transport = new LspTransport(input, output);
    }

    public static async Task<int> RunStdioAsync(CancellationToken cancellationToken = default)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var server = new PspLanguageServer(stdin, stdout);
        await server.RunAsync(cancellationToken);
        return 0;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var doc = await _transport.ReadMessageAsync(cancellationToken);
            if (doc is null)
                break;

            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var methodProp))
                continue;

            string method = methodProp.GetString() ?? string.Empty;
            bool hasId = root.TryGetProperty("id", out var idProp);
            root.TryGetProperty("params", out var paramsProp);

            switch (method)
            {
                case "initialize":
                    if (hasId)
                    {
                        await _transport.SendResponseAsync(idProp, BuildInitializeResult(), cancellationToken);
                    }
                    break;

                case "initialized":
                    break;

                case "shutdown":
                    _shutdownRequested = true;
                    if (hasId)
                    {
                        await _transport.SendResponseAsync(idProp, null, cancellationToken);
                    }
                    break;

                case "exit":
                    return;

                case "textDocument/didOpen":
                    await HandleDidOpenAsync(paramsProp, cancellationToken);
                    break;

                case "textDocument/didChange":
                    await HandleDidChangeAsync(paramsProp, cancellationToken);
                    break;

                case "textDocument/didSave":
                    await HandleDidSaveAsync(paramsProp, cancellationToken);
                    break;

                case "textDocument/didClose":
                    await HandleDidCloseAsync(paramsProp, cancellationToken);
                    break;

                case "textDocument/completion":
                    if (hasId)
                    {
                        var result = HandleCompletion(paramsProp);
                        await _transport.SendResponseAsync(idProp, result, cancellationToken);
                    }
                    break;

                case "textDocument/hover":
                    if (hasId)
                    {
                        var result = HandleHover(paramsProp);
                        await _transport.SendResponseAsync(idProp, result, cancellationToken);
                    }
                    break;

                case "textDocument/definition":
                    if (hasId)
                    {
                        var result = HandleDefinition(paramsProp);
                        await _transport.SendResponseAsync(idProp, result, cancellationToken);
                    }
                    break;

                case "textDocument/signatureHelp":
                    if (hasId)
                    {
                        var result = HandleSignatureHelp(paramsProp);
                        await _transport.SendResponseAsync(idProp, result, cancellationToken);
                    }
                    break;

                case "textDocument/documentSymbol":
                    if (hasId)
                    {
                        var result = HandleDocumentSymbol(paramsProp);
                        await _transport.SendResponseAsync(idProp, result, cancellationToken);
                    }
                    break;

                default:
                    if (hasId)
                    {
                        await _transport.SendResponseAsync(idProp, null, cancellationToken);
                    }
                    break;
            }
        }
    }

    private static object BuildInitializeResult() => new
    {
        capabilities = new
        {
            textDocumentSync = 1, // Full document sync
            completionProvider = new
            {
                resolveProvider = false,
                triggerCharacters = new[] { ".", "\"", "<", ":", "{" }
            },
            hoverProvider = true,
            definitionProvider = true,
            documentSymbolProvider = true,
            signatureHelpProvider = new
            {
                triggerCharacters = new[] { "(", "," }
            }
        },
        serverInfo = new
        {
            name = "pspscript-lsp",
            version = "0.2.1"
        }
    };

    private async Task HandleDidOpenAsync(JsonElement @params, CancellationToken cancellationToken)
    {
        var td = @params.GetProperty("textDocument");
        string uri = td.GetProperty("uri").GetString() ?? string.Empty;
        string text = td.GetProperty("text").GetString() ?? string.Empty;
        _openDocuments[uri] = text;
        await AnalyzeAndPublishDiagnosticsAsync(uri, text, cancellationToken);
    }

    private async Task HandleDidChangeAsync(JsonElement @params, CancellationToken cancellationToken)
    {
        string uri = @params.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
        var changes = @params.GetProperty("contentChanges");
        if (changes.GetArrayLength() == 0)
            return;

        string text = changes[changes.GetArrayLength() - 1].GetProperty("text").GetString() ?? string.Empty;
        _openDocuments[uri] = text;
        await AnalyzeAndPublishDiagnosticsAsync(uri, text, cancellationToken);
    }

    private async Task HandleDidSaveAsync(JsonElement @params, CancellationToken cancellationToken)
    {
        string uri = @params.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
        if (_openDocuments.TryGetValue(uri, out string? text))
        {
            await AnalyzeAndPublishDiagnosticsAsync(uri, text, cancellationToken);
        }
    }

    private async Task HandleDidCloseAsync(JsonElement @params, CancellationToken cancellationToken)
    {
        string uri = @params.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
        _openDocuments.Remove(uri);
        _cachedAnalysis.Remove(uri);
        await _transport.SendNotificationAsync("textDocument/publishDiagnostics", new
        {
            uri,
            diagnostics = Array.Empty<object>()
        }, cancellationToken);
    }

    private async Task AnalyzeAndPublishDiagnosticsAsync(string uri, string text, CancellationToken cancellationToken)
    {
        var diagnostics = new List<object>();
        string? filePath = UriToLocalPath(uri);
        string? canonicalOpenPath = !string.IsNullOrEmpty(filePath) ? Path.GetFullPath(filePath) : null;

        var overrides = BuildSourceOverrides();
        var (loadResult, loadedViaProjectEntry) = LoadDocumentOrProject(uri, text, filePath, overrides);

        if (!loadResult.Success || loadResult.Program is null)
        {
            foreach (var err in loadResult.Errors)
            {
                if (loadedViaProjectEntry && canonicalOpenPath is not null)
                {
                    string errFullPath = Path.GetFullPath(err.FilePath);
                    if (!string.Equals(errFullPath, canonicalOpenPath, StringComparison.Ordinal))
                        continue;
                }

                int l = Math.Max(0, err.Line - 1);
                int c = Math.Max(0, err.Column - 1);
                int tokenLen = ComputeErrorTokenLength(text, l, c);
                diagnostics.Add(new
                {
                    range = new
                    {
                        start = new { line = l, character = c },
                        end = new { line = l, character = c + tokenLen }
                    },
                    severity = 1, // Error
                    source = "pspscript",
                    message = err.Message
                });

                // Incomplete-line recovery: try masking the failing line with spaces so recently added
                // declarations above/below the cursor still update the symbol cache for IDE features.
                if (err.Line > 0)
                {
                    TryRecoverWithBlankedLine(uri, text, filePath, canonicalOpenPath, err.Line - 1);
                }
            }
        }
        else
        {
            var analysis = SemanticAnalyzer.Analyze(loadResult.Program, checkAssetPathsOnDisk: true);
            if (analysis.Model is not null)
            {
                _cachedAnalysis[uri] = new CachedDocumentAnalysis(
                    loadResult.Program,
                    analysis.Model.Symbols,
                    loadResult.ResolvedImports ?? []
                );
            }

            foreach (var diag in analysis.Diagnostics)
            {
                if (canonicalOpenPath is not null && !string.IsNullOrEmpty(diag.Span.FilePath))
                {
                    string diagPath = Path.GetFullPath(diag.Span.FilePath);
                    if (!string.Equals(diagPath, canonicalOpenPath, StringComparison.Ordinal))
                        continue;
                }

                int l = Math.Max(0, diag.Span.Line - 1);
                int c = Math.Max(0, diag.Span.Column - 1);
                int len = Math.Max(1, diag.Span.Length);
                int sev = diag.Severity switch
                {
                    DiagnosticSeverity.Warning => 2,
                    DiagnosticSeverity.Info => 3,
                    _ => 1
                };

                diagnostics.Add(new
                {
                    range = new
                    {
                        start = new { line = l, character = c },
                        end = new { line = l, character = c + len }
                    },
                    severity = sev,
                    source = "pspscript",
                    message = diag.Message
                });
            }
        }

        await _transport.SendNotificationAsync("textDocument/publishDiagnostics", new
        {
            uri,
            diagnostics
        }, cancellationToken);
    }

    private void TryRecoverWithBlankedLine(
        string uri,
        string text,
        string? filePath,
        string? canonicalOpenPath,
        int zeroBasedErrorLine)
    {
        string currentText = text;
        int lineToMask = zeroBasedErrorLine;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            string? maskedText = MaskLineWithSpaces(currentText, lineToMask);
            if (maskedText is null)
                return;

            currentText = maskedText;
            var overrides = BuildSourceOverrides();
            if (canonicalOpenPath is not null)
            {
                overrides[canonicalOpenPath] = currentText;
            }

            var (recoveredLoad, _) = LoadDocumentOrProject(uri, currentText, filePath, overrides);
            if (recoveredLoad.Success && recoveredLoad.Program is not null && recoveredLoad.Program.Declarations.Count > 0)
            {
                var analysis = SemanticAnalyzer.Analyze(recoveredLoad.Program, checkAssetPathsOnDisk: false);
                if (analysis.Model is not null)
                {
                    _cachedAnalysis[uri] = new CachedDocumentAnalysis(
                        recoveredLoad.Program,
                        analysis.Model.Symbols,
                        recoveredLoad.ResolvedImports ?? []
                    );
                }
                return;
            }

            var nextErr = recoveredLoad.Errors.FirstOrDefault(e =>
                canonicalOpenPath is null ||
                string.Equals(Path.GetFullPath(e.FilePath), canonicalOpenPath, StringComparison.Ordinal));
            if (nextErr is null || nextErr.Line <= 0 || nextErr.Line - 1 == lineToMask)
                return;

            lineToMask = nextErr.Line - 1;
        }
    }

    private CachedDocumentAnalysis? GetOrRecoverAnalysis(string uri, string text, int cursorLine)
    {
        string? filePath = UriToLocalPath(uri);
        string? canonicalPath = !string.IsNullOrEmpty(filePath) ? Path.GetFullPath(filePath) : null;

        // If cursor line is currently being edited and fails to parse, try blanking cursorLine on the fly
        string? masked = MaskLineWithSpaces(text, cursorLine);
        if (masked is not null)
        {
            var overrides = BuildSourceOverrides();
            if (canonicalPath is not null)
            {
                overrides[canonicalPath] = masked;
            }
            var (recovered, _) = LoadDocumentOrProject(uri, masked, filePath, overrides);
            if (recovered.Success && recovered.Program is not null && recovered.Program.Declarations.Count > 0)
            {
                var analysis = SemanticAnalyzer.Analyze(recovered.Program, checkAssetPathsOnDisk: false);
                if (analysis.Model is not null)
                {
                    var cached = new CachedDocumentAnalysis(
                        recovered.Program,
                        analysis.Model.Symbols,
                        recovered.ResolvedImports ?? []
                    );
                    _cachedAnalysis[uri] = cached;
                    return cached;
                }
            }
        }

        return _cachedAnalysis.TryGetValue(uri, out var existing) ? existing : null;
    }

    private static string? MaskLineWithSpaces(string text, int zeroBasedLine)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (zeroBasedLine < 0 || zeroBasedLine >= lines.Length)
            return null;

        string target = lines[zeroBasedLine];
        // Preserve any trailing '{' or '}' on that line so block balance remains intact
        char[] chars = target.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '{' && chars[i] != '}')
            {
                chars[i] = ' ';
            }
        }
        lines[zeroBasedLine] = new string(chars);
        return string.Join('\n', lines);
    }

    private static int ComputeErrorTokenLength(string text, int zeroBasedLine, int zeroBasedCol)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (zeroBasedLine < 0 || zeroBasedLine >= lines.Length)
            return 1;

        string lineText = lines[zeroBasedLine];
        if (zeroBasedCol < 0 || zeroBasedCol >= lineText.Length)
            return 1;

        int end = zeroBasedCol;
        while (end < lineText.Length && (char.IsLetterOrDigit(lineText[end]) || lineText[end] == '_'))
        {
            end++;
        }
        return Math.Max(1, end - zeroBasedCol);
    }

    private static (ModuleLoadResult Result, bool LoadedViaProjectEntry) LoadDocumentOrProject(
        string uri,
        string text,
        string? filePath,
        IReadOnlyDictionary<string, string> overrides)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            return ResolveProjectEntryAndLoad(filePath, overrides);
        }

        if (PspParser.TryParse(text, out var parsed, out var parseError, uri) && parsed is not null)
        {
            return (new ModuleLoadResult(true, parsed, [uri], []), false);
        }

        return (new ModuleLoadResult(false, null, [], [
            new ModuleLoadError(
                uri,
                parseError?.Message ?? "Syntax error",
                parseError?.Position.Line ?? 1,
                parseError?.Position.Column ?? 1,
                text
            )
        ]), false);
    }

    private object HandleCompletion(JsonElement @params)
    {
        string uri = @params.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
        var pos = @params.GetProperty("position");
        int line = pos.GetProperty("line").GetInt32();
        int character = pos.GetProperty("character").GetInt32();

        string text = _openDocuments.TryGetValue(uri, out var docText) ? docText : string.Empty;
        var cached = _cachedAnalysis.TryGetValue(uri, out var c) ? c : GetOrRecoverAnalysis(uri, text, line);

        var items = LspCompletionProvider.GetCompletions(
            text,
            UriToLocalPath(uri),
            line,
            character,
            cached?.Symbols,
            cached?.Program
        );

        return new
        {
            isIncomplete = false,
            items = items.Select(static i => new
            {
                label = i.Label,
                kind = i.Kind,
                detail = i.Detail,
                documentation = i.Documentation,
                insertText = i.InsertText
            }).ToArray()
        };
    }

    private object? HandleHover(JsonElement @params)
    {
        string uri = @params.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
        var pos = @params.GetProperty("position");
        int line = pos.GetProperty("line").GetInt32();
        int character = pos.GetProperty("character").GetInt32();

        if (!_openDocuments.TryGetValue(uri, out var text))
            return null;

        var cached = _cachedAnalysis.TryGetValue(uri, out var c) ? c : GetOrRecoverAnalysis(uri, text, line);
        string? markdown = LspCompletionProvider.GetHoverMarkdown(
            text,
            line,
            character,
            cached?.Symbols,
            cached?.Program,
            UriToLocalPath(uri)
        );
        if (markdown is null)
            return null;

        return new
        {
            contents = new
            {
                kind = "markdown",
                value = markdown
            }
        };
    }

    private object? HandleDefinition(JsonElement @params)
    {
        string uri = @params.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
        var pos = @params.GetProperty("position");
        int line = pos.GetProperty("line").GetInt32();
        int character = pos.GetProperty("character").GetInt32();

        if (!_openDocuments.TryGetValue(uri, out var text))
            return null;

        var cached = _cachedAnalysis.TryGetValue(uri, out var c) ? c : GetOrRecoverAnalysis(uri, text, line);
        return LspCompletionProvider.GetDefinitionLocation(
            text,
            uri,
            UriToLocalPath(uri),
            line,
            character,
            cached?.Symbols,
            cached?.Program,
            cached?.Imports
        );
    }

    private object? HandleSignatureHelp(JsonElement @params)
    {
        string uri = @params.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
        var pos = @params.GetProperty("position");
        int line = pos.GetProperty("line").GetInt32();
        int character = pos.GetProperty("character").GetInt32();

        if (!_openDocuments.TryGetValue(uri, out var text))
            return null;

        var cached = _cachedAnalysis.TryGetValue(uri, out var c) ? c : GetOrRecoverAnalysis(uri, text, line);
        return LspCompletionProvider.GetSignatureHelp(text, line, character, cached?.Symbols);
    }

    private object HandleDocumentSymbol(JsonElement @params)
    {
        string uri = @params.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
        string text = _openDocuments.TryGetValue(uri, out var docText) ? docText : string.Empty;
        _cachedAnalysis.TryGetValue(uri, out var cached);
        return LspCompletionProvider.GetDocumentSymbols(text, UriToLocalPath(uri), cached?.Program);
    }

    private static (ModuleLoadResult Result, bool LoadedViaProjectEntry) ResolveProjectEntryAndLoad(
        string openFilePath,
        IReadOnlyDictionary<string, string> overrides)
    {
        string canonicalOpen = Path.GetFullPath(openFilePath);
        string? dir = Path.GetDirectoryName(canonicalOpen);

        for (int depth = 0; depth < 4 && !string.IsNullOrEmpty(dir); depth++)
        {
            string candidateMain = Path.GetFullPath(Path.Combine(dir, "main.psps"));
            if (!string.Equals(candidateMain, canonicalOpen, StringComparison.Ordinal) &&
                (overrides.ContainsKey(candidateMain) || File.Exists(candidateMain)))
            {
                var projectLoad = ModuleLoader.Load(candidateMain, overrides);
                bool includesOpenFile = projectLoad.LoadedCanonicalPaths is not null &&
                                        projectLoad.LoadedCanonicalPaths.Contains(canonicalOpen);
                bool errorInOpenFile = projectLoad.Errors.Any(e =>
                    string.Equals(Path.GetFullPath(e.FilePath), canonicalOpen, StringComparison.Ordinal));

                if (includesOpenFile || errorInOpenFile)
                {
                    return (projectLoad, true);
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return (ModuleLoader.Load(openFilePath, overrides), false);
    }

    private Dictionary<string, string> BuildSourceOverrides()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (uri, text) in _openDocuments)
        {
            string? path = UriToLocalPath(uri);
            if (!string.IsNullOrEmpty(path))
            {
                map[Path.GetFullPath(path)] = text;
            }
        }
        return map;
    }

    internal static string? UriToLocalPath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            return parsed.LocalPath;
        }
        return null;
    }

    internal static string LocalPathToUri(string filePath)
    {
        if (filePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return filePath;
        return new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
    }

    public bool ShutdownRequested => _shutdownRequested;
}
