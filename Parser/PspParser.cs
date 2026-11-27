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
        "import", "game", "asset", "message", "struct", "enum", "actor", "state",
        "const", "global", "level", "temp", "var", "fn",
        "init", "update", "draw", "exit", "on",
        "int", "u8", "u16", "u32", "f32", "bool", "void",
        "vec2", "vec3", "rect", "color", "timer", "ref",
        "if", "else", "match", "for", "each", "in", "break", "continue", "return",
        "send", "spawn", "destroy", "goto",
        "true", "false", "null", "self"
    };

    private static readonly HashSet<string> BuiltinTypes = new(StringComparer.Ordinal)
    {
        "int", "u8", "u16", "u32", "f32", "bool", "void",
        "vec2", "vec3", "rect", "color", "timer"
    };

    
    public static readonly Parser<ProgramNode> CompiledProgramParser = BuildProgramParser();
  
    public static bool TryParse(string source, out ProgramNode? program, out ParseError? error, string? filePath = null)
    {
        bool ok = CompiledProgramParser.TryParse(source, out program, out error);
        if (ok && program is not null && !string.IsNullOrEmpty(filePath))
        {
            program = StampFilePath(program, filePath);
        }
        return ok;
    }

    public static ProgramNode StampFilePath(ProgramNode program, string filePath)
    {
        return (ProgramNode)StampNode(program, filePath);
    }

    private static AstNode StampNode(AstNode node, string filePath)
    {
        var span = node.Span with { FilePath = filePath };
        return node switch
        {
            ProgramNode p => p with
            {
                Span = span,
                Declarations = p.Declarations.Select(d => (TopDecl)StampNode(d, filePath)).ToList()
            },
            AssetDecl a => a with
            {
                Span = span,
                Options = a.Options.Select(o => (NamedArg)StampNode(o, filePath)).ToList()
            },
            MessageDecl m => m with
            {
                Span = span,
                Fields = m.Fields.Select(f => (FieldDecl)StampNode(f, filePath)).ToList()
            },
            StructDecl s => s with
            {
                Span = span,
                Fields = s.Fields.Select(f => (FieldDecl)StampNode(f, filePath)).ToList()
            },
            ActorDecl act => act with
            {
                Span = span,
                PoolCapacityExpr = act.PoolCapacityExpr is not null ? (Expr)StampNode(act.PoolCapacityExpr, filePath) : null,
                Fields = act.Fields.Select(f => (FieldDecl)StampNode(f, filePath)).ToList(),
                Hooks = act.Hooks.Select(h => (LifecycleHook)StampNode(h, filePath)).ToList(),
                Handlers = act.Handlers.Select(h => (MessageHandler)StampNode(h, filePath)).ToList(),
                Methods = act.Methods.Select(fn => (FuncDecl)StampNode(fn, filePath)).ToList()
            },
            StateDecl st => st with
            {
                Span = span,
                Fields = st.Fields.Select(f => (FieldDecl)StampNode(f, filePath)).ToList(),
                Hooks = st.Hooks.Select(h => (LifecycleHook)StampNode(h, filePath)).ToList(),
                Handlers = st.Handlers.Select(h => (MessageHandler)StampNode(h, filePath)).ToList(),
                Methods = st.Methods.Select(fn => (FuncDecl)StampNode(fn, filePath)).ToList()
            },
            ConstDecl cd => cd with
            {
                Span = span,
                ExplicitType = cd.ExplicitType is not null ? (TypeRef)StampNode(cd.ExplicitType, filePath) : null,
                Initializer = (Expr)StampNode(cd.Initializer, filePath)
            },
            GlobalVarDecl g => g with
            {
                Span = span,
                Explicittype = g.Explicittype is not null ? (TypeRef)StampNode(g.Explicittype, filePath) : null,
                Initializer = g.Initializer is not null ? (Expr)StampNode(g.Initializer, filePath) : null
            },
            FuncDecl fn => fn with
            {
                Span = span,
                Parameters = fn.Parameters.Select(p => (ParamDecl)StampNode(p, filePath)).ToList(),
                ReturnType = (TypeRef)StampNode(fn.ReturnType, filePath),
                Body = (BlockStmt)StampNode(fn.Body, filePath)
            },
            ParamDecl pd => pd with
            {
                Span = span,
                Type = (TypeRef)StampNode(pd.Type, filePath)
            },
            FieldDecl fd => fd with
            {
                Span = span,
                Type = fd.Type is not null ? (TypeRef)StampNode(fd.Type, filePath) : null,
                DefaultValue = fd.DefaultValue is not null ? (Expr)StampNode(fd.DefaultValue, filePath) : null
            },
            LifecycleHook lh => lh with
            {
                Span = span,
                Parameters = lh.Parameters.Select(p => (ParamDecl)StampNode(p, filePath)).ToList(),
                Body = (BlockStmt)StampNode(lh.Body, filePath)
            },
            MessageHandler mh => mh with
            {
                Span = span,
                Body = (BlockStmt)StampNode(mh.Body, filePath)
            },
            NamedArg na => na with
            {
                Span = span,
                Value = (Expr)StampNode(na.Value, filePath)
            },
            FixedArrayTypeRef fa => fa with
            {
                Span = span,
                LengthExpr = (Expr)StampNode(fa.LengthExpr, filePath),
                ElementType = (TypeRef)StampNode(fa.ElementType, filePath)
            },
            FunctionTypeRef ft => ft with
            {
                Span = span,
                ParameterTypes = ft.ParameterTypes.Select(p => (TypeRef)StampNode(p, filePath)).ToList(),
                ReturnType = (TypeRef)StampNode(ft.ReturnType, filePath)
            },
            BlockStmt blk => blk with
            {
                Span = span,
                Statements = blk.Statements.Select(s => (Stmt)StampNode(s, filePath)).ToList()
            },
            LocalVarStmt lv => lv with
            {
                Span = span,
                ExplicitType = lv.ExplicitType is not null ? (TypeRef)StampNode(lv.ExplicitType, filePath) : null,
                Initializer = lv.Initializer is not null ? (Expr)StampNode(lv.Initializer, filePath) : null
            },
            LocalConstStmt lc => lc with
            {
                Span = span,
                ExplicitType = lc.ExplicitType is not null ? (TypeRef)StampNode(lc.ExplicitType, filePath) : null,
                Initializer = (Expr)StampNode(lc.Initializer, filePath)
            },
            AssignStmt asg => asg with
            {
                Span = span,
                Target = (Expr)StampNode(asg.Target, filePath),
                Value = (Expr)StampNode(asg.Value, filePath)
            },
            IfStmt ifs => ifs with
            {
                Span = span,
                Condition = (Expr)StampNode(ifs.Condition, filePath),
                ThenBranch = (BlockStmt)StampNode(ifs.ThenBranch, filePath),
                ElseBranch = ifs.ElseBranch is not null ? (Stmt)StampNode(ifs.ElseBranch, filePath) : null
            },
            MatchStmt ms => ms with
            {
                Span = span,
                Scrutinee = (Expr)StampNode(ms.Scrutinee, filePath),
                Arms = ms.Arms.Select(a => (MatchArm)StampNode(a, filePath)).ToList()
            },
            MatchArm ma => ma with
            {
                Span = span,
                Patterns = ma.Patterns.Select(p => (MatchPattern)StampNode(p, filePath)).ToList(),
                Body = (Stmt)StampNode(ma.Body, filePath)
            },
            MatchExpr me => me with
            {
                Span = span,
                Scrutinee = (Expr)StampNode(me.Scrutinee, filePath),
                Arms = me.Arms.Select(a => (MatchExprArm)StampNode(a, filePath)).ToList()
            },
            MatchExprArm mea => mea with
            {
                Span = span,
                Patterns = mea.Patterns.Select(p => (MatchPattern)StampNode(p, filePath)).ToList(),
                Value = (Expr)StampNode(mea.Value, filePath)
            },
            ForStmt fs => fs with
            {
                Span = span,
                Condition = (Expr)StampNode(fs.Condition, filePath),
                Body = (BlockStmt)StampNode(fs.Body, filePath)
            },
            ForRangeStmt fr => fr with
            {
                Span = span,
                Start = (Expr)StampNode(fr.Start, filePath),
                End = (Expr)StampNode(fr.End, filePath),
                Body = (BlockStmt)StampNode(fr.Body, filePath)
            },
            ForEachStmt fe => fe with
            {
                Span = span,
                Body = (BlockStmt)StampNode(fe.Body, filePath)
            },
            ReturnStmt rs => rs with
            {
                Span = span,
                Value = rs.Value is not null ? (Expr)StampNode(rs.Value, filePath) : null
            },
            SendStmt snd => snd with
            {
                Span = span,
                Target = (Expr)StampNode(snd.Target, filePath),
                Arguments = snd.Arguments.Select(a => (NamedArg)StampNode(a, filePath)).ToList()
            },
            SpawnStmt spw => spw with
            {
                Span = span,
                Initializers = spw.Initializers.Select(a => (NamedArg)StampNode(a, filePath)).ToList()
            },
            DestroyStmt dst => dst with
            {
                Span = span,
                Target = (Expr)StampNode(dst.Target, filePath)
            },
            ExprStmt es => es with
            {
                Span = span,
                Expression = (Expr)StampNode(es.Expression, filePath)
            },
            VecLiteralExpr vl => vl with
            {
                Span = span,
                Components = vl.Components.Select(c => (Expr)StampNode(c, filePath)).ToList()
            },
            UnaryExpr ue => ue with
            {
                Span = span,
                Operand = (Expr)StampNode(ue.Operand, filePath)
            },
            BinaryExpr be => be with
            {
                Span = span,
                Left = (Expr)StampNode(be.Left, filePath),
                Right = (Expr)StampNode(be.Right, filePath)
            },
            TernaryExpr te => te with
            {
                Span = span,
                Condition = (Expr)StampNode(te.Condition, filePath),
                WhenTrue = (Expr)StampNode(te.WhenTrue, filePath),
                WhenFalse = (Expr)StampNode(te.WhenFalse, filePath)
            },
            CallExpr ce => ce with
            {
                Span = span,
                Callee = (Expr)StampNode(ce.Callee, filePath),
                Arguments = ce.Arguments.Select(a => (Expr)StampNode(a, filePath)).ToList()
            },
            FieldAccessExpr fae => fae with
            {
                Span = span,
                Target = (Expr)StampNode(fae.Target, filePath)
            },
            IndexAccessExpr iae => iae with
            {
                Span = span,
                Target = (Expr)StampNode(iae.Target, filePath),
                Index = (Expr)StampNode(iae.Index, filePath)
            },
            LambdaParam lp => lp with
            {
                Span = span,
                ExplicitType = lp.ExplicitType is not null ? (TypeRef)StampNode(lp.ExplicitType, filePath) : null
            },
            LambdaExpr le => le with
            {
                Span = span,
                Parameters = le.Parameters.Select(p => (LambdaParam)StampNode(p, filePath)).ToList(),
                BodyExpr = le.BodyExpr is not null ? (Expr)StampNode(le.BodyExpr, filePath) : null,
                BodyStmt = le.BodyStmt is not null ? (Stmt)StampNode(le.BodyStmt, filePath) : null
            },
            _ => node with { Span = span }
        };
    }

    private sealed class SpanTrackingParser<T> : Parser<T> where T : AstNode
    {
        private readonly Parser<T> _inner;

        public SpanTrackingParser(Parser<T> inner)
        {
            _inner = inner;
        }

        public override bool Parse(ParseContext context, ref ParseResult<T> result)
        {
            context.SkipWhiteSpace();
            var start = context.Scanner.Cursor.Position;
            if (!_inner.Parse(context, ref result))
                return false;

            var end = context.Scanner.Cursor.Position;
            int len = ComputeSpanLength(context.Scanner.Buffer, start.Offset, end.Offset, start.Line, end.Line);
            if (result.Value is not null)
            {
                result.Value = result.Value with { Span = new SourceSpan(start.Line, start.Column, len) };
            }
            return true;
        }

        private static int ComputeSpanLength(string buffer, int startOffset, int endOffset, int startLine, int endLine)
        {
            if (startOffset < 0 || startOffset >= buffer.Length)
                return 1;

            if (endLine == startLine && endOffset > startOffset)
                return endOffset - startOffset;

            int maxEnd = Math.Min(buffer.Length, endOffset > startOffset ? endOffset : buffer.Length);
            int pos = startOffset;
            while (pos < maxEnd && buffer[pos] != '\r' && buffer[pos] != '\n' && buffer[pos] != '{')
            {
                pos++;
            }
            while (pos > startOffset && char.IsWhiteSpace(buffer[pos - 1]))
            {
                pos--;
            }
            return Math.Max(1, pos - startOffset);
        }
    }

    private static Parser<T> WithSpan<T>(Parser<T> parser) where T : AstNode =>
        new SpanTrackingParser<T>(parser);

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

        var expr = Deferred<Expr>();
        var typeRef = Deferred<TypeRef>();
        var stmt = Deferred<Stmt>();
        var blockStmt = Deferred<BlockStmt>();
        var lambdaBodyStmt = Deferred<Stmt>();

        // ref<Enemy>
        var actorRefType = Keyword("ref")
            .SkipAnd(Terms.Char('<'))
            .SkipAnd(identifier.ElseError("Expected actor type name inside ref<...>"))
            .AndSkip(Terms.Char('>').ElseError("Expected '>' closing ref type"))
            .Then<TypeRef>(static (context, actorName) => new ActorRefTypeRef(actorName));

        // [32]Bullet or [MAX_TILES]u8
        var fixedArrayType = Between(
                Terms.Char('['),
                expr.ElseError("Expected constant integer expression inside '[...]'"),
                Terms.Char(']').ElseError("Expected ']' closing fixed array length")
            )
            .And(typeRef)
            .Then<TypeRef>(static (context, x) => new FixedArrayTypeRef(x.Item1, x.Item2));

        // fn(f32, f32) -> f32  or  fn()  (shorthand for fn() -> void)
        var functionTypeRef = Keyword("fn")
            .SkipAnd(Between(
                Terms.Char('(').ElseError("Expected '(' after 'fn' in function type"),
                Separated(Terms.Char(','), typeRef, min: 0),
                Terms.Char(')').ElseError("Expected ')' closing function type parameters")
            ))
            .And(Terms.Text("->").SkipAnd(typeRef).Else(new BuiltinTypeRef("void")))
            .Then<TypeRef>(static (context, x) => new FunctionTypeRef(x.Item1, x.Item2));

        // Built-in or user-defined type name
        var namedOrBuiltinType = rawIdentifier
            .Then<TypeRef>(static (context, name) => BuiltinTypes.Contains(name)
                ? new BuiltinTypeRef(name)
                : new NamedTypeRef(name));

        typeRef.Parser = WithSpan(OneOf(actorRefType, fixedArrayType, functionTypeRef, namedOrBuiltinType));

        // ====================================================================
        // Expressions & PSP Literals
        // ====================================================================

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

        // Match patterns (shared by match statements and match expressions)
        var wildcardPattern = Keyword("_")
            .Then<MatchPattern>(static (context, _) => new WildcardPattern());

        var enumVariantPattern = identifier
            .AndSkip(Terms.Char('.'))
            .And(identifier.ElseError("Expected enum variant name after '.'"))
            .Then<MatchPattern>(static (context, x) => new EnumVariantPattern(x.Item1, x.Item2));

        var signedIntPattern = Terms.Char('-').ThenElse(static (_, _) => true, false)
            .And(intLiteral)
            .Then<MatchPattern>(static (context, x) => new IntLiteralPattern(x.Item1 ? -x.Item2 : x.Item2));

        var constPattern = identifier
            .Then<MatchPattern>(static (context, name) => new ConstPattern(name));

        var matchPattern = WithSpan(OneOf(wildcardPattern, enumVariantPattern, signedIntPattern, constPattern));

        var matchPatternList = Separated(
            Not(Terms.Text("||")).SkipAnd(Terms.Char('|')),
            matchPattern,
            min: 1
        );

        var matchExprArm = WithSpan(matchPatternList
            .AndSkip(Terms.Text("=>").ElseError("Expected '=>' after match pattern"))
            .And(expr)
            .AndSkip(ZeroOrOne(Terms.Char(',')))
            .Then(static (context, x) => new MatchExprArm(x.Item1, x.Item2)));

        var matchExpr = Keyword("match")
            .SkipAnd(expr)
            .And(Between(
                Terms.Char('{'),
                ZeroOrMany(matchExprArm),
                Terms.Char('}').ElseError("Expected '}' closing match expression")
            ))
            .Then<Expr>(static (context, x) => new MatchExpr(x.Item1, x.Item2));

        // Typed game constructors: vec2(x, y), vec3(x, y, z), rect(x, y, w, h)
        var gameTypeCtorExpr = OneOf(
                Keyword("vec2").Then(static (_, _) => "vec2"),
                Keyword("vec3").Then(static (_, _) => "vec3"),
                Keyword("rect").Then(static (_, _) => "rect")
            )
            .And(Between(
                Terms.Char('('),
                Separated(Terms.Char(','), expr, min: 0),
                Terms.Char(')').ElseError("Expected ')' closing constructor arguments")
            ))
            .Then<Expr>(static (context, x) => new CallExpr(new IdentifierExpr(x.Item1), x.Item2));

        // { x, y } or { x, y, z }
        var vecLiteralExpr = Between(
                Terms.Char('{'),
                Separated(Terms.Char(','), expr, min: 2, max: 4),
                Terms.Char('}')
            )
            .Then<Expr>(static (context, list) => new VecLiteralExpr(list));

        // Non-capturing arrow lambda: () => goto Match, (x: f32) => x * 2.0, (a, b) => { ... }
        var lambdaParam = WithSpan(identifier
            .And(Terms.Char(':').SkipAnd(typeRef).ThenElse<TypeRef?>(static (context, t) => t, null))
            .Then(static (context, x) => new LambdaParam(x.Item1, x.Item2)));

        var lambdaBody = OneOf(
            blockStmt.Then<(Expr? Expr, Stmt? Stmt)>(static (context, b) => (null, b)),
            lambdaBodyStmt.Then<(Expr? Expr, Stmt? Stmt)>(static (context, s) => (null, s)),
            expr.Then<(Expr? Expr, Stmt? Stmt)>(static (context, e) => (e, null))
        );

        var lambdaExpr = Between(
                Terms.Char('('),
                Separated(Terms.Char(','), lambdaParam, min: 0),
                Terms.Char(')')
            )
            .AndSkip(Terms.Text("=>"))
            .And(lambdaBody.ElseError("Expected lambda body after '=>'"))
            .Then<Expr>(static (context, x) => new LambdaExpr(x.Item1, x.Item2.Expr, x.Item2.Stmt));

        var primaryExpr = WithSpan(OneOf(
            hexColorExpr,
            numericExpr,
            strExpr,
            boolExpr,
            nullExpr,
            selfExpr,
            matchExpr,
            gameTypeCtorExpr,
            vecLiteralExpr,
            lambdaExpr,
            Between(Terms.Char('('), expr, Terms.Char(')').ElseError("Expected ')'")),
            idExpr
        ));

        // Postfix suffixes: call `(...)`, field `.x`, index `[i]`
        var callSuffix = Between(
                Terms.Char('('),
                Separated(Terms.Char(','), expr, min: 0),
                Terms.Char(')').ElseError("Expected ')' after arguments")
            )
            .Then<Func<Expr, Expr>>(static (context, args) => target => new CallExpr(target, args) { Span = target.Span });

        var fieldSuffix = Not(Terms.Text(".."))
            .SkipAnd(Terms.Char('.'))
            .SkipAnd(rawIdentifier.ElseError("Expected field or method name after '.'"))
            .Then<Func<Expr, Expr>>(static (context, field) => target => new FieldAccessExpr(target, field) { Span = target.Span });

        var indexSuffix = Between(
                Terms.Char('['),
                expr,
                Terms.Char(']').ElseError("Expected ']' after index expression")
            )
            .Then<Func<Expr, Expr>>(static (context, idx) => target => new IndexAccessExpr(target, idx) { Span = target.Span });

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

        // Unary operators: `-x`, `!flag`, `~mask`
        var unaryExpr = Deferred<Expr>();
        unaryExpr.Parser = WithSpan(OneOf(
            Terms.Char('-').SkipAnd(unaryExpr).Then<Expr>(static (context, operand) => new UnaryExpr(UnaryOp.Negate, operand)),
            Terms.Char('!').SkipAnd(unaryExpr).Then<Expr>(static (context, operand) => new UnaryExpr(UnaryOp.Not, operand)),
            Terms.Char('~').SkipAnd(unaryExpr).Then<Expr>(static (context, operand) => new UnaryExpr(UnaryOp.BitNot, operand)),
            postfixExpr
        ));

        // Precedence climbing (highest to lowest, matching C99)
        var factorExpr = unaryExpr.LeftAssociative(
            (Terms.Text("*"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Mul, b) { Span = a.Span }),
            (Terms.Text("/"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Div, b) { Span = a.Span }),
            (Terms.Text("%"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Mod, b) { Span = a.Span })
        );

        var termExpr = factorExpr.LeftAssociative(
            (Terms.Text("+"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Add, b) { Span = a.Span }),
            (Terms.Text("-"), static (context, a, b) => new BinaryExpr(a, BinaryOp.Sub, b) { Span = a.Span })
        );

        var shiftExpr = termExpr.LeftAssociative(
            (Terms.Text("<<"), static (context, a, b) => new BinaryExpr(a, BinaryOp.ShiftLeft, b) { Span = a.Span }),
            (Terms.Text(">>"), static (context, a, b) => new BinaryExpr(a, BinaryOp.ShiftRight, b) { Span = a.Span })
        );

        var comparisonExpr = shiftExpr.LeftAssociative(
            (Terms.Text("<="), static (context, a, b) => new BinaryExpr(a, BinaryOp.LessEqual, b) { Span = a.Span }),
            (Terms.Text(">="), static (context, a, b) => new BinaryExpr(a, BinaryOp.GreaterEqual, b) { Span = a.Span }),
            (Terms.Text("<"),  static (context, a, b) => new BinaryExpr(a, BinaryOp.Less, b) { Span = a.Span }),
            (Terms.Text(">"),  static (context, a, b) => new BinaryExpr(a, BinaryOp.Greater, b) { Span = a.Span })
        );

        var equalityExpr = comparisonExpr.LeftAssociative(
            (Terms.Text("=="), static (context, a, b) => new BinaryExpr(a, BinaryOp.Equal, b) { Span = a.Span }),
            (Terms.Text("!="), static (context, a, b) => new BinaryExpr(a, BinaryOp.NotEqual, b) { Span = a.Span })
        );

        var bitAndExpr = equalityExpr.LeftAssociative(
            (Not(Terms.Text("&&")).SkipAnd(Terms.Text("&")), static (context, a, b) => new BinaryExpr(a, BinaryOp.BitAnd, b) { Span = a.Span })
        );

        var bitXorExpr = bitAndExpr.LeftAssociative(
            (Terms.Text("^"), static (context, a, b) => new BinaryExpr(a, BinaryOp.BitXor, b) { Span = a.Span })
        );

        var bitOrExpr = bitXorExpr.LeftAssociative(
            (Not(Terms.Text("||")).SkipAnd(Terms.Text("|")), static (context, a, b) => new BinaryExpr(a, BinaryOp.BitOr, b) { Span = a.Span })
        );

        var logicAndExpr = bitOrExpr.LeftAssociative(
            (Terms.Text("&&"), static (context, a, b) => new BinaryExpr(a, BinaryOp.LogicalAnd, b) { Span = a.Span })
        );

        var logicOrExpr = logicAndExpr.LeftAssociative(
            (Terms.Text("||"), static (context, a, b) => new BinaryExpr(a, BinaryOp.LogicalOr, b) { Span = a.Span })
        );

        var ternaryExpr = Deferred<Expr>();
        ternaryExpr.Parser = logicOrExpr
            .And(
                Terms.Char('?')
                    .SkipAnd(expr)
                    .AndSkip(Terms.Char(':').ElseError("Expected ':' in ternary conditional expression"))
                    .And(ternaryExpr)
                    .ThenElse<(Expr? Then, Expr? Else)>(static (context, x) => (x.Item1, x.Item2), default)
            )
            .Then<Expr>(static (context, x) => x.Item2.Then is not null && x.Item2.Else is not null
                ? new TernaryExpr(x.Item1, x.Item2.Then, x.Item2.Else) { Span = x.Item1.Span }
                : x.Item1);

        expr.Parser = ternaryExpr;

        // ====================================================================
        // Shared Helpers (NamedArgs, Fields, Params, Blocks)
        // ====================================================================

        // amount: 25
        var namedArg = WithSpan(rawIdentifier
            .AndSkip(Terms.Char(':'))
            .And(expr)
            .Then(static (context, x) => new NamedArg(x.Item1, x.Item2)));

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

        // pos: vec2 = { 0.0, 0.0 }  OR  frame = 0  OR  pos = vec2(240.0, 136.0)
        var typedFieldDecl = rawIdentifier
            .AndSkip(Terms.Char(':'))
            .And(typeRef)
            .And(optionalInitializer)
            .AndSkip(ZeroOrOne(Terms.Char(',')))
            .Then(static (context, x) => new FieldDecl(x.Item1, x.Item2, x.Item3));

        var inferredFieldDecl = rawIdentifier
            .AndSkip(Terms.Char('='))
            .And(expr)
            .AndSkip(ZeroOrOne(Terms.Char(',')))
            .Then(static (context, x) => new FieldDecl(x.Item1, null, x.Item2));

        var fieldDecl = WithSpan(OneOf(typedFieldDecl, inferredFieldDecl));

        // dt: f32
        var paramDecl = WithSpan(identifier
            .AndSkip(Terms.Char(':'))
            .And(typeRef)
            .Then(static (context, x) => new ParamDecl(x.Item1, x.Item2)));

        var paramList = Separated(Terms.Char(','), paramDecl, min: 0);

        // ====================================================================
        // Statements & Blocks
        // ====================================================================

        blockStmt.Parser = WithSpan(Between(
                Terms.Char('{'),
                ZeroOrMany(stmt),
                Terms.Char('}').ElseError("Expected '}' at the end of block")
            )
            .Then(static (context, stmts) => new BlockStmt(stmts)));

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

        var localConstStmt = Keyword("const")
            .SkipAnd(identifier.ElseError("Expected constant name after 'const'"))
            .And(optionalTypeAnnotation)
            .AndSkip(Terms.Char('=').ElseError("Expected '=' in constant declaration"))
            .And(expr)
            .Then<Stmt>(static (context, x) => new LocalConstStmt(
                Name: x.Item1,
                ExplicitType: x.Item2,
                Initializer: x.Item3
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

        // match scrutinee { Pattern | Pattern => stmt_or_block, _ => ... }
        var matchArm = WithSpan(matchPatternList
            .AndSkip(Terms.Text("=>").ElseError("Expected '=>' after match pattern"))
            .And(OneOf(blockStmt.Then<Stmt>(static (context, b) => b), stmt))
            .AndSkip(ZeroOrOne(Terms.Char(',')))
            .Then(static (context, x) => new MatchArm(x.Item1, x.Item2)));

        var matchStmt = Keyword("match")
            .SkipAnd(expr)
            .And(Between(
                Terms.Char('{'),
                ZeroOrMany(matchArm),
                Terms.Char('}').ElseError("Expected '}' closing match statement")
            ))
            .Then<Stmt>(static (context, x) => new MatchStmt(x.Item1, x.Item2));

        // for i in start..end { ... } or for i in start..=end { ... }
        var rangeOp = Terms.Text("..=").Then(static (context, _) => true)
            .Or(Terms.Text("..").Then(static (context, _) => false))
            .ElseError("Expected '..' or '..=' in range 'for' loop");

        var forRangeStmt = Keyword("for")
            .SkipAnd(identifier)
            .AndSkip(Keyword("in"))
            .And(expr)
            .And(rangeOp)
            .And(expr)
            .And(blockStmt)
            .Then<Stmt>(static (context, x) => new ForRangeStmt(x.Item1, x.Item2, x.Item4, x.Item5, IsInclusive: x.Item3));

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

        // break / continue
        var breakStmt = Keyword("break")
            .Then<Stmt>(static (context, _) => new BreakStmt());

        var continueStmt = Keyword("continue")
            .Then<Stmt>(static (context, _) => new ContinueStmt());

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
            Terms.Text("<<=").Then(static (context, _) => AssignOp.ShiftLeftAssign),
            Terms.Text(">>=").Then(static (context, _) => AssignOp.ShiftRightAssign),
            Terms.Text("+=").Then(static (context, _) => AssignOp.AddAssign),
            Terms.Text("-=").Then(static (context, _) => AssignOp.SubAssign),
            Terms.Text("*=").Then(static (context, _) => AssignOp.MulAssign),
            Terms.Text("/=").Then(static (context, _) => AssignOp.DivAssign),
            Terms.Text("&=").Then(static (context, _) => AssignOp.BitAndAssign),
            Terms.Text("|=").Then(static (context, _) => AssignOp.BitOrAssign),
            Terms.Text("^=").Then(static (context, _) => AssignOp.BitXorAssign),
            Terms.Text("=").Then(static (context, _) => AssignOp.Assign)
        );

        var assignOrExprStmt = postfixExpr
            .And(assignOp.And(expr).ThenElse<(AssignOp Op, Expr? Value)>(static (context, x) => (x.Item1, x.Item2), default))
            .Then<Stmt>(static (context, x) => x.Item2.Value is not null
                ? new AssignStmt(x.Item1, x.Item2.Op, x.Item2.Value)
                : new ExprStmt(x.Item1));

        var strictAssignStmt = postfixExpr
            .And(assignOp)
            .And(expr)
            .Then<Stmt>(static (context, x) => new AssignStmt(x.Item1, x.Item2, x.Item3));

        lambdaBodyStmt.Parser = WithSpan(OneOf(
            ifStmt.Then<Stmt>(static (context, i) => i),
            forRangeStmt,
            forCondStmt,
            forEachStmt,
            returnStmt,
            sendStmt,
            spawnStandalone,
            destroyStmt,
            transitionStmt,
            strictAssignStmt
        ));

        stmt.Parser = WithSpan(OneOf(
            spawnWithBinding,
            localConstStmt,
            localVarStmt,
            ifStmt.Then<Stmt>(static (context, i) => i),
            matchStmt,
            forRangeStmt,
            forCondStmt,
            forEachStmt,
            breakStmt,
            continueStmt,
            returnStmt,
            sendStmt,
            spawnStandalone,
            destroyStmt,
            transitionStmt,
            assignOrExprStmt
        ));

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

        var lifecycleHook = WithSpan(lifecycleKind
            .And(Between(Terms.Char('('), paramList, Terms.Char(')')))
            .And(blockStmt)
            .Then(static (context, x) => new LifecycleHook(x.Item1, x.Item2, x.Item3)));

        // Message handler: on Hit(msg) { ... }
        var messageHandler = WithSpan(Keyword("on")
            .SkipAnd(identifier.ElseError("Expected message type after 'on'"))
            .And(Between(
                Terms.Char('('),
                identifier.ElseError("Expected parameter name inside message handler"),
                Terms.Char(')')
            ))
            .And(blockStmt)
            .Then(static (context, x) => new MessageHandler(x.Item1, x.Item2, x.Item3)));

        // Function / Method: fn foo(a: int) -> bool { ... }
        var funcDecl = WithSpan(Keyword("fn")
            .SkipAnd(identifier.ElseError("Expected function name after 'fn'"))
            .And(Between(Terms.Char('('), paramList, Terms.Char(')')))
            .And(Terms.Text("->").SkipAnd(typeRef).Else(new BuiltinTypeRef("void")))
            .And(blockStmt)
            .Then(static (context, x) => new FuncDecl(
                Name: x.Item1,
                Parameters: x.Item2,
                ReturnType: x.Item3,
                Body: x.Item4
            )));

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

        var boolConfigValue = Keyword("true").Then<ConfigValue>(static (context, _) => new BoolConfigValue(true))
            .Or(Keyword("false").Then<ConfigValue>(static (context, _) => new BoolConfigValue(false)));

        var configValue = OneOf(
            stringLiteral.Then<ConfigValue>(static (context, s) => new StringConfigValue(s)),
            sizeOrIntConfig,
            boolConfigValue,
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

        // Enum: enum Season { Spring, Summer, Fall, Winter }
        var enumDecl = Keyword("enum")
            .SkipAnd(identifier.ElseError("Expected enum name after 'enum'"))
            .And(Between(
                Terms.Char('{'),
                Separated(Terms.Char(','), identifier, min: 0).AndSkip(ZeroOrOne(Terms.Char(','))),
                Terms.Char('}').ElseError("Expected '}' closing enum declaration")
            ))
            .Then<TopDecl>(static (context, x) => new EnumDecl(x.Item1, x.Item2));

        // Shared member parser for Actor and State blocks
        var containerMember = OneOf(
            lifecycleHook.Then<object>(static (context, h) => h),
            messageHandler.Then<object>(static (context, m) => m),
            funcDecl.Then<object>(static (context, fn) => fn),
            fieldDecl.Then<object>(static (context, f) => f)
        );

        var poolCapacity = Between(Terms.Char('['), expr, Terms.Char(']').ElseError("Expected ']' closing actor pool capacity"))
            .ThenElse<Expr?>(static (context, cap) => cap, null);

        // Actor: actor Enemy[32] { ... } or actor Enemy[MAX_ENEMIES] { ... }
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
                int? literalCap = x.Item2 is IntLiteralExpr il ? il.Value : null;
                return new ActorDecl(
                    Name: x.Item1,
                    PoolCapacity: literalCap,
                    Fields: members.OfType<FieldDecl>().ToList(),
                    Hooks: members.OfType<LifecycleHook>().ToList(),
                    Handlers: members.OfType<MessageHandler>().ToList(),
                    Methods: members.OfType<FuncDecl>().ToList(),
                    PoolCapacityExpr: x.Item2
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

        // Top-level compile-time constant: const MAX_ENEMIES = 32
        var constDecl = Keyword("const")
            .SkipAnd(identifier.ElseError("Expected constant name after 'const'"))
            .And(optionalTypeAnnotation)
            .AndSkip(Terms.Char('=').ElseError("Expected '=' in constant declaration"))
            .And(expr)
            .Then<TopDecl>(static (context, x) => new ConstDecl(
                Name: x.Item1,
                ExplicitType: x.Item2,
                Initializer: x.Item3
            ));

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

        var topDecl = WithSpan(OneOf(
            importDecl,
            gameConfigDecl,
            assetDecl,
            constDecl,
            messageDecl,
            structDecl,
            enumDecl,
            actorDecl,
            stateDecl,
            globalVarDecl,
            funcDecl.Then<TopDecl>(static (context, fn) => fn)
        ));

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
