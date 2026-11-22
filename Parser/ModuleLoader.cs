using PspScript.Ast;

namespace PspScript.Parsing;

public sealed record ModuleLoadError(
    string FilePath,
    string Message,
    int Line = 0,
    int Column = 0,
    string? SourceText = null
);

public sealed record ModuleLoadResult(
    bool Success,
    ProgramNode? Program,
    IReadOnlyList<string> LoadedFiles,
    IReadOnlyList<ModuleLoadError> Errors
);

/// <summary>
/// Recursively loads a PSPScript entry file and all transitive <c>import "..."</c> modules,
/// resolving relative paths, deduplicating diamond imports, detecting circular dependencies,
/// and merging declarations into a single <see cref="ProgramNode"/>.
/// </summary>
public sealed class ModuleLoader
{
    private readonly HashSet<string> _loadedPaths = new(StringComparer.Ordinal);
    private readonly List<string> _loadedFilesInOrder = [];
    private readonly List<string> _activeStack = [];
    private readonly List<TopDecl> _mergedDeclarations = [];
    private readonly List<ModuleLoadError> _errors = [];

    public static ModuleLoadResult Load(string entryFilePath)
    {
        var loader = new ModuleLoader();
        return loader.LoadEntry(entryFilePath);
    }

    private ModuleLoadResult LoadEntry(string entryFilePath)
    {
        string fullPath = Path.GetFullPath(entryFilePath);
        if (!File.Exists(fullPath))
        {
            _errors.Add(new ModuleLoadError(entryFilePath, $"Input file not found: '{entryFilePath}'"));
            return new ModuleLoadResult(false, null, _loadedFilesInOrder, _errors);
        }

        LoadFileRecursive(fullPath, displayPath: entryFilePath, requestedBy: null);

        if (_errors.Count > 0)
        {
            return new ModuleLoadResult(false, null, _loadedFilesInOrder, _errors);
        }

        return new ModuleLoadResult(
            Success: true,
            Program: new ProgramNode(_mergedDeclarations),
            LoadedFiles: _loadedFilesInOrder,
            Errors: _errors
        );
    }

    private void LoadFileRecursive(string canonicalPath, string displayPath, string? requestedBy)
    {
        // Detect circular imports (A -> B -> A)
        int cycleStart = _activeStack.IndexOf(canonicalPath);
        if (cycleStart >= 0)
        {
            var cycleChain = _activeStack
                .Skip(cycleStart)
                .Append(canonicalPath)
                .Select(Path.GetFileName);

            _errors.Add(new ModuleLoadError(
                FilePath: requestedBy ?? displayPath,
                Message: $"Circular import detected: {string.Join(" -> ", cycleChain)}"
            ));
            return;
        }

        // Deduplicate already-loaded modules (#pragma once semantics)
        if (!_loadedPaths.Add(canonicalPath))
        {
            return;
        }

        if (!File.Exists(canonicalPath))
        {
            string contextMsg = requestedBy is not null
                ? $"Imported module '{displayPath}' not found (resolved to '{canonicalPath}')."
                : $"File not found: '{displayPath}'.";
            _errors.Add(new ModuleLoadError(requestedBy ?? displayPath, contextMsg));
            return;
        }

        string source = File.ReadAllText(canonicalPath);
        if (!PspParser.TryParse(source, out var parsed, out var parseError) || parsed is null)
        {
            _errors.Add(new ModuleLoadError(
                FilePath: displayPath,
                Message: parseError?.Message ?? "Syntax error",
                Line: parseError?.Position.Line ?? 1,
                Column: parseError?.Position.Column ?? 1,
                SourceText: source
            ));
            return;
        }

        _loadedFilesInOrder.Add(displayPath);
        _activeStack.Add(canonicalPath);

        string currentDir = Path.GetDirectoryName(canonicalPath) ?? Directory.GetCurrentDirectory();

        foreach (var decl in parsed.Declarations)
        {
            if (decl is ImportDecl importDecl)
            {
                string rawImport = importDecl.Path.Trim();
                if (string.IsNullOrEmpty(rawImport))
                {
                    _errors.Add(new ModuleLoadError(displayPath, "Import path cannot be empty."));
                    continue;
                }

                string normalizedRel = rawImport.EndsWith(".psps", StringComparison.OrdinalIgnoreCase)
                    ? rawImport
                    : $"{rawImport}.psps";

                string targetFullPath = Path.GetFullPath(Path.Combine(currentDir, normalizedRel));
                string targetDisplay = Path.GetRelativePath(Directory.GetCurrentDirectory(), targetFullPath);

                LoadFileRecursive(targetFullPath, targetDisplay, requestedBy: displayPath);
            }
            else
            {
                _mergedDeclarations.Add(decl);
            }
        }

        _activeStack.RemoveAt(_activeStack.Count - 1);
    }
}
