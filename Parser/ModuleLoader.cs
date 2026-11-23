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

                var (targetFullPath, targetDisplay, notFoundMessage) = ResolveImportTarget(rawImport, normalizedRel, currentDir);
                if (targetFullPath is null)
                {
                    _errors.Add(new ModuleLoadError(displayPath, notFoundMessage!));
                    continue;
                }

                LoadFileRecursive(targetFullPath, targetDisplay, requestedBy: displayPath);
            }
            else if (decl is AssetDecl assetDecl)
            {
                _mergedDeclarations.Add(assetDecl with { SourceDirectory = currentDir });
            }
            else if (decl is GameConfigDecl gameDecl)
            {
                _mergedDeclarations.Add(gameDecl with { SourceDirectory = currentDir });
            }
            else
            {
                _mergedDeclarations.Add(decl);
            }
        }

        _activeStack.RemoveAt(_activeStack.Count - 1);
    }

    private static (string? CanonicalPath, string DisplayPath, string? NotFoundMessage) ResolveImportTarget(
        string rawImport,
        string normalizedRel,
        string currentDir)
    {
        if (IsBareModuleSpecifier(rawImport))
        {
            // 1. Check project-local override in the importing file's directory first
            string localCandidate = Path.GetFullPath(Path.Combine(currentDir, normalizedRel));
            if (File.Exists(localCandidate))
            {
                string localDisplay = Path.GetRelativePath(Directory.GetCurrentDirectory(), localCandidate);
                return (localCandidate, localDisplay, null);
            }

            // 2. Fall back to the bundled Standard Library (AppContext.BaseDirectory/StdLib, then repo StdLib/)
            string? stdLibPath = TryResolveStdLibModule(normalizedRel);
            if (stdLibPath is not null)
            {
                return (stdLibPath, $"StdLib/{normalizedRel}", null);
            }

            return (
                null,
                rawImport,
                $"Imported module '{rawImport}' not found (checked '{localCandidate}' and StdLib)."
            );
        }

        string targetFullPath = Path.GetFullPath(Path.Combine(currentDir, normalizedRel));
        string targetDisplay = Path.GetRelativePath(Directory.GetCurrentDirectory(), targetFullPath);
        return (targetFullPath, targetDisplay, null);
    }

    private static bool IsBareModuleSpecifier(string specifier) =>
        !specifier.StartsWith('.') &&
        !specifier.Contains('/') &&
        !specifier.Contains('\\') &&
        !Path.IsPathRooted(specifier);

    private static string? TryResolveStdLibModule(string normalizedFileName)
    {
        string baseDirCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "StdLib", normalizedFileName));
        if (File.Exists(baseDirCandidate))
        {
            return baseDirCandidate;
        }

        // Development fallback (e.g. `dotnet run` when StdLib resides at repository/solution root)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir?.Parent is not null)
        {
            dir = dir.Parent;
            string parentCandidate = Path.GetFullPath(Path.Combine(dir.FullName, "StdLib", normalizedFileName));
            if (File.Exists(parentCandidate))
            {
                return parentCandidate;
            }
        }

        string cwdCandidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "StdLib", normalizedFileName));
        return File.Exists(cwdCandidate) ? cwdCandidate : null;
    }
}
