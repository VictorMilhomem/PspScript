namespace PspScript.Ast;
public readonly record struct SourceSpan(int Line, int Column);

public abstract record AstNode(SourceSpan Span = default);

// ============================================================================
// Root Program & Top-Level Decl
// ============================================================================

public record ProgramNode(IReadOnlyList<TopDecl> Declarations): AstNode;

public abstract record TopDecl : AstNode;

public record ImportDecl(string Path) : TopDecl;

public record GameConfigDecl(
    string Title,
    IReadOnlyDictionary<string, ConfigValue> Settings,
    string? SourceDirectory = null
): TopDecl;

public abstract record ConfigValue;
public record IntConfigValue(int Value): ConfigValue;
public record SizeConfigValue(long Bytes): ConfigValue; // Normalized from KB/MB
public record StringConfigValue(string Value): ConfigValue;
public record BoolConfigValue(bool Value): ConfigValue;
public record IdentConfigValue(string Name): ConfigValue;

public record AssetDecl(
    string Name,
    string AssetKind,
    string FilePath,
    IReadOnlyList<NamedArg> Options,
    string? SourceDirectory = null
): TopDecl;

public record MessageDecl(
    string Name,
    IReadOnlyList<FieldDecl> Fields
): TopDecl;

public record StructDecl(
    string Name,
    IReadOnlyList<FieldDecl> Fields
): TopDecl;

public record EnumDecl(
    string Name,
    IReadOnlyList<string> Variants
): TopDecl;

public record ActorDecl(
    string Name,
    int? PoolCapacity, // null = singleton actor; non-null = static pool
    IReadOnlyList<FieldDecl> Fields,
    IReadOnlyList<LifecycleHook> Hooks,
    IReadOnlyList<MessageHandler> Handlers,
    IReadOnlyList<FuncDecl> Methods
): TopDecl;

public record StateDecl(
    string Name,
    IReadOnlyList<FieldDecl> Fields,
    IReadOnlyList<LifecycleHook> Hooks,
    IReadOnlyList<MessageHandler> Handlers,
    IReadOnlyList<FuncDecl> Methods
): TopDecl;

public record GlobalVarDecl(
    MemoryScope Scope,
    string Name,
    TypeRef? Explicittype,
    Expr? Initializer
): TopDecl;

public record FuncDecl(
    string Name,
    IReadOnlyList<ParamDecl> Parameters,
    TypeRef ReturnType,
    BlockStmt Body
): TopDecl;

public enum MemoryScope
{
    Stack, // Default local C stack variable
    Global, // Persistent .bss across the entire game
    LevelArena, // Reset automatically on state transition (goto)
    FrameArena // Reset automatically at the end of every frame (temp)
}

public enum LifecycleKind
{
    Init,
    Update,
    Draw,
    Exit
}

public record ParamDecl(string Name, TypeRef Type): AstNode;

public record FieldDecl(
    string Name,
    TypeRef Type,
    Expr? DefaultValue

): AstNode;

public record LifecycleHook(
    LifecycleKind Kind,
    IReadOnlyList<ParamDecl> Parameters,
    BlockStmt Body
) : AstNode;

public record MessageHandler(
    string MessageName,
    string ParamName,
    BlockStmt Body
): AstNode;

public record NamedArg(string Name, Expr Value): AstNode;
public abstract record TypeRef : AstNode;

// ============================================================================
// Type System (Game-First & Static Hardware Types)
// ============================================================================

/// <summary>
/// Built-in primitives & game types:
/// int, u8, u16, u32, f32, bool, void, vec2, vec3, rect, color, timer
/// </summary>
public record BuiltinTypeRef(string Name) : TypeRef;

/// <summary>
/// User-defined struct or actor type name
/// </summary>
public record NamedTypeRef(string Name) : TypeRef;

/// <summary>
/// Safe 32-bit generational handle: ref&lt;Enemy&gt;
/// </summary>
public record ActorRefTypeRef(string ActorName) : TypeRef;

/// <summary>
/// Fixed-capacity static array: [32]Bullet
/// </summary>
public record FixedArrayTypeRef(int Length, TypeRef ElementType) : TypeRef;

// ============================================================================
// Statements (Imperative & Actor Event Flow)
// ============================================================================

public abstract record Stmt : AstNode;

public record BlockStmt(IReadOnlyList<Stmt> Statements): Stmt;

public record LocalVarStmt(
    MemoryScope Scope, // Stack or FrameArena (temp)
    string Name,
    TypeRef? ExplicitType,
    Expr? Initializer
): Stmt;

