using PspScript.Ast;

namespace PspScript.Semantics;


public sealed class SemanticAnalyzer
{
    private readonly SymbolTable _symbols = new();
    private readonly SemanticModel _model;
    private readonly List<SemanticDiagnostic> _diagnostics = [];

    public SemanticAnalyzer()
    {
        _model = new SemanticModel(_symbols);
    }

    public static SemanticAnalysisResult Analyze(ProgramNode program)
    {
        var analyzer = new SemanticAnalyzer();
        return analyzer.Run(program);
    }

    public SemanticAnalysisResult Run(ProgramNode program)
    {
        // Pass 1: Collect all top-level symbols & resolve signatures
        CollectTopLevelDeclarations(program);
        ResolveGameEntryState();
        ResolveTopLevelSignatures(program);
        CheckStructCycles();

        // Pass 2: Validate initializers, bodies, statements, expressions & memory budget
        ValidateTopLevelInitializersAndBodies(program);
        ValidateStaticMemoryBudget();

        bool hasErrors = _diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error);
        return new SemanticAnalysisResult(
            Success: !hasErrors,
            Model: _model,
            Diagnostics: _diagnostics
        );
    }

    private void ReportError(string message, SourceSpan span = default)
    {
        _diagnostics.Add(new SemanticDiagnostic(message, span, DiagnosticSeverity.Error));
    }

    private void ReportWarning(string message, SourceSpan span = default)
    {
        _diagnostics.Add(new SemanticDiagnostic(message, span, DiagnosticSeverity.Warning));
    }

    // ========================================================================
    // Pre-register Top-Level Type & Container Names
    // ========================================================================

    private void CollectTopLevelDeclarations(ProgramNode program)
    {
        foreach (var decl in program.Declarations)
        {
            switch (decl)
            {
                case GameConfigDecl gameConfig:
                    if (_symbols.GameConfig is not null)
                    {
                        ReportError("Multiple 'game' configuration blocks are not allowed.", gameConfig.Span);
                    }
                    else
                    {
                        _symbols.GameConfig = gameConfig;
                        ExtractGameConfigSettings(gameConfig);
                    }
                    break;

                case AssetDecl asset:
                    if (!RegisterUniqueTopLevel(asset.Name, asset))
                        break;

                    var assetType = new AssetType(asset.AssetKind);
                    _symbols.Assets[asset.Name] = new AssetSymbol(
                        asset.Name,
                        asset.AssetKind,
                        asset.FilePath,
                        assetType,
                        asset
                    );
                    _model.RecordType(asset, assetType);
                    break;

                case MessageDecl msg:
                    if (!RegisterUniqueTopLevel(msg.Name, msg))
                        break;

                    _symbols.Messages[msg.Name] = new MessageSymbol(
                        msg.Name,
                        new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                        msg
                    );
                    break;

                case StructDecl st:
                    if (!RegisterUniqueTopLevel(st.Name, st))
                        break;

                    _symbols.Structs[st.Name] = new StructSymbol(
                        st.Name,
                        new StructType(st.Name),
                        new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                        st
                    );
                    break;

                case EnumDecl enumDecl:
                    if (!RegisterUniqueTopLevel(enumDecl.Name, enumDecl))
                        break;

                    if (enumDecl.Variants.Count == 0)
                    {
                        ReportError($"Enum '{enumDecl.Name}' must declare at least one variant.", enumDecl.Span);
                    }

                    var uniqueVariants = new List<string>(enumDecl.Variants.Count);
                    var seenVariants = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var variant in enumDecl.Variants)
                    {
                        if (!seenVariants.Add(variant))
                        {
                            ReportError($"Duplicate variant '{variant}' in enum '{enumDecl.Name}'.", enumDecl.Span);
                        }
                        else
                        {
                            uniqueVariants.Add(variant);
                        }
                    }

                    var enumType = new EnumType(enumDecl.Name);
                    _symbols.Enums[enumDecl.Name] = new EnumSymbol(
                        enumDecl.Name,
                        enumType,
                        uniqueVariants,
                        enumDecl
                    );
                    _model.RecordType(enumDecl, enumType);
                    break;

                case ActorDecl actor:
                    if (!RegisterUniqueTopLevel(actor.Name, actor))
                        break;

                    if (actor.PoolCapacity is <= 0)
                    {
                        ReportError(
                            $"Actor '{actor.Name}' pool capacity must be a positive integer, got {actor.PoolCapacity}.",
                            actor.Span
                        );
                    }

                    _symbols.Actors[actor.Name] = new ActorSymbol(
                        actor.Name,
                        actor.PoolCapacity,
                        new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                        new Dictionary<LifecycleKind, LifecycleHook>(),
                        new Dictionary<string, MessageHandler>(StringComparer.Ordinal),
                        new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal),
                        actor
                    );
                    break;

                case StateDecl state:
                    if (!RegisterUniqueTopLevel(state.Name, state))
                        break;

                    _symbols.States[state.Name] = new StateSymbol(
                        state.Name,
                        new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                        new Dictionary<LifecycleKind, LifecycleHook>(),
                        new Dictionary<string, MessageHandler>(StringComparer.Ordinal),
                        new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal),
                        state
                    );
                    break;
            }
        }
    }

    private void ExtractGameConfigSettings(GameConfigDecl gameConfig)
    {
        if (gameConfig.Settings.TryGetValue("ram_budget", out var ramSetting))
        {
            switch (ramSetting)
            {
                case SizeConfigValue sizeVal:
                    if (sizeVal.Bytes <= 0)
                        ReportError("'ram_budget' must be greater than 0 bytes.", gameConfig.Span);
                    else
                        _symbols.ConfiguredRamBudgetBytes = sizeVal.Bytes;
                    break;

                case IntConfigValue intVal:
                    if (intVal.Value <= 0)
                        ReportError("'ram_budget' must be greater than 0.", gameConfig.Span);
                    else
                        _symbols.ConfiguredRamBudgetBytes = intVal.Value;
                    break;

                default:
                    ReportError("'ram_budget' in game config must be a size literal (e.g., 16MB) or integer.", gameConfig.Span);
                    break;
            }
        }
    }

    private void ResolveGameEntryState()
    {
        if (_symbols.GameConfig is not null &&
            _symbols.GameConfig.Settings.TryGetValue("entry", out var entrySetting))
        {
            string? entryName = entrySetting switch
            {
                IdentConfigValue identVal => identVal.Name,
                StringConfigValue strVal => strVal.Value,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(entryName))
            {
                ReportError(
                    "'entry' in game config must be a state name (e.g., entry: Match or entry: \"Match\").",
                    _symbols.GameConfig.Span
                );
                return;
            }

            if (!_symbols.States.ContainsKey(entryName))
            {
                ReportError(
                    $"Game config 'entry' references undeclared state '{entryName}'.",
                    _symbols.GameConfig.Span
                );
                return;
            }

            _symbols.EntryStateName = entryName;
            return;
        }

        // Default to the first declared state if 'entry' is omitted
        _symbols.EntryStateName = _symbols.States.Values.FirstOrDefault()?.Name;
    }

    private bool RegisterUniqueTopLevel(string name, TopDecl decl)
    {
        if (!_symbols.TryRegisterTopLevelName(name, decl))
        {
            ReportError($"Duplicate top-level declaration '{name}'.", decl.Span);
            return false;
        }
        return true;
    }

    // ========================================================================
    // Resolve TypeRefs, Fields, Signatures & Handlers
    // ========================================================================

    private void ResolveTopLevelSignatures(ProgramNode program)
    {
        foreach (var decl in program.Declarations)
        {
            switch (decl)
            {
                case MessageDecl msg when _symbols.Messages.TryGetValue(msg.Name, out var msgSym):
                    var msgFields = (Dictionary<string, FieldSymbol>)msgSym.Fields;
                    ResolveFieldList(msg.Fields, msgFields, $"message '{msg.Name}'");
                    break;

                case StructDecl st when _symbols.Structs.TryGetValue(st.Name, out var stSym):
                    var stFields = (Dictionary<string, FieldSymbol>)stSym.Fields;
                    ResolveFieldList(st.Fields, stFields, $"struct '{st.Name}'");
                    break;

                case ActorDecl actor when _symbols.Actors.TryGetValue(actor.Name, out var actorSym):
                    ResolveFieldList(actor.Fields, actorSym.Fields, $"actor '{actor.Name}'");
                    ResolveContainerMembers(
                        containerKind: $"actor '{actor.Name}'",
                        hooks: actor.Hooks,
                        handlers: actor.Handlers,
                        methods: actor.Methods,
                        targetHooks: actorSym.Hooks,
                        targetHandlers: actorSym.Handlers,
                        targetMethods: actorSym.Methods
                    );
                    break;

                case StateDecl state when _symbols.States.TryGetValue(state.Name, out var stateSym):
                    ResolveFieldList(state.Fields, stateSym.Fields, $"state '{state.Name}'");
                    ResolveContainerMembers(
                        containerKind: $"state '{state.Name}'",
                        hooks: state.Hooks,
                        handlers: state.Handlers,
                        methods: state.Methods,
                        targetHooks: stateSym.Hooks,
                        targetHandlers: stateSym.Handlers,
                        targetMethods: stateSym.Methods
                    );
                    break;

                case FuncDecl fn:
                    if (!RegisterUniqueTopLevel(fn.Name, fn))
                        break;

                    if (_symbols.Functions.TryGetValue(fn.Name, out var existingFn) && !existingFn.IsBuiltin)
                    {
                        ReportError($"Duplicate function declaration '{fn.Name}'.", fn.Span);
                        break;
                    }

                    var fnSymbol = BuildFunctionSymbol(fn);
                    _symbols.Functions[fn.Name] = fnSymbol;
                    _model.RecordType(fn, fnSymbol.Signature);
                    break;

                case GlobalVarDecl gvar:
                    if (!RegisterUniqueTopLevel(gvar.Name, gvar))
                        break;

                    ResolvedType explicitType = gvar.Explicittype is not null
                        ? ResolveTypeRef(gvar.Explicittype)
                        : ResolvedType.Error; // Will be inferred from initializer in Pass 2 if omitted

                    if (explicitType == ResolvedType.Void)
                    {
                        ReportError($"Global variable '{gvar.Name}' cannot have type 'void'.", gvar.Span);
                        explicitType = ResolvedType.Error;
                    }

                    _symbols.Globals[gvar.Name] = new VariableSymbol(
                        Name: gvar.Name,
                        Scope: gvar.Scope,
                        Type: explicitType,
                        IsReadOnly: false,
                        Declaration: gvar
                    );
                    break;
            }
        }
    }

    private void ResolveFieldList(
        IReadOnlyList<FieldDecl> fields,
        Dictionary<string, FieldSymbol> target,
        string ownerDescription)
    {
        foreach (var field in fields)
        {
            if (target.ContainsKey(field.Name))
            {
                ReportError($"Duplicate field '{field.Name}' in {ownerDescription}.", field.Span);
                continue;
            }

            var resolvedType = ResolveTypeRef(field.Type);
            if (resolvedType == ResolvedType.Void)
            {
                ReportError($"Field '{field.Name}' in {ownerDescription} cannot have type 'void'.", field.Span);
                resolvedType = ResolvedType.Error;
            }

            _model.RecordType(field, resolvedType);
            target[field.Name] = new FieldSymbol(
                Name: field.Name,
                Type: resolvedType,
                HasDefaultValue: field.DefaultValue is not null,
                Declaration: field
            );
        }
    }

    private void ResolveContainerMembers(
        string containerKind,
        IReadOnlyList<LifecycleHook> hooks,
        IReadOnlyList<MessageHandler> handlers,
        IReadOnlyList<FuncDecl> methods,
        Dictionary<LifecycleKind, LifecycleHook> targetHooks,
        Dictionary<string, MessageHandler> targetHandlers,
        Dictionary<string, FunctionSymbol> targetMethods)
    {
        foreach (var hook in hooks)
        {
            if (!targetHooks.TryAdd(hook.Kind, hook))
            {
                ReportError(
                    $"Duplicate lifecycle hook '{hook.Kind.ToString().ToLowerInvariant()}' in {containerKind}.",
                    hook.Span
                );
            }

            foreach (var param in hook.Parameters)
            {
                var paramType = ResolveTypeRef(param.Type);
                _model.RecordType(param, paramType);
            }
        }

        foreach (var handler in handlers)
        {
            if (!_symbols.Messages.ContainsKey(handler.MessageName))
            {
                ReportError(
                    $"Message handler in {containerKind} references undeclared message type '{handler.MessageName}'.",
                    handler.Span
                );
            }

            if (!targetHandlers.TryAdd(handler.MessageName, handler))
            {
                ReportError(
                    $"Duplicate message handler 'on {handler.MessageName}' in {containerKind}.",
                    handler.Span
                );
            }
        }

        foreach (var method in methods)
        {
            if (targetMethods.ContainsKey(method.Name))
            {
                ReportError($"Duplicate method '{method.Name}' in {containerKind}.", method.Span);
                continue;
            }

            var fnSym = BuildFunctionSymbol(method);
            targetMethods[method.Name] = fnSym;
            _model.RecordType(method, fnSym.Signature);
        }
    }

    private FunctionSymbol BuildFunctionSymbol(FuncDecl fn)
    {
        var parameters = new List<ParamSymbol>(fn.Parameters.Count);
        var seenParams = new HashSet<string>(StringComparer.Ordinal);

        foreach (var param in fn.Parameters)
        {
            if (!seenParams.Add(param.Name))
            {
                ReportError($"Duplicate parameter '{param.Name}' in function '{fn.Name}'.", param.Span);
            }

            var paramType = ResolveTypeRef(param.Type);
            if (paramType == ResolvedType.Void)
            {
                ReportError($"Parameter '{param.Name}' in function '{fn.Name}' cannot have type 'void'.", param.Span);
                paramType = ResolvedType.Error;
            }

            _model.RecordType(param, paramType);
            parameters.Add(new ParamSymbol(param.Name, paramType, param));
        }

        var returnType = ResolveTypeRef(fn.ReturnType);
        return new FunctionSymbol(
            Name: fn.Name,
            Parameters: parameters,
            ReturnType: returnType,
            IsBuiltin: false,
            IsVariadic: false,
            Declaration: fn
        );
    }

    private ResolvedType ResolveTypeRef(TypeRef typeRef)
    {
        ResolvedType resolved = typeRef switch
        {
            BuiltinTypeRef builtin => ResolvedType.TryGetBuiltin(builtin.Name)
                ?? ReportAndReturnError($"Unknown built-in type '{builtin.Name}'.", builtin.Span),

            NamedTypeRef named => ResolveNamedType(named),

            ActorRefTypeRef actorRef => _symbols.Actors.ContainsKey(actorRef.ActorName)
                ? new ActorRefType(actorRef.ActorName)
                : ReportAndReturnError(
                    $"Type 'ref<{actorRef.ActorName}>' references undeclared actor '{actorRef.ActorName}'.",
                    actorRef.Span
                ),

            FixedArrayTypeRef arrayRef => ResolveFixedArrayType(arrayRef),

            _ => ReportAndReturnError($"Unsupported type reference '{typeRef}'.", typeRef.Span)
        };

        return _model.RecordType(typeRef, resolved);
    }

    private ResolvedType ResolveNamedType(NamedTypeRef named)
    {
        var builtin = ResolvedType.TryGetBuiltin(named.Name);
        if (builtin is not null)
            return builtin;

        if (_symbols.Structs.TryGetValue(named.Name, out var structSym))
            return structSym.Type;

        if (_symbols.Enums.TryGetValue(named.Name, out var enumSym))
            return enumSym.Type;

        if (_symbols.Actors.ContainsKey(named.Name))
        {
            ReportError(
                $"Actor '{named.Name}' cannot be used as a direct value type; use 'ref<{named.Name}>' instead.",
                named.Span
            );
            return ResolvedType.Error;
        }

        if (_symbols.Messages.ContainsKey(named.Name))
        {
            ReportError(
                $"Message '{named.Name}' cannot be used as a field or variable type outside an 'on {named.Name}' handler.",
                named.Span
            );
            return ResolvedType.Error;
        }

        ReportError($"Undeclared type '{named.Name}'.", named.Span);
        return ResolvedType.Error;
    }

    private ResolvedType ResolveFixedArrayType(FixedArrayTypeRef arrayRef)
    {
        if (arrayRef.Length <= 0)
        {
            ReportError($"Fixed array length must be greater than 0, got {arrayRef.Length}.", arrayRef.Span);
        }

        var elementType = ResolveTypeRef(arrayRef.ElementType);
        if (elementType == ResolvedType.Void)
        {
            ReportError("Fixed array element type cannot be 'void'.", arrayRef.Span);
            elementType = ResolvedType.Error;
        }

        return new FixedArrayType(arrayRef.Length, elementType);
    }

    private void CheckStructCycles()
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var structSym in _symbols.Structs.Values)
        {
            DetectStructCycle(structSym, visiting, visited);
        }
    }

    private void DetectStructCycle(
        StructSymbol current,
        HashSet<string> visiting,
        HashSet<string> visited)
    {
        if (visited.Contains(current.Name))
            return;

        if (!visiting.Add(current.Name))
        {
            ReportError(
                $"Recursive value cycle detected in struct '{current.Name}'. C99 value structs cannot contain themselves.",
                current.Declaration.Span
            );
            return;
        }

        foreach (var field in current.Fields.Values)
        {
            var unwrapped = UnwrapArrayElementType(field.Type);
            if (unwrapped is StructType nested && _symbols.Structs.TryGetValue(nested.Name, out var nestedSym))
            {
                DetectStructCycle(nestedSym, visiting, visited);
            }
        }

        visiting.Remove(current.Name);
        visited.Add(current.Name);
    }

    private static ResolvedType UnwrapArrayElementType(ResolvedType type)
    {
        while (type is FixedArrayType arr)
            type = arr.ElementType;
        return type;
    }

    // ========================================================================
    // Validate Initializers, Bodies, Statements & Expressions
    // ========================================================================

    private void ValidateTopLevelInitializersAndBodies(ProgramNode program)
    {
        var rootScope = new LexicalScope(parent: null);

        foreach (var decl in program.Declarations)
        {
            switch (decl)
            {
                case AssetDecl asset:
                    foreach (var opt in asset.Options)
                    {
                        ResolvedType optType = opt.Value is IdentifierExpr optId &&
                                               !_symbols.Globals.ContainsKey(optId.Name) &&
                                               !_symbols.Assets.ContainsKey(optId.Name)
                            ? _model.RecordType(opt.Value, ResolvedType.String)
                            : ResolveExpr(opt.Value, rootScope, expectedType: null);
                        _model.RecordType(opt, optType);
                    }
                    break;

                case GlobalVarDecl gvar when _symbols.Globals.TryGetValue(gvar.Name, out var gvarSym):
                    ValidateGlobalVarInitializer(gvar, gvarSym, rootScope);
                    break;

                case MessageDecl msg when _symbols.Messages.TryGetValue(msg.Name, out var msgSym):
                    ValidateFieldDefaults(msg.Fields, msgSym.Fields, rootScope, $"message '{msg.Name}'");
                    break;

                case StructDecl st when _symbols.Structs.TryGetValue(st.Name, out var stSym):
                    ValidateFieldDefaults(st.Fields, stSym.Fields, rootScope, $"struct '{st.Name}'");
                    break;

                case ActorDecl actor when _symbols.Actors.TryGetValue(actor.Name, out var actorSym):
                    var actorScope = new LexicalScope(rootScope, enclosingActor: actorSym);
                    ValidateFieldDefaults(actor.Fields, actorSym.Fields, actorScope, $"actor '{actor.Name}'");
                    ValidateContainerBodies(actor.Hooks, actor.Handlers, actor.Methods, actorSym.Methods, actorScope);
                    break;

                case StateDecl state when _symbols.States.TryGetValue(state.Name, out var stateSym):
                    var stateScope = new LexicalScope(rootScope, enclosingState: stateSym);
                    ValidateFieldDefaults(state.Fields, stateSym.Fields, stateScope, $"state '{state.Name}'");
                    ValidateContainerBodies(state.Hooks, state.Handlers, state.Methods, stateSym.Methods, stateScope);
                    break;

                case FuncDecl fn when _symbols.Functions.TryGetValue(fn.Name, out var fnSym):
                    ValidateFunctionBody(fn, fnSym, rootScope);
                    break;
            }
        }
    }

    private void ValidateGlobalVarInitializer(GlobalVarDecl gvar, VariableSymbol gvarSym, LexicalScope scope)
    {
        if (gvar.Explicittype is null && gvar.Initializer is null)
        {
            ReportError(
                $"Global variable '{gvar.Name}' must declare an explicit type or an initializer expression.",
                gvar.Span
            );
            _model.RecordType(gvar, ResolvedType.Error);
            return;
        }

        if (gvar.Initializer is not null)
        {
            var expected = gvar.Explicittype is not null ? gvarSym.Type : null;
            var initType = ResolveExpr(gvar.Initializer, scope, expected);

            if (gvar.Explicittype is null)
            {
                if (initType is NullType || initType == ResolvedType.Void)
                {
                    ReportError(
                        $"Cannot infer type of global variable '{gvar.Name}' from '{initType.DisplayName}'. Specify an explicit type.",
                        gvar.Span
                    );
                    gvarSym.Type = ResolvedType.Error;
                }
                else
                {
                    gvarSym.Type = initType;
                }
            }
            else if (!ResolvedType.IsAssignableTo(initType, gvarSym.Type))
            {
                ReportError(
                    $"Cannot initialize global variable '{gvar.Name}' of type '{gvarSym.Type}' with value of type '{initType}'.",
                    gvar.Span
                );
            }
        }

        _model.RecordType(gvar, gvarSym.Type);
    }

    private void ValidateFieldDefaults(
        IReadOnlyList<FieldDecl> fields,
        IReadOnlyDictionary<string, FieldSymbol> fieldSymbols,
        LexicalScope scope,
        string ownerDescription)
    {
        foreach (var field in fields)
        {
            if (field.DefaultValue is null || !fieldSymbols.TryGetValue(field.Name, out var fieldSym))
                continue;

            var initType = ResolveExpr(field.DefaultValue, scope, expectedType: fieldSym.Type);
            if (!ResolvedType.IsAssignableTo(initType, fieldSym.Type))
            {
                ReportError(
                    $"Default value for field '{field.Name}' in {ownerDescription} has type '{initType}', expected '{fieldSym.Type}'.",
                    field.Span
                );
            }
        }
    }

    private void ValidateContainerBodies(
        IReadOnlyList<LifecycleHook> hooks,
        IReadOnlyList<MessageHandler> handlers,
        IReadOnlyList<FuncDecl> methods,
        IReadOnlyDictionary<string, FunctionSymbol> methodSymbols,
        LexicalScope containerScope)
    {
        foreach (var hook in hooks)
        {
            var hookScope = new LexicalScope(
                containerScope,
                expectedReturnType: ResolvedType.Void
            );

            // Validate lifecycle hook parameter conventions (e.g., update(dt: f32))
            if (hook.Kind == LifecycleKind.Update && hook.Parameters.Count == 1)
            {
                var dtType = _model.GetTypeOrDefault(hook.Parameters[0]);
                if (!ResolvedType.IsAssignableTo(dtType, ResolvedType.F32))
                {
                    ReportError(
                        $"Lifecycle hook 'update' parameter '{hook.Parameters[0].Name}' must be compatible with 'f32', got '{dtType}'.",
                        hook.Parameters[0].Span
                    );
                }
            }

            foreach (var param in hook.Parameters)
            {
                var paramType = _model.GetTypeOrDefault(param);
                if (!hookScope.TryDeclareLocal(new VariableSymbol(param.Name, MemoryScope.Stack, paramType, Declaration: param)))
                {
                    ReportError($"Duplicate parameter '{param.Name}' in '{hook.Kind.ToString().ToLowerInvariant()}' hook.", param.Span);
                }
            }

            _model.RecordType(hook, ResolvedType.Void);
            ValidateBlockStmt(hook.Body, hookScope, createChildScope: false);
        }

        foreach (var handler in handlers)
        {
            var handlerScope = new LexicalScope(
                containerScope,
                expectedReturnType: ResolvedType.Void
            );

            ResolvedType msgParamType = _symbols.Messages.ContainsKey(handler.MessageName)
                ? new MessageType(handler.MessageName)
                : ResolvedType.Error;

            handlerScope.TryDeclareLocal(
                new VariableSymbol(
                    Name: handler.ParamName,
                    Scope: MemoryScope.Stack,
                    Type: msgParamType,
                    IsReadOnly: true,
                    Declaration: handler
                )
            );

            _model.RecordType(handler, msgParamType);
            ValidateBlockStmt(handler.Body, handlerScope, createChildScope: false);
        }

        foreach (var method in methods)
        {
            if (methodSymbols.TryGetValue(method.Name, out var methodSym))
            {
                ValidateFunctionBody(method, methodSym, containerScope);
            }
        }
    }

    private void ValidateFunctionBody(FuncDecl fn, FunctionSymbol fnSym, LexicalScope parentScope)
    {
        var fnScope = new LexicalScope(
            parentScope,
            expectedReturnType: fnSym.ReturnType
        );

        foreach (var param in fnSym.Parameters)
        {
            fnScope.TryDeclareLocal(
                new VariableSymbol(
                    Name: param.Name,
                    Scope: MemoryScope.Stack,
                    Type: param.Type,
                    IsReadOnly: false,
                    Declaration: param.Declaration
                )
            );
        }

        ValidateBlockStmt(fn.Body, fnScope, createChildScope: false);

        if (fnSym.ReturnType != ResolvedType.Void &&
            !fnSym.ReturnType.IsError &&
            !BlockDefinitelyReturns(fn.Body))
        {
            ReportError(
                $"Function '{fn.Name}' declares return type '{fnSym.ReturnType}', but not all code paths return a value.",
                fn.Span
            );
        }
    }

    private static bool BlockDefinitelyReturns(BlockStmt block) =>
        block.Statements.Any(StmtDefinitelyReturns);

    private static bool StmtDefinitelyReturns(Stmt stmt) => stmt switch
    {
        ReturnStmt => true,
        BlockStmt blk => BlockDefinitelyReturns(blk),
        IfStmt { ElseBranch: not null } ifStmt =>
            BlockDefinitelyReturns(ifStmt.ThenBranch) && StmtDefinitelyReturns(ifStmt.ElseBranch),
        MatchStmt { Arms.Count: > 0 } matchStmt =>
            matchStmt.Arms.All(static arm => StmtDefinitelyReturns(arm.Body)),
        _ => false
    };

    // ========================================================================
    // Statement Validation
    // ========================================================================

    private void ValidateBlockStmt(BlockStmt block, LexicalScope parentScope, bool createChildScope = true)
    {
        var scope = createChildScope ? parentScope.CreateChild() : parentScope;
        _model.RecordType(block, ResolvedType.Void);

        foreach (var stmt in block.Statements)
        {
            ValidateStmt(stmt, scope);
        }
    }

    private void ValidateStmt(Stmt stmt, LexicalScope scope)
    {
        switch (stmt)
        {
            case BlockStmt block:
                ValidateBlockStmt(block, scope, createChildScope: true);
                break;

            case LocalVarStmt localVar:
                ValidateLocalVarStmt(localVar, scope);
                break;

            case AssignStmt assign:
                ValidateAssignStmt(assign, scope);
                break;

            case IfStmt ifStmt:
                ValidateIfStmt(ifStmt, scope);
                break;

            case MatchStmt matchStmt:
                ValidateMatchStmt(matchStmt, scope);
                break;

            case ForStmt forStmt:
                ValidateForStmt(forStmt, scope);
                break;

            case ForRangeStmt forRangeStmt:
                ValidateForRangeStmt(forRangeStmt, scope);
                break;

            case ForEachStmt forEachStmt:
                ValidateForEachStmt(forEachStmt, scope);
                break;

            case BreakStmt breakStmt:
                ValidateBreakStmt(breakStmt, scope);
                break;

            case ReturnStmt returnStmt:
                ValidateReturnStmt(returnStmt, scope);
                break;

            case SendStmt sendStmt:
                ValidateSendStmt(sendStmt, scope);
                break;

            case SpawnStmt spawnStmt:
                ValidateSpawnStmt(spawnStmt, scope);
                break;

            case DestroyStmt destroyStmt:
                ValidateDestroyStmt(destroyStmt, scope);
                break;

            case TransitionStmt transitionStmt:
                ValidateTransitionStmt(transitionStmt);
                break;

            case ExprStmt exprStmt:
                var exprType = ResolveExpr(exprStmt.Expression, scope, expectedType: null);
                _model.RecordType(exprStmt, exprType);
                break;
        }
    }

    private void ValidateLocalVarStmt(LocalVarStmt localVar, LexicalScope scope)
    {
        ResolvedType? explicitType = localVar.ExplicitType is not null
            ? ResolveTypeRef(localVar.ExplicitType)
            : null;

        if (explicitType == ResolvedType.Void)
        {
            ReportError($"Local variable '{localVar.Name}' cannot have type 'void'.", localVar.Span);
            explicitType = ResolvedType.Error;
        }

        ResolvedType? initType = null;
        if (localVar.Initializer is not null)
        {
            initType = ResolveExpr(localVar.Initializer, scope, expectedType: explicitType);
        }

        ResolvedType finalType;
        if (explicitType is not null && initType is not null)
        {
            if (!ResolvedType.IsAssignableTo(initType, explicitType))
            {
                ReportError(
                    $"Cannot initialize local variable '{localVar.Name}' of type '{explicitType}' with value of type '{initType}'.",
                    localVar.Span
                );
            }
            finalType = explicitType;
        }
        else if (explicitType is not null)
        {
            finalType = explicitType;
        }
        else if (initType is not null)
        {
            if (initType is NullType || initType == ResolvedType.Void)
            {
                ReportError(
                    $"Cannot infer type of local variable '{localVar.Name}' from '{initType}'. Provide an explicit type annotation.",
                    localVar.Span
                );
                finalType = ResolvedType.Error;
            }
            else
            {
                finalType = initType;
            }
        }
        else
        {
            ReportError(
                $"Local variable '{localVar.Name}' must have an explicit type annotation or an initializer.",
                localVar.Span
            );
            finalType = ResolvedType.Error;
        }

        var varSym = new VariableSymbol(
            Name: localVar.Name,
            Scope: localVar.Scope,
            Type: finalType,
            IsReadOnly: false,
            Declaration: localVar
        );

        if (!scope.TryDeclareLocal(varSym))
        {
            ReportError($"Variable '{localVar.Name}' is already declared in this scope.", localVar.Span);
        }

        _model.RecordType(localVar, finalType);
    }

    private void ValidateAssignStmt(AssignStmt assign, LexicalScope scope)
    {
        if (assign.Target is not (IdentifierExpr or FieldAccessExpr or IndexAccessExpr))
        {
            ReportError("Invalid assignment target. Left-hand side must be a variable, field, or array element.", assign.Span);
            ResolveExpr(assign.Value, scope, expectedType: null);
            _model.RecordType(assign, ResolvedType.Error);
            return;
        }

        if (assign.Target is IdentifierExpr idTarget)
        {
            if (scope.TryLookupLocal(idTarget.Name, out var localSym) && localSym!.IsReadOnly)
            {
                ReportError($"Cannot assign to read-only binding '{idTarget.Name}'.", assign.Span);
            }
            else if (_symbols.Assets.ContainsKey(idTarget.Name))
            {
                ReportError($"Cannot assign to compile-time asset '{idTarget.Name}'.", assign.Span);
            }
        }

        var targetType = ResolveExpr(assign.Target, scope, expectedType: null);
        if (assign.Target is FieldAccessExpr { Target: IdentifierExpr enumId } enumField &&
            _symbols.Enums.ContainsKey(enumId.Name))
        {
            ReportError($"Cannot assign to enum variant '{enumId.Name}.{enumField.FieldName}'.", assign.Span);
        }
        else if (IsReadOnlyComputedProperty(assign.Target, out string? readOnlyProp))
        {
            ReportError($"Cannot assign to read-only computed property '{readOnlyProp}'.", assign.Span);
        }

        var valueType = ResolveExpr(assign.Value, scope, expectedType: targetType);

        if (targetType.IsError || valueType.IsError)
        {
            _model.RecordType(assign, ResolvedType.Error);
            return;
        }

        if (assign.Operator == AssignOp.Assign)
        {
            if (!ResolvedType.IsAssignableTo(valueType, targetType))
            {
                ReportError(
                    $"Cannot assign value of type '{valueType}' to target of type '{targetType}'.",
                    assign.Span
                );
            }
        }
        else
        {
            var binaryEquiv = assign.Operator switch
            {
                AssignOp.AddAssign => BinaryOp.Add,
                AssignOp.SubAssign => BinaryOp.Sub,
                AssignOp.MulAssign => BinaryOp.Mul,
                AssignOp.DivAssign => BinaryOp.Div,
                _ => BinaryOp.Add
            };

            var resultType = TryResolveArithmeticBinaryOp(targetType, binaryEquiv, valueType);
            if (resultType is null || !ResolvedType.IsAssignableTo(resultType, targetType))
            {
                ReportError(
                    $"Operator '{FormatAssignOp(assign.Operator)}' cannot be applied to operands of type '{targetType}' and '{valueType}'.",
                    assign.Span
                );
            }
        }

        _model.RecordType(assign, targetType);
    }

    private void ValidateIfStmt(IfStmt ifStmt, LexicalScope scope)
    {
        var condType = ResolveExpr(ifStmt.Condition, scope, expectedType: ResolvedType.Bool);
        if (!condType.IsError && condType != ResolvedType.Bool)
        {
            ReportError($"'if' condition must evaluate to 'bool', got '{condType}'.", ifStmt.Span);
        }

        ValidateBlockStmt(ifStmt.ThenBranch, scope, createChildScope: true);
        if (ifStmt.ElseBranch is not null)
        {
            ValidateStmt(ifStmt.ElseBranch, scope);
        }

        _model.RecordType(ifStmt, ResolvedType.Void);
    }

    private void ValidateMatchStmt(MatchStmt matchStmt, LexicalScope scope)
    {
        var scrutineeType = ResolveExpr(matchStmt.Scrutinee, scope, expectedType: null);
        _model.RecordType(matchStmt, ResolvedType.Void);

        if (matchStmt.Arms.Count == 0)
        {
            ReportError("'match' statement must have at least one arm.", matchStmt.Span);
            return;
        }

        if (scrutineeType.IsError)
        {
            foreach (var arm in matchStmt.Arms)
            {
                ValidateMatchArmBody(arm.Body, scope);
            }
            return;
        }

        if (scrutineeType is EnumType enumType && _symbols.Enums.TryGetValue(enumType.Name, out var enumSym))
        {
            var coveredVariants = new HashSet<string>(StringComparer.Ordinal);
            bool hasWildcard = false;

            foreach (var arm in matchStmt.Arms)
            {
                if (hasWildcard)
                {
                    ReportError("Unreachable 'match' arm after wildcard '_'.", arm.Span);
                }

                switch (arm.Pattern)
                {
                    case WildcardPattern:
                        if (hasWildcard)
                        {
                            ReportError("Duplicate wildcard '_' arm in 'match' statement.", arm.Span);
                        }
                        hasWildcard = true;
                        break;

                    case EnumVariantPattern variantPat:
                        if (!string.Equals(variantPat.EnumName, enumSym.Name, StringComparison.Ordinal))
                        {
                            ReportError(
                                $"Match arm pattern '{variantPat.EnumName}.{variantPat.VariantName}' does not match scrutinee enum type '{enumSym.Name}'.",
                                arm.Span
                            );
                        }
                        else if (!enumSym.Variants.Contains(variantPat.VariantName))
                        {
                            ReportError(
                                $"Enum '{enumSym.Name}' does not contain a variant named '{variantPat.VariantName}'.",
                                arm.Span
                            );
                        }
                        else if (!coveredVariants.Add(variantPat.VariantName))
                        {
                            ReportError(
                                $"Duplicate match arm for '{enumSym.Name}.{variantPat.VariantName}'.",
                                arm.Span
                            );
                        }
                        break;

                    case IntLiteralPattern intPat:
                        ReportError(
                            $"Cannot match integer literal '{intPat.Value}' against enum type '{enumSym.Name}'.",
                            arm.Span
                        );
                        break;
                }

                ValidateMatchArmBody(arm.Body, scope);
            }

            if (!hasWildcard)
            {
                var missing = enumSym.Variants.Where(v => !coveredVariants.Contains(v)).ToList();
                if (missing.Count > 0)
                {
                    string missingList = string.Join(", ", missing.Select(static v => $"'{v}'"));
                    ReportError(
                        $"Non-exhaustive match on '{enumSym.Name}': missing cases {missingList}.",
                        matchStmt.Span
                    );
                }
            }
        }
        else if (scrutineeType.IsInteger)
        {
            var coveredInts = new HashSet<int>();
            bool hasWildcard = false;

            foreach (var arm in matchStmt.Arms)
            {
                if (hasWildcard)
                {
                    ReportError("Unreachable 'match' arm after wildcard '_'.", arm.Span);
                }

                switch (arm.Pattern)
                {
                    case WildcardPattern:
                        if (hasWildcard)
                        {
                            ReportError("Duplicate wildcard '_' arm in 'match' statement.", arm.Span);
                        }
                        hasWildcard = true;
                        break;

                    case IntLiteralPattern intPat:
                        if (!coveredInts.Add(intPat.Value))
                        {
                            ReportError(
                                $"Duplicate match arm for integer literal '{intPat.Value}'.",
                                arm.Span
                            );
                        }
                        break;

                    case EnumVariantPattern variantPat:
                        ReportError(
                            $"Cannot match enum variant '{variantPat.EnumName}.{variantPat.VariantName}' against integer type '{scrutineeType}'.",
                            arm.Span
                        );
                        break;
                }

                ValidateMatchArmBody(arm.Body, scope);
            }

            if (!hasWildcard)
            {
                ReportError(
                    $"Non-exhaustive match on integer type '{scrutineeType}': a wildcard '_' arm is required.",
                    matchStmt.Span
                );
            }
        }
        else
        {
            ReportError(
                $"'match' scrutinee must be an enum or integer type, got '{scrutineeType}'.",
                matchStmt.Scrutinee.Span
            );

            foreach (var arm in matchStmt.Arms)
            {
                ValidateMatchArmBody(arm.Body, scope);
            }
        }
    }

    private void ValidateMatchArmBody(Stmt body, LexicalScope parentScope)
    {
        var armScope = parentScope.CreateChild(isInsideLoop: false);
        if (body is BlockStmt block)
        {
            ValidateBlockStmt(block, armScope, createChildScope: false);
        }
        else
        {
            ValidateStmt(body, armScope);
        }
    }

    private bool IsReadOnlyComputedProperty(Expr target, out string? propertyName)
    {
        Expr? current = target;
        while (current is FieldAccessExpr fieldAccess)
        {
            var ownerType = _model.GetTypeOrDefault(fieldAccess.Target);
            if (ownerType == ResolvedType.Rect && fieldAccess.FieldName == "center")
            {
                propertyName = "rect.center";
                return true;
            }
            current = fieldAccess.Target;
        }

        propertyName = null;
        return false;
    }

    private void ValidateForStmt(ForStmt forStmt, LexicalScope scope)
    {
        var condType = ResolveExpr(forStmt.Condition, scope, expectedType: ResolvedType.Bool);
        if (!condType.IsError && condType != ResolvedType.Bool)
        {
            ReportError($"'for' condition must evaluate to 'bool', got '{condType}'.", forStmt.Span);
        }

        var loopScope = scope.CreateChild(isInsideLoop: true);
        ValidateBlockStmt(forStmt.Body, loopScope, createChildScope: false);
        _model.RecordType(forStmt, ResolvedType.Void);
    }

    private void ValidateForRangeStmt(ForRangeStmt forRangeStmt, LexicalScope scope)
    {
        var startType = ResolveExpr(forRangeStmt.Start, scope, expectedType: ResolvedType.Int);
        if (!startType.IsError && !startType.IsInteger)
        {
            ReportError($"'for' range start must be an integer type, got '{startType}'.", forRangeStmt.Start.Span);
        }

        var endType = ResolveExpr(forRangeStmt.End, scope, expectedType: ResolvedType.Int);
        if (!endType.IsError && !endType.IsInteger)
        {
            ReportError($"'for' range end must be an integer type, got '{endType}'.", forRangeStmt.End.Span);
        }

        var loopScope = scope.CreateChild(isInsideLoop: true);
        loopScope.TryDeclareLocal(
            new VariableSymbol(
                Name: forRangeStmt.IteratorName,
                Scope: MemoryScope.Stack,
                Type: ResolvedType.Int,
                IsReadOnly: true,
                Declaration: forRangeStmt
            )
        );

        _model.RecordType(forRangeStmt, ResolvedType.Int);
        ValidateBlockStmt(forRangeStmt.Body, loopScope, createChildScope: false);
    }

    private void ValidateForEachStmt(ForEachStmt forEachStmt, LexicalScope scope)
    {
        ResolvedType iteratorType = ResolvedType.Error;

        if (!_symbols.Actors.TryGetValue(forEachStmt.PoolName, out var actorSym))
        {
            ReportError(
                $"'each' loop target '{forEachStmt.PoolName}' is not a declared actor pool.",
                forEachStmt.Span
            );
        }
        else if (actorSym.IsSingleton)
        {
            ReportError(
                $"Actor '{forEachStmt.PoolName}' is a singleton actor (declared without '[capacity]') and cannot be iterated with 'each'.",
                forEachStmt.Span
            );
        }
        else
        {
            iteratorType = actorSym.RefType;
        }

        var loopScope = scope.CreateChild(isInsideLoop: true);
        loopScope.TryDeclareLocal(
            new VariableSymbol(
                Name: forEachStmt.IteratorName,
                Scope: MemoryScope.Stack,
                Type: iteratorType,
                IsReadOnly: true,
                Declaration: forEachStmt
            )
        );

        _model.RecordType(forEachStmt, iteratorType);
        ValidateBlockStmt(forEachStmt.Body, loopScope, createChildScope: false);
    }

    private void ValidateBreakStmt(BreakStmt breakStmt, LexicalScope scope)
    {
        if (!scope.IsInsideLoop)
        {
            ReportError("'break' statement can only be used inside a 'for' or 'each' loop.", breakStmt.Span);
        }

        _model.RecordType(breakStmt, ResolvedType.Void);
    }

    private void ValidateReturnStmt(ReturnStmt returnStmt, LexicalScope scope)
    {
        var expected = scope.ExpectedReturnType;

        if (returnStmt.Value is null)
        {
            if (expected != ResolvedType.Void && !expected.IsError)
            {
                ReportError($"Return statement must return a value of type '{expected}'.", returnStmt.Span);
            }
            _model.RecordType(returnStmt, ResolvedType.Void);
            return;
        }

        var actual = ResolveExpr(returnStmt.Value, scope, expectedType: expected);
        if (expected == ResolvedType.Void)
        {
            ReportError($"Cannot return a value from a void hook, handler, or function.", returnStmt.Span);
        }
        else if (!ResolvedType.IsAssignableTo(actual, expected))
        {
            ReportError($"Cannot return value of type '{actual}'; expected '{expected}'.", returnStmt.Span);
        }

        _model.RecordType(returnStmt, actual);
    }

    private void ValidateSendStmt(SendStmt sendStmt, LexicalScope scope)
    {
        var targetType = ResolveExpr(sendStmt.Target, scope, expectedType: null);
        ActorSymbol? targetActor = null;

        if (!targetType.IsError)
        {
            if (targetType is ActorRefType actorRef)
            {
                _symbols.Actors.TryGetValue(actorRef.ActorName, out targetActor);
            }
            else
            {
                ReportError(
                    $"'send' target must be an actor reference ('ref<T>'), got '{targetType}'.",
                    sendStmt.Span
                );
            }
        }

        if (!_symbols.Messages.TryGetValue(sendStmt.MessageName, out var msgSym))
        {
            ReportError($"Undeclared message type '{sendStmt.MessageName}' in 'send' statement.", sendStmt.Span);
            foreach (var arg in sendStmt.Arguments)
            {
                ResolveExpr(arg.Value, scope, expectedType: null);
            }
            _model.RecordType(sendStmt, ResolvedType.Error);
            return;
        }

        if (targetActor is not null && !targetActor.Handlers.ContainsKey(sendStmt.MessageName))
        {
            ReportError(
                $"Actor '{targetActor.Name}' does not declare an 'on {sendStmt.MessageName}(...)' message handler.",
                sendStmt.Span
            );
        }

        var providedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arg in sendStmt.Arguments)
        {
            if (!providedFields.Add(arg.Name))
            {
                ReportError(
                    $"Duplicate field '{arg.Name}' in message '{sendStmt.MessageName}' payload.",
                    arg.Span
                );
            }

            if (!msgSym.Fields.TryGetValue(arg.Name, out var fieldSym))
            {
                ReportError(
                    $"Message '{sendStmt.MessageName}' does not have a field named '{arg.Name}'.",
                    arg.Span
                );
                ResolveExpr(arg.Value, scope, expectedType: null);
                _model.RecordType(arg, ResolvedType.Error);
                continue;
            }

            var argType = ResolveExpr(arg.Value, scope, expectedType: fieldSym.Type);
            _model.RecordType(arg, argType);

            if (!ResolvedType.IsAssignableTo(argType, fieldSym.Type))
            {
                ReportError(
                    $"Message '{sendStmt.MessageName}' field '{arg.Name}' expects type '{fieldSym.Type}', got '{argType}'.",
                    arg.Span
                );
            }
        }

        // Check for required message fields without default values
        foreach (var fieldSym in msgSym.Fields.Values)
        {
            if (!fieldSym.HasDefaultValue && !providedFields.Contains(fieldSym.Name))
            {
                ReportError(
                    $"Missing required field '{fieldSym.Name}' in message '{sendStmt.MessageName}' payload.",
                    sendStmt.Span
                );
            }
        }

        _model.RecordType(sendStmt, new MessageType(sendStmt.MessageName));
    }

    private void ValidateSpawnStmt(SpawnStmt spawnStmt, LexicalScope scope)
    {
        ResolvedType spawnedRefType = ResolvedType.Error;

        if (!_symbols.Actors.TryGetValue(spawnStmt.ActorName, out var actorSym))
        {
            ReportError($"Cannot spawn undeclared actor '{spawnStmt.ActorName}'.", spawnStmt.Span);
            foreach (var init in spawnStmt.Initializers)
            {
                ResolveExpr(init.Value, scope, expectedType: null);
            }
        }
        else
        {
            spawnedRefType = actorSym.RefType;
            var seenFields = new HashSet<string>(StringComparer.Ordinal);

            foreach (var init in spawnStmt.Initializers)
            {
                if (!seenFields.Add(init.Name))
                {
                    ReportError(
                        $"Duplicate field initializer '{init.Name}' in 'spawn {spawnStmt.ActorName}'.",
                        init.Span
                    );
                }

                if (!actorSym.Fields.TryGetValue(init.Name, out var fieldSym))
                {
                    ReportError(
                        $"Actor '{spawnStmt.ActorName}' does not have a field named '{init.Name}'.",
                        init.Span
                    );
                    ResolveExpr(init.Value, scope, expectedType: null);
                    _model.RecordType(init, ResolvedType.Error);
                    continue;
                }

                var valType = ResolveExpr(init.Value, scope, expectedType: fieldSym.Type);
                _model.RecordType(init, valType);

                if (!ResolvedType.IsAssignableTo(valType, fieldSym.Type))
                {
                    ReportError(
                        $"Field '{init.Name}' on actor '{spawnStmt.ActorName}' expects type '{fieldSym.Type}', got '{valType}'.",
                        init.Span
                    );
                }
            }
        }

        if (spawnStmt.BindingName is not null)
        {
            var bindingSym = new VariableSymbol(
                Name: spawnStmt.BindingName,
                Scope: MemoryScope.Stack,
                Type: spawnedRefType,
                IsReadOnly: false,
                Declaration: spawnStmt
            );

            if (!scope.TryDeclareLocal(bindingSym))
            {
                ReportError($"Variable '{spawnStmt.BindingName}' is already declared in this scope.", spawnStmt.Span);
            }
        }

        _model.RecordType(spawnStmt, spawnedRefType);
    }

    private void ValidateDestroyStmt(DestroyStmt destroyStmt, LexicalScope scope)
    {
        if (destroyStmt.Target is SelfExpr && scope.EnclosingActor is null)
        {
            ReportError("'destroy self' is only valid inside an 'actor' declaration.", destroyStmt.Span);
            _model.RecordType(destroyStmt.Target, ResolvedType.Error);
            _model.RecordType(destroyStmt, ResolvedType.Error);
            return;
        }

        var targetType = ResolveExpr(destroyStmt.Target, scope, expectedType: null);
        if (!targetType.IsError && targetType is not ActorRefType)
        {
            ReportError(
                $"'destroy' target must be 'self' or an actor reference ('ref<T>'), got '{targetType}'.",
                destroyStmt.Span
            );
        }

        _model.RecordType(destroyStmt, ResolvedType.Void);
    }

    private void ValidateTransitionStmt(TransitionStmt transitionStmt)
    {
        if (!_symbols.States.ContainsKey(transitionStmt.TargetStateName))
        {
            ReportError(
                $"'goto {transitionStmt.TargetStateName}' targets undeclared state '{transitionStmt.TargetStateName}'.",
                transitionStmt.Span
            );
            _model.RecordType(transitionStmt, ResolvedType.Error);
            return;
        }

        _model.RecordType(transitionStmt, ResolvedType.Void);
    }

    // ========================================================================
    // Expression Type Resolution
    // ========================================================================

    private ResolvedType ResolveExpr(Expr expr, LexicalScope scope, ResolvedType? expectedType)
    {
        ResolvedType resolved = expr switch
        {
            IntLiteralExpr => ResolvedType.Int,
            FloatLiteralExpr => ResolvedType.F32,
            TimeLiteralExpr => ResolvedType.Timer,
            ColorLiteralExpr => ResolvedType.Color,
            StringLiteralExpr => ResolvedType.String,
            BooleanLiteralExpr => ResolvedType.Bool,
            NullLiteralExpr => ResolvedType.Null,
            SelfExpr selfExpr => ResolveSelfExpr(selfExpr, scope),
            VecLiteralExpr vecExpr => ResolveVecLiteralExpr(vecExpr, scope, expectedType),
            IdentifierExpr idExpr => ResolveIdentifierExpr(idExpr, scope),
            UnaryExpr unaryExpr => ResolveUnaryExpr(unaryExpr, scope),
            BinaryExpr binaryExpr => ResolveBinaryExpr(binaryExpr, scope),
            FieldAccessExpr fieldExpr => ResolveFieldAccessExpr(fieldExpr, scope),
            IndexAccessExpr indexExpr => ResolveIndexAccessExpr(indexExpr, scope),
            CallExpr callExpr => ResolveCallExpr(callExpr, scope),
            _ => ReportAndReturnError($"Unsupported expression node '{expr.GetType().Name}'.", expr.Span)
        };

        return _model.RecordType(expr, resolved);
    }

    private ResolvedType ResolveSelfExpr(SelfExpr selfExpr, LexicalScope scope)
    {
        if (scope.EnclosingActor is null)
        {
            ReportError("'self' is only valid inside an 'actor' declaration.", selfExpr.Span);
            return ResolvedType.Error;
        }

        return scope.EnclosingActor.RefType;
    }

    private ResolvedType ResolveVecLiteralExpr(
        VecLiteralExpr vecExpr,
        LexicalScope scope,
        ResolvedType? expectedType)
    {
        int count = vecExpr.Components.Count;
        foreach (var comp in vecExpr.Components)
        {
            var compType = ResolveExpr(comp, scope, expectedType: ResolvedType.F32);
            if (!compType.IsError && !compType.IsNumeric)
            {
                ReportError(
                    $"Vector/rect literal component must be numeric ('f32' or 'int'), got '{compType}'.",
                    comp.Span
                );
            }
        }

        ResolvedType inferred = count switch
        {
            2 => ResolvedType.Vec2,
            3 => ResolvedType.Vec3,
            4 => ResolvedType.Rect,
            _ => ReportAndReturnError(
                $"Vector literal must have 2 ('vec2'), 3 ('vec3'), or 4 ('rect') components, got {count}.",
                vecExpr.Span
            )
        };

        if (expectedType is PrimitiveType { Kind: PrimitiveKind.Vec2 or PrimitiveKind.Vec3 or PrimitiveKind.Rect } expPrim &&
            !inferred.IsError &&
            inferred != expPrim)
        {
            ReportError(
                $"Vector literal with {count} components has type '{inferred}', which cannot be used as '{expPrim}'.",
                vecExpr.Span
            );
            return ResolvedType.Error;
        }

        return inferred;
    }

    private ResolvedType ResolveIdentifierExpr(IdentifierExpr idExpr, LexicalScope scope)
    {
        // Local variables & parameters (including foreach iterators and message handler parameters)
        if (scope.TryLookupLocal(idExpr.Name, out var localSym))
            return localSym!.Type;

        // Enclosing actor fields
        if (scope.EnclosingActor is not null &&
            scope.EnclosingActor.Fields.TryGetValue(idExpr.Name, out var actorField))
        {
            return actorField.Type;
        }

        // Enclosing state fields
        if (scope.EnclosingState is not null &&
            scope.EnclosingState.Fields.TryGetValue(idExpr.Name, out var stateField))
        {
            return stateField.Type;
        }

        // Global & level variables
        if (_symbols.Globals.TryGetValue(idExpr.Name, out var globalSym))
            return globalSym.Type;

        // Compile-time assets
        if (_symbols.Assets.TryGetValue(idExpr.Name, out var assetSym))
            return assetSym.Type;

        // Singleton actors referenced directly by name resolve to ref<ActorName>
        if (_symbols.Actors.TryGetValue(idExpr.Name, out var actorSym) && actorSym.IsSingleton)
            return actorSym.RefType;

        if (_symbols.Enums.TryGetValue(idExpr.Name, out var enumSym))
        {
            string example = enumSym.Variants.Count > 0 ? enumSym.Variants[0] : "Variant";
            ReportError(
                $"'{idExpr.Name}' is an enum type; access a variant such as '{idExpr.Name}.{example}'.",
                idExpr.Span
            );
            return ResolvedType.Error;
        }

        ReportError($"Undeclared identifier '{idExpr.Name}'.", idExpr.Span);
        return ResolvedType.Error;
    }

    private ResolvedType ResolveUnaryExpr(UnaryExpr unaryExpr, LexicalScope scope)
    {
        var operandType = ResolveExpr(unaryExpr.Operand, scope, expectedType: null);
        if (operandType.IsError)
            return ResolvedType.Error;

        switch (unaryExpr.Operator)
        {
            case UnaryOp.Negate:
                if (operandType.IsNumeric || operandType.IsVector)
                    return operandType;

                ReportError($"Unary '-' operator cannot be applied to operand of type '{operandType}'.", unaryExpr.Span);
                return ResolvedType.Error;

            case UnaryOp.Not:
                if (operandType == ResolvedType.Bool)
                    return ResolvedType.Bool;

                ReportError($"Logical '!' operator requires a 'bool' operand, got '{operandType}'.", unaryExpr.Span);
                return ResolvedType.Error;

            default:
                return ResolvedType.Error;
        }
    }

    private ResolvedType ResolveBinaryExpr(BinaryExpr binaryExpr, LexicalScope scope)
    {
        var leftType = ResolveExpr(binaryExpr.Left, scope, expectedType: null);
        var rightType = ResolveExpr(binaryExpr.Right, scope, expectedType: null);

        if (leftType.IsError || rightType.IsError)
            return ResolvedType.Error;

        switch (binaryExpr.Op)
        {
            case BinaryOp.Add:
            case BinaryOp.Sub:
            case BinaryOp.Mul:
            case BinaryOp.Div:
            case BinaryOp.Mod:
            {
                var result = TryResolveArithmeticBinaryOp(leftType, binaryExpr.Op, rightType);
                if (result is not null)
                    return result;

                ReportError(
                    $"Operator '{FormatBinaryOp(binaryExpr.Op)}' cannot be applied to operands of type '{leftType}' and '{rightType}'.",
                    binaryExpr.Span
                );
                return ResolvedType.Error;
            }

            case BinaryOp.Less:
            case BinaryOp.LessEqual:
            case BinaryOp.Greater:
            case BinaryOp.GreaterEqual:
            {
                bool validOrderComparison =
                    (leftType.IsNumeric && rightType.IsNumeric) ||
                    (leftType == ResolvedType.Timer && (rightType == ResolvedType.Timer || rightType.IsNumeric)) ||
                    (rightType == ResolvedType.Timer && leftType.IsNumeric);

                if (validOrderComparison)
                    return ResolvedType.Bool;

                ReportError(
                    $"Comparison operator '{FormatBinaryOp(binaryExpr.Op)}' requires numeric or timer operands, got '{leftType}' and '{rightType}'.",
                    binaryExpr.Span
                );
                return ResolvedType.Error;
            }

            case BinaryOp.Equal:
            case BinaryOp.NotEqual:
            {
                bool comparable =
                    ResolvedType.IsAssignableTo(leftType, rightType) ||
                    ResolvedType.IsAssignableTo(rightType, leftType);

                if (comparable)
                    return ResolvedType.Bool;

                ReportError(
                    $"Cannot compare incompatible types '{leftType}' and '{rightType}' with '{FormatBinaryOp(binaryExpr.Op)}'.",
                    binaryExpr.Span
                );
                return ResolvedType.Error;
            }

            case BinaryOp.LogicalAnd:
            case BinaryOp.LogicalOr:
            {
                if (leftType == ResolvedType.Bool && rightType == ResolvedType.Bool)
                    return ResolvedType.Bool;

                ReportError(
                    $"Logical operator '{FormatBinaryOp(binaryExpr.Op)}' requires 'bool' operands, got '{leftType}' and '{rightType}'.",
                    binaryExpr.Span
                );
                return ResolvedType.Error;
            }

            default:
                return ResolvedType.Error;
        }
    }

    private static ResolvedType? TryResolveArithmeticBinaryOp(
        ResolvedType left,
        BinaryOp op,
        ResolvedType right)
    {
        // Numeric + Numeric -> promoted numeric type (f32 if either is f32, else int)
        if (left.IsNumeric && right.IsNumeric)
        {
            if (op == BinaryOp.Mod && (left.IsFloat || right.IsFloat))
                return ResolvedType.F32;

            return (left.IsFloat || right.IsFloat) ? ResolvedType.F32 : ResolvedType.Int;
        }

        // Vector arithmetic:
        //    vec2 + vec2, vec2 - vec2, vec2 * vec2, vec2 / vec2
        //    vec3 + vec3, vec3 - vec3, vec3 * vec3, vec3 / vec3
        if (left.IsVector && left == right && op is BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Div)
        {
            return left;
        }

        // Vector scalar multiplication & division:
        //    vec2 * f32, f32 * vec2, vec2 / f32
        //    vec3 * f32, f32 * vec3, vec3 / f32
        if (left.IsVector && right.IsNumeric && op is BinaryOp.Mul or BinaryOp.Div)
        {
            return left;
        }

        if (left.IsNumeric && right.IsVector && op == BinaryOp.Mul)
        {
            return right;
        }

        // Timer arithmetic (timer + time/f32, timer - time/f32)
        if (left == ResolvedType.Timer && (right == ResolvedType.Timer || right.IsNumeric) &&
            op is BinaryOp.Add or BinaryOp.Sub)
        {
            return ResolvedType.Timer;
        }

        return null;
    }

    private ResolvedType ResolveFieldAccessExpr(FieldAccessExpr fieldExpr, LexicalScope scope)
    {
        // Enum variant access: EnumName.Variant
        if (fieldExpr.Target is IdentifierExpr enumId &&
            _symbols.Enums.TryGetValue(enumId.Name, out var enumSym))
        {
            _model.RecordType(fieldExpr.Target, enumSym.Type);
            if (enumSym.Variants.Contains(fieldExpr.FieldName))
            {
                return enumSym.Type;
            }

            ReportError(
                $"Enum '{enumSym.Name}' does not contain a variant named '{fieldExpr.FieldName}'.",
                fieldExpr.Span
            );
            return ResolvedType.Error;
        }

        var targetType = ResolveExpr(fieldExpr.Target, scope, expectedType: null);
        if (targetType.IsError)
            return ResolvedType.Error;

        string field = fieldExpr.FieldName;

        switch (targetType)
        {
            case PrimitiveType { Kind: PrimitiveKind.Vec2 }:
                if (field is "x" or "y")
                    return ResolvedType.F32;
                break;

            case PrimitiveType { Kind: PrimitiveKind.Vec3 }:
                if (field is "x" or "y" or "z")
                    return ResolvedType.F32;
                break;

            case PrimitiveType { Kind: PrimitiveKind.Rect }:
                if (field is "x" or "y" or "w" or "h")
                    return ResolvedType.F32;
                if (field == "center")
                    return ResolvedType.Vec2;
                break;

            case PrimitiveType { Kind: PrimitiveKind.Color }:
                if (field is "r" or "g" or "b" or "a")
                    return ResolvedType.U8;
                break;

            case PrimitiveType { Kind: PrimitiveKind.Timer }:
                if (field is "remaining" or "duration")
                    return ResolvedType.F32;
                break;

            case StructType st when _symbols.Structs.TryGetValue(st.Name, out var structSym):
                if (structSym.Fields.TryGetValue(field, out var structField))
                    return structField.Type;
                break;

            case MessageType msg when _symbols.Messages.TryGetValue(msg.Name, out var msgSym):
                if (msgSym.Fields.TryGetValue(field, out var msgField))
                    return msgField.Type;
                break;

            case ActorRefType actorRef when _symbols.Actors.TryGetValue(actorRef.ActorName, out var actorSym):
                if (actorSym.Fields.TryGetValue(field, out var actorField))
                    return actorField.Type;
                break;

            case FixedArrayType arrayType:
                if (field is "len" or "length" or "capacity")
                    return ResolvedType.Int;
                break;
        }

        ReportError($"Type '{targetType}' does not contain a field named '{field}'.", fieldExpr.Span);
        return ResolvedType.Error;
    }

    private ResolvedType ResolveIndexAccessExpr(IndexAccessExpr indexExpr, LexicalScope scope)
    {
        var targetType = ResolveExpr(indexExpr.Target, scope, expectedType: null);
        var idxType = ResolveExpr(indexExpr.Index, scope, expectedType: ResolvedType.Int);

        if (!idxType.IsError && !idxType.IsInteger)
        {
            ReportError($"Array index must be an integer type, got '{idxType}'.", indexExpr.Span);
        }

        if (targetType.IsError)
            return ResolvedType.Error;

        if (targetType is FixedArrayType fixedArray)
            return fixedArray.ElementType;

        ReportError($"Cannot index into non-array type '{targetType}'.", indexExpr.Span);
        return ResolvedType.Error;
    }

    private ResolvedType ResolveCallExpr(CallExpr callExpr, LexicalScope scope)
    {
        // Method call on an object/primitive (e.g., shoot_cooldown.elapsed() or target.custom_method())
        if (callExpr.Callee is FieldAccessExpr memberAccess)
        {
            return ResolveMethodCallExpr(callExpr, memberAccess, scope);
        }

        // Direct function or enclosing actor/state method call (e.g., sprite(...), helper(...))
        if (callExpr.Callee is IdentifierExpr funcId)
        {
            FunctionSymbol? fnSym = null;

            if (scope.EnclosingActor is not null &&
                scope.EnclosingActor.Methods.TryGetValue(funcId.Name, out var actorMethod))
            {
                fnSym = actorMethod;
            }
            else if (scope.EnclosingState is not null &&
                     scope.EnclosingState.Methods.TryGetValue(funcId.Name, out var stateMethod))
            {
                fnSym = stateMethod;
            }
            else if (_symbols.Functions.TryGetValue(funcId.Name, out var globalFn))
            {
                fnSym = globalFn;
            }

            if (fnSym is null)
            {
                ReportError($"Call to undeclared function '{funcId.Name}'.", callExpr.Span);
                foreach (var arg in callExpr.Arguments)
                {
                    ResolveExpr(arg, scope, expectedType: null);
                }
                return ResolvedType.Error;
            }

            _model.RecordType(callExpr.Callee, fnSym.Signature);
            ValidateCallArguments(fnSym.Name, fnSym, callExpr.Arguments, scope, callExpr.Span);
            return fnSym.ReturnType;
        }

        ReportError("Invalid call target expression.", callExpr.Span);
        foreach (var arg in callExpr.Arguments)
        {
            ResolveExpr(arg, scope, expectedType: null);
        }
        return ResolvedType.Error;
    }

    private ResolvedType ResolveMethodCallExpr(
        CallExpr callExpr,
        FieldAccessExpr memberAccess,
        LexicalScope scope)
    {
        var receiverType = ResolveExpr(memberAccess.Target, scope, expectedType: null);
        if (receiverType.IsError)
        {
            foreach (var arg in callExpr.Arguments)
            {
                ResolveExpr(arg, scope, expectedType: null);
            }
            return ResolvedType.Error;
        }

        string methodName = memberAccess.FieldName;

        // Built-in methods on 'timer'
        if (receiverType == ResolvedType.Timer)
        {
            switch (methodName)
            {
                case "elapsed" or "ready":
                    if (callExpr.Arguments.Count != 0)
                    {
                        ReportError($"Method 'timer.{methodName}()' takes 0 arguments, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    _model.RecordType(memberAccess, new FunctionType([], ResolvedType.Bool));
                    return ResolvedType.Bool;

                case "reset":
                    if (callExpr.Arguments.Count > 1)
                    {
                        ReportError($"Method 'timer.reset()' takes 0 or 1 duration argument, got {callExpr.Arguments.Count}.", callExpr.Span);
                    }
                    foreach (var arg in callExpr.Arguments)
                    {
                        var argType = ResolveExpr(arg, scope, expectedType: ResolvedType.Timer);
                        if (!ResolvedType.IsAssignableTo(argType, ResolvedType.Timer))
                        {
                            ReportError($"Argument to 'timer.reset' must be a time literal or 'f32', got '{argType}'.", arg.Span);
                        }
                    }
                    _model.RecordType(memberAccess, new FunctionType([], ResolvedType.Void));
                    return ResolvedType.Void;
            }
        }

        // Built-in methods on 'vec2' and 'vec3'
        if (receiverType.IsVector)
        {
            switch (methodName)
            {
                case "length":
                    if (callExpr.Arguments.Count != 0)
                    {
                        ReportError($"Method '{receiverType}.length()' takes 0 arguments.", callExpr.Span);
                    }
                    _model.RecordType(memberAccess, new FunctionType([], ResolvedType.F32));
                    return ResolvedType.F32;

                case "normalized":
                    if (callExpr.Arguments.Count != 0)
                    {
                        ReportError($"Method '{receiverType}.normalized()' takes 0 arguments.", callExpr.Span);
                    }
                    _model.RecordType(memberAccess, new FunctionType([], receiverType));
                    return receiverType;

                case "dot":
                    if (callExpr.Arguments.Count != 1)
                    {
                        ReportError($"Method '{receiverType}.dot(other)' takes 1 argument, got {callExpr.Arguments.Count}.", callExpr.Span);
                    }
                    else
                    {
                        var argType = ResolveExpr(callExpr.Arguments[0], scope, expectedType: receiverType);
                        if (!ResolvedType.IsAssignableTo(argType, receiverType))
                        {
                            ReportError($"Argument to '{receiverType}.dot' must be '{receiverType}', got '{argType}'.", callExpr.Arguments[0].Span);
                        }
                    }
                    _model.RecordType(memberAccess, new FunctionType([receiverType], ResolvedType.F32));
                    return ResolvedType.F32;
            }
        }

        // Built-in methods on 'rect'
        if (receiverType == ResolvedType.Rect)
        {
            switch (methodName)
            {
                case "overlaps":
                    if (callExpr.Arguments.Count != 1)
                    {
                        ReportError($"Method 'rect.overlaps(other)' takes 1 argument, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else
                    {
                        var argType = ResolveExpr(callExpr.Arguments[0], scope, expectedType: ResolvedType.Rect);
                        if (!ResolvedType.IsAssignableTo(argType, ResolvedType.Rect))
                        {
                            ReportError($"Argument to 'rect.overlaps' must be 'rect', got '{argType}'.", callExpr.Arguments[0].Span);
                        }
                    }
                    _model.RecordType(memberAccess, new FunctionType([ResolvedType.Rect], ResolvedType.Bool));
                    return ResolvedType.Bool;

                case "contains":
                    if (callExpr.Arguments.Count != 1)
                    {
                        ReportError($"Method 'rect.contains(point)' takes 1 argument, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else
                    {
                        var argType = ResolveExpr(callExpr.Arguments[0], scope, expectedType: ResolvedType.Vec2);
                        if (!ResolvedType.IsAssignableTo(argType, ResolvedType.Vec2))
                        {
                            ReportError($"Argument to 'rect.contains' must be 'vec2', got '{argType}'.", callExpr.Arguments[0].Span);
                        }
                    }
                    _model.RecordType(memberAccess, new FunctionType([ResolvedType.Vec2], ResolvedType.Bool));
                    return ResolvedType.Bool;
            }
        }

        // Actor methods via ref<ActorName>
        if (receiverType is ActorRefType actorRef &&
            _symbols.Actors.TryGetValue(actorRef.ActorName, out var actorSym) &&
            actorSym.Methods.TryGetValue(methodName, out var actorMethod))
        {
            _model.RecordType(memberAccess, actorMethod.Signature);
            ValidateCallArguments($"{actorSym.Name}.{methodName}", actorMethod, callExpr.Arguments, scope, callExpr.Span);
            return actorMethod.ReturnType;
        }

        foreach (var arg in callExpr.Arguments)
        {
            ResolveExpr(arg, scope, expectedType: null);
        }

        ReportError($"Type '{receiverType}' does not define a method named '{methodName}'.", callExpr.Span);
        return ResolvedType.Error;
    }

    private void ValidateCallArguments(
        string callableName,
        FunctionSymbol fnSym,
        IReadOnlyList<Expr> arguments,
        LexicalScope scope,
        SourceSpan callSpan)
    {
        int expectedCount = fnSym.Parameters.Count;

        if (!fnSym.IsVariadic && arguments.Count != expectedCount)
        {
            ReportError(
                $"Function '{callableName}' expects {expectedCount} argument(s), got {arguments.Count}.",
                callSpan
            );
        }
        else if (fnSym.IsVariadic && arguments.Count < expectedCount)
        {
            ReportError(
                $"Function '{callableName}' expects at least {expectedCount} argument(s), got {arguments.Count}.",
                callSpan
            );
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            var expectedParamType = i < expectedCount ? fnSym.Parameters[i].Type : null;
            var argType = ResolveExpr(arguments[i], scope, expectedType: expectedParamType);

            if (expectedParamType is not null && !ResolvedType.IsAssignableTo(argType, expectedParamType))
            {
                ReportError(
                    $"Argument {i + 1} ('{fnSym.Parameters[i].Name}') of '{callableName}' expects '{expectedParamType}', got '{argType}'.",
                    arguments[i].Span
                );
            }
        }
    }

    // ========================================================================
    // Static Memory Budget Validation
    // ========================================================================

    private void ValidateStaticMemoryBudget()
    {
        long estimatedBytes = _symbols.EstimateTotalStaticMemoryBytes();
        _model.EstimatedStaticMemoryBytes = estimatedBytes;

        if (_symbols.ConfiguredRamBudgetBytes is long budgetBytes && estimatedBytes > budgetBytes)
        {
            ReportError(
                $"Estimated static memory footprint ({estimatedBytes:N0} bytes) exceeds configured game 'ram_budget' ({budgetBytes:N0} bytes).",
                _symbols.GameConfig?.Span ?? default
            );
        }
    }

    // ========================================================================
    // Formatting Helpers
    // ========================================================================

    private ResolvedType ReportAndReturnError(string message, SourceSpan span = default)
    {
        ReportError(message, span);
        return ResolvedType.Error;
    }

    private static string FormatBinaryOp(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Sub => "-",
        BinaryOp.Mul => "*",
        BinaryOp.Div => "/",
        BinaryOp.Mod => "%",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.Less => "<",
        BinaryOp.LessEqual => "<=",
        BinaryOp.Greater => ">",
        BinaryOp.GreaterEqual => ">=",
        BinaryOp.LogicalAnd => "&&",
        BinaryOp.LogicalOr => "||",
        _ => op.ToString()
    };

    private static string FormatAssignOp(AssignOp op) => op switch
    {
        AssignOp.Assign => "=",
        AssignOp.AddAssign => "+=",
        AssignOp.SubAssign => "-=",
        AssignOp.MulAssign => "*=",
        AssignOp.DivAssign => "/=",
        _ => op.ToString()
    };
}
