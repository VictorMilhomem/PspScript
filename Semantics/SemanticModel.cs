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
    private readonly Dictionary<ConstPattern, ConstValue> _resolvedConstPatterns = new(ReferenceEqualityComparer.Instance);
    private readonly List<LambdaExpr> _lambdas = [];
    private readonly Dictionary<LambdaExpr, int> _lambdaIds = new(ReferenceEqualityComparer.Instance);

    public SymbolTable Symbols { get; }
    public IReadOnlyDictionary<AstNode, ResolvedType> NodeTypes => _nodeTypes;
    public IReadOnlyDictionary<ConstPattern, ConstValue> ResolvedConstPatterns => _resolvedConstPatterns;
    public IReadOnlyList<LambdaExpr> Lambdas => _lambdas;
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

    internal void RecordConstPattern(ConstPattern pattern, ConstValue value)
    {
        _resolvedConstPatterns[pattern] = value;
    }

    internal int RegisterLambda(LambdaExpr lambda)
    {
        if (_lambdaIds.TryGetValue(lambda, out int existing))
            return existing;

        int id = _lambdas.Count;
        _lambdas.Add(lambda);
        _lambdaIds[lambda] = id;
        return id;
    }

    public bool TryGetLambdaId(LambdaExpr lambda, out int id) =>
        _lambdaIds.TryGetValue(lambda, out id);

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