public record AssignStmt(
    Expr Target, // Can be IndetifierExpr, FieldAccessExpr, or IndexAccessExpr
    AssignOp Operator,
    Expr Value
): Stmt;

public enum AssignOp
{
    Assign, // =
    AddAssign, // +=
    SubAssign, // -=
    MulAssign, // *=
    DivAssign, // /=
    BitAndAssign, // &=
    BitOrAssign, // |=
    BitXorAssign, // ^=
    ShiftLeftAssign, // <<=
    ShiftRightAssign // >>=
}

public record IfStmt(
    Expr Condition,
    BlockStmt ThenBranch,
    Stmt? ElseBranch
): Stmt;

public record MatchStmt(
    Expr Scrutinee,
    IReadOnlyList<MatchArm> Arms
) : Stmt;

public record MatchArm(
    MatchPattern Pattern,
    Stmt Body
) : AstNode;

public abstract record MatchPattern : AstNode;
public record EnumVariantPattern(string EnumName, string VariantName) : MatchPattern;
public record IntLiteralPattern(int Value) : MatchPattern;
public record WildcardPattern() : MatchPattern;

/// <summary>
/// Go-style conditional loop: for cond { ... }
/// </summary>
public record ForStmt(
    Expr Condition,
    BlockStmt Body
) : Stmt;

/// <summary>
/// Exclusive integer range loop: for i in start..end { ... }
/// </summary>
public record ForRangeStmt(
    string IteratorName,
    Expr Start,
    Expr End,
    BlockStmt Body
) : Stmt;

/// <summary>
/// each b in Bullet { ... }
/// </summary>
public record ForEachStmt(
    string IteratorName,
    string PoolName,
    BlockStmt Body
) : Stmt;

public record BreakStmt() : Stmt;

public record ReturnStmt(Expr? Value) : Stmt;

/// <summary>
/// send(target, Hit { amount: 10, knockback: { -50.0, 0.0 } })
/// </summary>
public record SendStmt(
    Expr Target,
    string MessageName,
    IReadOnlyList<NamedArg> Arguments
) : Stmt;

/// <summary>
/// var e = spawn Enemy { pos: { 100.0, 20.0 } }
/// </summary>
public record SpawnStmt(
    string? BindingName, // Optional: if assigned to a local variable
    string ActorName,
    IReadOnlyList<NamedArg> Initializers
) : Stmt;

/// <summary>
/// destroy self (or destroy target_ref)
/// </summary>
public record DestroyStmt(Expr Target) : Stmt;

/// <summary>
/// goto GameOver (triggers LevelArena reset and state transition)
/// </summary>
public record TransitionStmt(string TargetStateName) : Stmt;

/// <summary>
/// Standalone expression call, e.g., sprite(ship_tex, player.pos)
/// </summary>
public record ExprStmt(Expr Expression) : Stmt;


// ============================================================================
// Expressions & PSP Literals
// ============================================================================
public abstract record Expr : AstNode;
public record IntLiteralExpr(int Value): Expr;
public record FloatLiteralExpr(float Value): Expr; // Emits 'f' suffix in C (e.g., 10.0f)
public record TimeLiteralExpr(float Seconds): Expr; // Normalized from 's' or 'ms'
public record ColorLiteralExpr(uint AbgrValue): Expr; // Pre-packed PSP native ABGR8888
public record StringLiteralExpr(string Text): Expr;
public record BooleanLiteralExpr(bool Value): Expr;
public record NullLiteralExpr(): Expr;
public record SelfExpr(): Expr;

public record VecLiteralExpr(IReadOnlyList<Expr>Components): Expr;
public record IdentifierExpr(string Name): Expr;
public record UnaryExpr(UnaryOp Operator, Expr Operand): Expr;

public enum UnaryOp
{
    Negate, // -
    Not, // !
    BitNot // ~
}

public record BinaryExpr(Expr Left, BinaryOp Op, Expr Right): Expr;

public enum BinaryOp
{
    Add, Sub, Mul, Div, Mod,
    ShiftLeft, ShiftRight,
    BitAnd, BitXor, BitOr,
    Equal, NotEqual,
    Less, LessEqual, Greater, GreaterEqual,
    LogicalAnd, LogicalOr
}
public record CallExpr(
    Expr Callee,
    IReadOnlyList<Expr>Arguments
): Expr;

public record FieldAccessExpr(
    Expr Target,
    string FieldName
): Expr;

public record IndexAccessExpr(
    Expr Target,
    Expr Index
): Expr;

