using PspScript.Ast;

namespace PspScript.Semantics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info
}

public sealed record SemanticDiagnostic(
    string Message,
    SourceSpan Span = default,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error
)
{
    public override string ToString()
    {
        string location = Span.Line > 0 ? $"({Span.Line}:{Span.Column}) " : string.Empty;
        return $"[{Severity}] {location}{Message}";
    }
}


public sealed class SemanticModel
{
    private readonly Dictionary<AstNode, ResolvedType> _nodeTypes = new(ReferenceEqualityComparer.Instance);

    public SymbolTable Symbols { get; }
    public IReadOnlyDictionary<AstNode, ResolvedType> NodeTypes => _nodeTypes;
    public long EstimatedStaticMemoryBytes { get; internal set; }
    public long? ConfiguredRamBudgetBytes => Symbols.ConfiguredRamBudgetBytes;

    public SemanticModel(SymbolTable symbols)
    {
        Symbols = symbols;
    }

    internal ResolvedType RecordType(AstNode node, ResolvedType type)
    {
        _nodeTypes[node] = type;
        return type;
    }

    public bool TryGetType(AstNode node, out ResolvedType? type) =>
        _nodeTypes.TryGetValue(node, out type);

    public ResolvedType GetTypeOrDefault(AstNode node) =>
        _nodeTypes.TryGetValue(node, out var type) ? type : ResolvedType.Error;
}


public sealed record SemanticAnalysisResult(
    bool Success,
    SemanticModel? Model,
    IReadOnlyList<SemanticDiagnostic> Diagnostics
);
