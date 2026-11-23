using System.Globalization;
using Parlot;
using Parlot.Fluent;
using PspScript.Ast;
using static Parlot.Fluent.Parsers;

namespace PspScript.Parsing;

public static class PspParser
{
    private const string IdentifierPartChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "import", "game", "asset", "message", "struct", "actor", "state",
        "global", "level", "temp", "var", "fn",
        "init", "update", "draw", "exit", "on",
        "int", "u8", "u16", "u32", "f32", "bool", "void",
        "vec2", "vec3", "rect", "color", "timer", "ref",
        "if", "else", "for", "each", "in", "break", "return",
        "send", "spawn", "destroy", "goto",
        "true", "false", "null", "self"
    };

    private static readonly HashSet<string> BuiltinTypes = new(StringComparer.Ordinal)
    {
        "int", "u8", "u16", "u32", "f32", "bool", "void",
        "vec2", "vec3", "rect", "color", "timer"
    };

    
    public static readonly Parser<ProgramNode> CompiledProgramParser = BuildProgramParser();
  
    public static bool TryParse(string source, out ProgramNode? program, out ParseError? error)
    {
        return CompiledProgramParser.TryParse(source, out program, out error);
    }

    private static Parser<string> Keyword(string word) =>
        Terms.Keyword(word, IdentifierPartChars)
            .Then(static (context, span) => span.ToString());

    private static Parser<ProgramNode> BuildProgramParser()
    {
        // ====================================================================
        // Lexical Primitives & Identifiers
        // ====================================================================

        var identifier = Terms.Identifier()
            .Then(static (context, span) => span.ToString())
            .When(static (context, id) => !Keywords.Contains(id));

        // Allows any valid identifier name even if it overlaps with a keyword when needed (e.g., field names)
        var rawIdentifier = Terms.Identifier()
            .Then(static (context, span) => span.ToString());

        var stringLiteral = Terms.String(StringLiteralQuotes.Double)
            .Then(static (context, span) => span.ToString());

        var intLiteral = Terms.Integer(NumberOptions.None)
            .Then(static (context, val) => (int)val);

        // ====================================================================
        // Type System (TypeRef)
        // ====================================================================

        var typeRef = Deferred<TypeRef>();

        // ref<Enemy>
        var actorRefType = Keyword("ref")
            .SkipAnd(Terms.Char('<'))
            .SkipAnd(identifier.ElseError("Expected actor type name inside ref<...>"))
            .AndSkip(Terms.Char('>').ElseError("Expected '>' closing ref type"))
            .Then<TypeRef>(static (context, actorName) => new ActorRefTypeRef(actorName));

        // [32]Bullet
        var fixedArrayType = Between(
                Terms.Char('['),
                intLiteral.ElseError("Expected constant integer capacity inside '[...]'"),
                Terms.Char(']').ElseError("Expected ']' closing fixed array length")
            )
            .And(typeRef)
            .Then<TypeRef>(static (context, x) => new FixedArrayTypeRef(x.Item1, x.Item2));

        // Built-in or user-defined type name
        var namedOrBuiltinType = rawIdentifier
            .Then<TypeRef>(static (context, name) => BuiltinTypes.Contains(name)
                ? new BuiltinTypeRef(name)
                : new NamedTypeRef(name));

        typeRef.Parser = OneOf(actorRefType, fixedArrayType, namedOrBuiltinType);

        // ====================================================================
        // Expressions & PSP Literals
        // ====================================================================

        var expr = Deferred<Expr>();

        // #RRGGBB or #RRGGBBAA -> packed into PSP native ABGR8888 uint
        var hexColorExpr = Terms.Char('#')
            .SkipAnd(
                Literals.Pattern(static c => char.IsAsciiHexDigit(c), minSize: 6, maxSize: 8)
                    .When(static (context, span) => span.Length is 6 or 8)
                    .ElseError("Invalid hex color literal")
            )
            .Then<Expr>(static (context, span) =>
            {
                var hex = span.Span;
                uint r = uint.Parse(hex[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                uint g = uint.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                uint b = uint.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                uint a = hex.Length == 8
                    ? uint.Parse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : 0xFF;
                uint abgr = (a << 24) | (b << 16) | (g << 8) | r;
                return new ColorLiteralExpr(abgr);
            });

        // Numbers: float (10.5), time (0.5s / 250ms), or int (42)
        // Fractional '.' is only consumed when followed by a digit so range literals like `0..10` stay intact.
        var digits = Literals.Pattern(static c => char.IsAsciiDigit(c), minSize: 1);
        var fracPartParser = Literals.Char('.').SkipAnd(digits)
            .ThenElse<string?>(static (context, s) => s.ToString(), null);
        var numericExpr = SkipWhiteSpace(digits)
            .And(fracPartParser)
            .And(Literals.Text("ms").Or(Literals.Text("s")).Else(string.Empty))
            .Then<Expr>(static (context, x) =>
            {
                string intPart = x.Item1.ToString();
                string? fracPart = x.Item2;
                string suffix = x.Item3;

                if (suffix == "ms")
                {
                    float val = float.Parse(fracPart is not null ? $"{intPart}.{fracPart}" : intPart, CultureInfo.InvariantCulture);
                    return new TimeLiteralExpr(val / 1000.0f);
                }

                if (suffix == "s")
                {
                    float val = float.Parse(fracPart is not null ? $"{intPart}.{fracPart}" : intPart, CultureInfo.InvariantCulture);
                    return new TimeLiteralExpr(val);
                }

                if (fracPart is not null)
                {
                    return new FloatLiteralExpr(float.Parse($"{intPart}.{fracPart}", CultureInfo.InvariantCulture));
                }

                return new IntLiteralExpr(int.Parse(intPart, CultureInfo.InvariantCulture));
            });

        var boolExpr = Keyword("true").Then<Expr>(static (context, _) => new BooleanLiteralExpr(true))
            .Or(Keyword("false").Then<Expr>(static (context, _) => new BooleanLiteralExpr(false)));

        var nullExpr = Keyword("null").Then<Expr>(static (context, _) => new NullLiteralExpr());
        var selfExpr = Keyword("self").Then<Expr>(static (context, _) => new SelfExpr());
        var strExpr = stringLiteral.Then<Expr>(static (context, s) => new StringLiteralExpr(s));
        var idExpr = identifier.Then<Expr>(static (context, name) => new IdentifierExpr(name));

        // { x, y } or { x, y, z }
        var vecLiteralExpr = Between(
                Terms.Char('{'),
                Separated(Terms.Char(','), expr, min: 2, max: 4),
                Terms.Char('}')
            )
            .Then<Expr>(static (context, list) => new VecLiteralExpr(list));

        var primaryExpr = OneOf(
            hexColorExpr,
            numericExpr,
            strExpr,
            boolExpr,
            nullExpr,
            selfExpr,
            vecLiteralExpr,
            Between(Terms.Char('('), expr, Terms.Char(')').ElseError("Expected ')'")),
            idExpr
        );

        // Postfix suffixes: call `(...)`, field `.x`, index `[i]`
        var callSuffix = Between(
                Terms.Char('('),
                Separated(Terms.Char(','), expr, min: 0),
                Terms.Char(')').ElseError("Expected ')' after arguments")
            )
            .Then<Func<Expr, Expr>>(static (context, args) => target => new CallExpr(target, args));

        var fieldSuffix = Not(Terms.Text(".."))
            .SkipAnd(Terms.Char('.'))
            .SkipAnd(rawIdentifier.ElseError("Expected field or method name after '.'"))
            .Then<Func<Expr, Expr>>(static (context, field) => target => new FieldAccessExpr(target, field));

        var indexSuffix = Between(
                Terms.Char('['),
                expr,
                Terms.Char(']').ElseError("Expected ']' after index expression")
            )
            .Then<Func<Expr, Expr>>(static (context, idx) => target => new IndexAccessExpr(target, idx));

        var postfixExpr = primaryExpr
            .And(ZeroOrMany(OneOf(callSuffix, fieldSuffix, indexSuffix)))
            .Then(static (context, x) =>
            {
                var current = x.Item1;
                foreach (var applySuffix in x.Item2)
                {
                    current = applySuffix(current);
                }
                return current;
            });

        // Unary operators: `-x`, `!flag`
        var unaryExpr = Deferred<Expr>();
        unaryExpr.Parser = OneOf(
            Terms.Char('-').SkipAnd(unaryExpr).Then<Expr>(static (context, operand) => new UnaryExpr(UnaryOp.Negate, operand)),
            Terms.Char('!').SkipAnd(unaryExpr).Then<Expr>(static (context, operand) => new UnaryExpr(UnaryOp.Not, operand)),
            postfixExpr
        );

        // Precedence climbing (highest to lowest)
        var factorExpr = unaryExpr.LeftAssociative(
            (Terms.Text("*"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Mul, b)),
            (Terms.Text("/"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Div, b)),
            (Terms.Text("%"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Mod, b))
        );

        var termExpr = factorExpr.LeftAssociative(
            (Terms.Text("+"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Add, b)),
            (Terms.Text("-"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Sub, b))
        );

        var comparisonExpr = termExpr.LeftAssociative(
            (Terms.Text("<="), static (context, a, b) => new BinaryExpr(a, BinaryOp.LessEqual, b)),
            (Terms.Text(">="), static (context, a, b) => new BinaryExpr(a, BinaryOp.GreaterEqual, b)),
            (Terms.Text("<"),  static (context, a, b) => new BinaryExpr(a, BinaryOp.Less, b)),
            (Terms.Text(">"),  static (context, a, b) => new BinaryExpr(a, BinaryOp.Greater, b))
        );

        var equalityExpr = comparisonExpr.LeftAssociative(
            (Terms.Text("=="), static (context, a, b) => new BinaryExpr(a, BinaryOp.Equal, b)),
            (Terms.Text("!="), static (context, a, b) => new BinaryExpr(a, BinaryOp.NotEqual, b))
        );

        var logicAndExpr = equalityExpr.LeftAssociative(
            (Terms.Text("&&"), static (context, a, b) => new BinaryExpr(a, BinaryOp.LogicalAnd, b))
        );

        var logicOrExpr = logicAndExpr.LeftAssociative(
            (Terms.Text("||"), static (context, a, b) => new BinaryExpr(a, BinaryOp.LogicalOr, b))
        );

        expr.Parser = logicOrExpr;

        // ====================================================================
        // Shared Helpers (NamedArgs, Fields, Params, Blocks)
        // ====================================================================

        // amount: 25
        var namedArg = rawIdentifier
            .AndSkip(Terms.Char(':'))
            .And(expr)
            .Then(static (context, x) => new NamedArg(x.Item1, x.Item2));

        var namedArgList = Separated(Terms.Char(','), namedArg);

        // { amount: 25, knockback: { -10.0, 0.0 } }
        var namedArgBlock = Between(
            Terms.Char('{'),
            Separated(Terms.Char(','), namedArg, min: 0),
            Terms.Char('}').ElseError("Expected '}' closing initializer arguments")
        );

        var optionalTypeAnnotation = Terms.Char(':')
            .SkipAnd(typeRef)
            .ThenElse<TypeRef?>(static (context, t) => t, null);

        var optionalInitializer = Terms.Char('=')
            .SkipAnd(expr)
            .ThenElse<Expr?>(static (context, e) => e, null);

        // pos: vec2 = { 0.0, 0.0 }
        var fieldDecl = rawIdentifier
            .AndSkip(Terms.Char(':'))
            .And(typeRef)
            .And(optionalInitializer)
            .AndSkip(ZeroOrOne(Terms.Char(',')))
            .Then(static (context, x) => new FieldDecl(x.Item1, x.Item2, x.Item3));

        // dt: f32
        var paramDecl = identifier
            .AndSkip(Terms.Char(':'))
            .And(typeRef)
            .Then(static (context, x) => new ParamDecl(x.Item1, x.Item2));

        var paramList = Separated(Terms.Char(','), paramDecl, min: 0);

        // ====================================================================
        // Statements & Blocks
        // ====================================================================

        var stmt = Deferred<Stmt>();
        var blockStmt = Deferred<BlockStmt>();

        blockStmt.Parser = Between(
                Terms.Char('{'),
                ZeroOrMany(stmt),
                Terms.Char('}').ElseError("Expected '}' at the end of block")
            )
            .Then(static (context, stmts) => new BlockStmt(stmts));

        // temp? var x (: Type)? (= Expr)?
        var localVarScope = OneOf(
            Keyword("temp").SkipAnd(Keyword("var")).Then(static (context, _) => MemoryScope.FrameArena),
            Keyword("var").Then(static (context, _) => MemoryScope.Stack)
        );

        var localVarStmt = localVarScope
            .And(identifier.ElseError("Expected variable name after 'var'"))
            .And(optionalTypeAnnotation)
            .And(optionalInitializer)
            .Then<Stmt>(static (context, x) => new LocalVarStmt(
                Scope: x.Item1,
                Name: x.Item2,
                ExplicitType: x.Item3,
                Initializer: x.Item4
            ));

        // if cond { ... } else { ... }
        var ifStmt = Deferred<IfStmt>();
        ifStmt.Parser = Keyword("if")
            .SkipAnd(expr)
            .And(blockStmt)
            .And(
                Keyword("else").SkipAnd(
                    ifStmt.Then<Stmt>(static (context, elif) => elif)
                    .Or(blockStmt.Then<Stmt>(static (context, blk) => blk))
                ).ThenElse<Stmt?>(static (context, s) => s, null)
            )
            .Then(static (context, x) => new IfStmt(x.Item1, x.Item2, x.Item3));

        // for i in start..end { ... }
        var forRangeStmt = Keyword("for")
            .SkipAnd(identifier)
            .AndSkip(Keyword("in"))
            .And(expr)
            .AndSkip(Terms.Text("..").ElseError("Expected '..' in range 'for' loop"))
            .And(expr)
            .And(blockStmt)
            .Then<Stmt>(static (context, x) => new ForRangeStmt(x.Item1, x.Item2, x.Item3, x.Item4));

        // for cond { ... }
        var forCondStmt = Keyword("for")
            .SkipAnd(expr)
            .And(blockStmt)
            .Then<Stmt>(static (context, x) => new ForStmt(x.Item1, x.Item2));

        // each b in Bullet { ... }
        var forEachStmt = Keyword("each")
            .SkipAnd(identifier.ElseError("Expected iterator variable name after 'each'"))
            .AndSkip(Keyword("in").ElseError("Expected 'in' in 'each' loop"))
            .And(identifier.ElseError("Expected actor pool name after 'in'"))
            .And(blockStmt)
            .Then<Stmt>(static (context, x) => new ForEachStmt(x.Item1, x.Item2, x.Item3));

        // break
        var breakStmt = Keyword("break")
            .Then<Stmt>(static (context, _) => new BreakStmt());

        // return expr?
        var returnStmt = Keyword("return")
            .SkipAnd(expr.NoNewLineBefore().ThenElse<Expr?>(static (context, e) => e, null))
            .Then<Stmt>(static (context, val) => new ReturnStmt(val));

        // send(target, Hit { amount: 25 })
        var sendStmt = Keyword("send")
            .SkipAnd(Terms.Char('(').ElseError("Expected '(' after 'send'"))
            .SkipAnd(expr)
            .AndSkip(Terms.Char(',').ElseError("Expected ',' after message target"))
            .And(identifier.ElseError("Expected message type name"))
            .And(namedArgBlock.Else(Array.Empty<NamedArg>()))
            .AndSkip(Terms.Char(')').ElseError("Expected ')' closing 'send'"))
            .Then<Stmt>(static (context, x) => new SendStmt(
                Target: x.Item1,
                MessageName: x.Item2,
                Arguments: x.Item3
            ));

        // (var e =)? spawn Enemy { pos: { 10.0, 20.0 } }
        var spawnWithBinding = Keyword("var")
            .SkipAnd(identifier)
            .AndSkip(Terms.Char('='))
            .AndSkip(Keyword("spawn"))
            .And(identifier.ElseError("Expected actor name after 'spawn'"))
            .And(namedArgBlock.Else(Array.Empty<NamedArg>()))
            .Then<Stmt>(static (context, x) => new SpawnStmt(
                BindingName: x.Item1,
                ActorName: x.Item2,
                Initializers: x.Item3
            ));

        var spawnStandalone = Keyword("spawn")
            .SkipAnd(identifier.ElseError("Expected actor name after 'spawn'"))
            .And(namedArgBlock.Else(Array.Empty<NamedArg>()))
            .Then<Stmt>(static (context, x) => new SpawnStmt(
                BindingName: null,
                ActorName: x.Item1,
                Initializers: x.Item2
            ));

        // destroy self
        var destroyStmt = Keyword("destroy")
            .SkipAnd(expr)
            .Then<Stmt>(static (context, target) => new DestroyStmt(target));

        // goto GameOver
        var transitionStmt = Keyword("goto")
            .SkipAnd(identifier.ElseError("Expected target state name after 'goto'"))
            .Then<Stmt>(static (context, stateName) => new TransitionStmt(stateName));

        // Assignment (`target += val`) or standalone expression (`sprite(...)`)
        var assignOp = OneOf(
            Terms.Text("+=").Then(static (context, _) => AssignOp.AddAssign),
            Terms.Text("-=").Then(static (context, _) => AssignOp.SubAssign),
            Terms.Text("*=").Then(static (context, _) => AssignOp.MulAssign),
            Terms.Text("/=").Then(static (context, _) => AssignOp.DivAssign),
            Terms.Text("=").Then(static (context, _) => AssignOp.Assign)
        );

        var assignOrExprStmt = postfixExpr
            .And(assignOp.And(expr).ThenElse<(AssignOp Op, Expr? Value)>(static (context, x) => (x.Item1, x.Item2), default))
            .Then<Stmt>(static (context, x) => x.Item2.Value is not null
                ? new AssignStmt(x.Item1, x.Item2.Op, x.Item2.Value)
                : new ExprStmt(x.Item1));

        stmt.Parser = OneOf(
            spawnWithBinding,
            localVarStmt,
            ifStmt.Then<Stmt>(static (context, i) => i),
            forRangeStmt,
            forCondStmt,
            forEachStmt,
            breakStmt,
            returnStmt,
            sendStmt,
            spawnStandalone,
            destroyStmt,
            transitionStmt,
            assignOrExprStmt
        );

        // ====================================================================
        // Top-Level Declarations & Members
        // ====================================================================

        // Lifecycle hooks: init(), update(dt: f32), draw(), exit()
        var lifecycleKind = OneOf(
            Keyword("init").Then(static (context, _) => LifecycleKind.Init),
            Keyword("update").Then(static (context, _) => LifecycleKind.Update),
            Keyword("draw").Then(static (context, _) => LifecycleKind.Draw),
            Keyword("exit").Then(static (context, _) => LifecycleKind.Exit)
        );

        var lifecycleHook = lifecycleKind
            .And(Between(Terms.Char('('), paramList, Terms.Char(')')))
            .And(blockStmt)
            .Then(static (context, x) => new LifecycleHook(x.Item1, x.Item2, x.Item3));

        // Message handler: on Hit(msg) { ... }
        var messageHandler = Keyword("on")
            .SkipAnd(identifier.ElseError("Expected message type after 'on'"))
            .And(Between(
                Terms.Char('('),
                identifier.ElseError("Expected parameter name inside message handler"),
                Terms.Char(')')
            ))
            .And(blockStmt)
            .Then(static (context, x) => new MessageHandler(x.Item1, x.Item2, x.Item3));

        // Function / Method: fn foo(a: int) -> bool { ... }
        var funcDecl = Keyword("fn")
            .SkipAnd(identifier.ElseError("Expected function name after 'fn'"))
            .And(Between(Terms.Char('('), paramList, Terms.Char(')')))
            .And(Terms.Text("->").SkipAnd(typeRef).Else(new BuiltinTypeRef("void")))
            .And(blockStmt)
            .Then(static (context, x) => new FuncDecl(
                Name: x.Item1,
                Parameters: x.Item2,
                ReturnType: x.Item3,
                Body: x.Item4
            ));

        // Game config: game "StarDust PSP" { ram_budget: 16MB, fps: 60 }
        var sizeOrIntConfig = Terms.Integer(NumberOptions.None)
            .And(Literals.Text("MB").Or(Literals.Text("KB")).Else(string.Empty))
            .Then<ConfigValue>(static (context, x) =>
            {
                long val = x.Item1;
                string unit = x.Item2;
                if (unit == "MB") return new SizeConfigValue(val * 1024L * 1024L);
                if (unit == "KB") return new SizeConfigValue(val * 1024L);
                return new IntConfigValue((int)val);
            });

        var configValue = OneOf(
            stringLiteral.Then<ConfigValue>(static (context, s) => new StringConfigValue(s)),
            sizeOrIntConfig,
            rawIdentifier.Then<ConfigValue>(static (context, id) => new IdentConfigValue(id))
        );

        var configEntry = rawIdentifier
            .AndSkip(Terms.Char(':'))
            .And(configValue)
            .AndSkip(ZeroOrOne(Terms.Char(',')));

        var gameConfigDecl = Keyword("game")
            .SkipAnd(stringLiteral.ElseError("Expected game title string after 'game'"))
            .And(Between(
                Terms.Char('{'),
                ZeroOrMany(configEntry),
                Terms.Char('}').ElseError("Expected '}' closing game config")
            ))
            .Then<TopDecl>(static (context, x) =>
            {
                var dict = x.Item2.ToDictionary(k => k.Item1, v => v.Item2, StringComparer.Ordinal);
                return new GameConfigDecl(x.Item1, dict);
            });

        // Asset: asset ship = texture("ship.png", format: RGBA4444)
        var assetDecl = Keyword("asset")
            .SkipAnd(identifier.ElseError("Expected asset name after 'asset'"))
            .AndSkip(Terms.Char('='))
            .And(rawIdentifier.ElseError("Expected asset loader kind (e.g., 'texture', 'sound')"))
            .AndSkip(Terms.Char('('))
            .And(stringLiteral.ElseError("Expected asset file path string"))
            .And(Terms.Char(',').SkipAnd(namedArgList).Else(Array.Empty<NamedArg>()))
            .AndSkip(Terms.Char(')').ElseError("Expected ')' closing asset declaration"))
            .Then<TopDecl>(static (context, x) => new AssetDecl(
                Name: x.Item1,
                AssetKind: x.Item2,
                FilePath: x.Item3,
                Options: x.Item4
            ));

        // Message: message Hit { amount: int }
        var messageDecl = Keyword("message")
            .SkipAnd(identifier.ElseError("Expected message name after 'message'"))
            .And(Between(
                Terms.Char('{'),
                ZeroOrMany(fieldDecl),
                Terms.Char('}').ElseError("Expected '}' closing message declaration")
            ))
            .Then<TopDecl>(static (context, x) => new MessageDecl(x.Item1, x.Item2));

        // Struct: struct Stats { score: int = 0 }
        var structDecl = Keyword("struct")
            .SkipAnd(identifier.ElseError("Expected struct name after 'struct'"))
            .And(Between(
                Terms.Char('{'),
                ZeroOrMany(fieldDecl),
                Terms.Char('}').ElseError("Expected '}' closing struct declaration")
            ))
            .Then<TopDecl>(static (context, x) => new StructDecl(x.Item1, x.Item2));

        // Shared member parser for Actor and State blocks
        var containerMember = OneOf(
            lifecycleHook.Then<object>(static (context, h) => h),
            messageHandler.Then<object>(static (context, m) => m),
            funcDecl.Then<object>(static (context, fn) => fn),
            fieldDecl.Then<object>(static (context, f) => f)
        );

        var poolCapacity = Between(Terms.Char('['), intLiteral, Terms.Char(']'))
            .ThenElse<int?>(static (context, cap) => cap, null);

        // Actor: actor Enemy[32] { ... }
        var actorDecl = Keyword("actor")
            .SkipAnd(identifier.ElseError("Expected actor name after 'actor'"))
            .And(poolCapacity)
            .And(Between(
                Terms.Char('{'),
                ZeroOrMany(containerMember),
                Terms.Char('}').ElseError("Expected '}' closing actor declaration")
            ))
            .Then<TopDecl>(static (context, x) =>
            {
                var members = x.Item3;
                return new ActorDecl(
                    Name: x.Item1,
                    PoolCapacity: x.Item2,
                    Fields: members.OfType<FieldDecl>().ToList(),
                    Hooks: members.OfType<LifecycleHook>().ToList(),
                    Handlers: members.OfType<MessageHandler>().ToList(),
                    Methods: members.OfType<FuncDecl>().ToList()
                );
            });

        // State: state Gameplay { ... }
        var stateDecl = Keyword("state")
            .SkipAnd(identifier.ElseError("Expected state name after 'state'"))
            .And(Between(
                Terms.Char('{'),
                ZeroOrMany(containerMember),
                Terms.Char('}').ElseError("Expected '}' closing state declaration")
            ))
            .Then<TopDecl>(static (context, x) =>
            {
                var members = x.Item2;
                return new StateDecl(
                    Name: x.Item1,
                    Fields: members.OfType<FieldDecl>().ToList(),
                    Hooks: members.OfType<LifecycleHook>().ToList(),
                    Handlers: members.OfType<MessageHandler>().ToList(),
                    Methods: members.OfType<FuncDecl>().ToList()
                );
            });

        // Global / Level variable: level var score: int = 0
        var globalVarScope = OneOf(
            Keyword("level").SkipAnd(Keyword("var")).Then(static (context, _) => MemoryScope.LevelArena),
            Keyword("global").SkipAnd(Keyword("var")).Then(static (context, _) => MemoryScope.Global),
            Keyword("var").Then(static (context, _) => MemoryScope.Global)
        );

        var globalVarDecl = globalVarScope
            .And(identifier.ElseError("Expected global variable name after 'var'"))
            .And(optionalTypeAnnotation)
            .And(optionalInitializer)
            .Then<TopDecl>(static (context, x) => new GlobalVarDecl(
                Scope: x.Item1,
                Name: x.Item2,
                Explicittype: x.Item3,
                Initializer: x.Item4
            ));

        // ====================================================================
        // Root Program
        // ====================================================================

        var importDecl = Keyword("import")
            .SkipAnd(OneOf(stringLiteral, identifier).ElseError("Expected module name or path string after 'import'"))
            .Then<TopDecl>(static (context, path) => new ImportDecl(path));

        var topDecl = OneOf(
            importDecl,
            gameConfigDecl,
            assetDecl,
            messageDecl,
            structDecl,
            actorDecl,
            stateDecl,
            globalVarDecl,
            funcDecl.Then<TopDecl>(static (context, fn) => fn)
        );

        return ZeroOrMany(topDecl)
            .AndSkip(SkipWhiteSpace(Eof()).ElseError("Unexpected trailing tokens at end of file"))
            .Then(static (context, decls) => new ProgramNode(decls))
            .WithComments(static comments =>
            {
                comments.WithWhiteSpaceOrNewLine();
                comments.WithSingleLine("//");
                comments.WithMultiLine("/*", "*/");
            });
    }
}
