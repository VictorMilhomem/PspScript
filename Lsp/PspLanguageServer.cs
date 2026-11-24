using System.Text.Json;
using PspScript.Parsing;
using PspScript.Semantics;

namespace PspScript.Lsp;

/// <summary>
/// PSPScript Language Server implementing LSP over JSON-RPC 2.0 (stdio or in-memory streams).
/// Provides real-time syntax &amp; semantic diagnostics, context-aware autocomplete, and hover info.
/// </summary>
public sealed class PspLanguageServer
{
    private readonly LspTransport _transport;
    private readonly Dictionary<string, string> _openDocuments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SymbolTable> _cachedSymbols = new(StringComparer.Ordinal);
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
                triggerCharacters = new[] { ".", "\"", "<" }
            },
            hoverProvider = true
        },
        serverInfo = new
        {
            name = "pspscript-lsp",
            version = "0.2.0"
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
        _cachedSymbols.Remove(uri);
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

        ModuleLoadResult loadResult;
        bool loadedViaProjectEntry = false;
        string? canonicalOpenPath = !string.IsNullOrEmpty(filePath) ? Path.GetFullPath(filePath) : null;

        if (!string.IsNullOrEmpty(filePath))
        {
            var overrides = BuildSourceOverrides();
            (loadResult, loadedViaProjectEntry) = ResolveProjectEntryAndLoad(filePath, overrides);
        }
        else
        {
            if (PspParser.TryParse(text, out var parsed, out var parseError) && parsed is not null)
            {
                loadResult = new ModuleLoadResult(true, parsed, [uri], []);
            }
            else
            {
                loadResult = new ModuleLoadResult(false, null, [], [
                    new ModuleLoadError(
                        uri,
                        parseError?.Message ?? "Syntax error",
                        parseError?.Position.Line ?? 1,
                        parseError?.Position.Column ?? 1,
                        text
                    )
                ]);
            }
        }

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
                diagnostics.Add(new
                {
                    range = new
                    {
                        start = new { line = l, character = c },
                        end = new { line = l, character = c + 1 }
                    },
                    severity = 1, // Error
                    source = "pspscript",
                    message = err.Message
                });
            }
        }
        else
        {
            var analysis = SemanticAnalyzer.Analyze(loadResult.Program);
            if (analysis.Model is not null)
            {
                _cachedSymbols[uri] = analysis.Model.Symbols;
            }

            foreach (var diag in analysis.Diagnostics)
            {
                int l = Math.Max(0, diag.Span.Line - 1);
                int c = Math.Max(0, diag.Span.Column - 1);
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
                        end = new { line = l, character = c + 1 }
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

    private object HandleCompletion(JsonElement @params)
    {
        string uri = @params.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
        var pos = @params.GetProperty("position");
        int line = pos.GetProperty("line").GetInt32();
        int character = pos.GetProperty("character").GetInt32();

        string text = _openDocuments.TryGetValue(uri, out var docText) ? docText : string.Empty;
        _cachedSymbols.TryGetValue(uri, out var symbols);

        var items = LspCompletionProvider.GetCompletions(text, UriToLocalPath(uri), line, character, symbols);
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

        _cachedSymbols.TryGetValue(uri, out var symbols);
        string? markdown = LspCompletionProvider.GetHoverMarkdown(text, line, character, symbols);
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

    private static string? UriToLocalPath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            return parsed.LocalPath;
        }
        return null;
    }

    public bool ShutdownRequested => _shutdownRequested;
}
