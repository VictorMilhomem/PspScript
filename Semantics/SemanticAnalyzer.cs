using PspScript.Ast;

namespace PspScript.Semantics;


public sealed class SemanticAnalyzer
{
    private readonly SymbolTable _symbols = new();
    private readonly SemanticModel _model;
    private readonly List<SemanticDiagnostic> _diagnostics = [];
    private readonly Dictionary<string, ConstDecl> _pendingConstants = new(StringComparer.Ordinal);
    private readonly HashSet<string> _evaluatingConstants = new(StringComparer.Ordinal);
    private readonly bool _checkAssetPathsOnDisk;

    public SemanticAnalyzer(bool checkAssetPathsOnDisk = false)
    {
        _model = new SemanticModel(_symbols);
        _checkAssetPathsOnDisk = checkAssetPathsOnDisk;
    }

    public static SemanticAnalysisResult Analyze(ProgramNode program, bool checkAssetPathsOnDisk = false)
    {
        var analyzer = new SemanticAnalyzer(checkAssetPathsOnDisk);
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
        if (_checkAssetPathsOnDisk)
        {
            ValidateAssetAndConfigPathsOnDisk();
        }

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
        // Pre-pass A: register enums and pending compile-time constants first so they can be used
        // in actor pool capacities, fixed array sizes, and other constant initializers in any order.
        foreach (var decl in program.Declarations)
        {
            if (decl is EnumDecl enumDecl)
            {
                if (!RegisterUniqueTopLevel(enumDecl.Name, enumDecl))
                    continue;

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
            }
            else if (decl is ConstDecl constDecl)
            {
                if (!RegisterUniqueTopLevel(constDecl.Name, constDecl))
                    continue;

                _pendingConstants[constDecl.Name] = constDecl;
            }
        }

        // Pre-pass B: evaluate all top-level compile-time constants
        foreach (string constName in _pendingConstants.Keys.ToList())
        {
            TryGetOrEvaluateTopLevelConst(constName, out _);
        }

        foreach (var decl in program.Declarations)
        {
            switch (decl)
            {
                case EnumDecl:
                case ConstDecl:
                    break;

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
                    bool isMutable = asset.Options.Any(static opt =>
                        string.Equals(opt.Name, "mutable", StringComparison.OrdinalIgnoreCase) &&
                        opt.Value is BooleanLiteralExpr { Value: true });
                    _symbols.Assets[asset.Name] = new AssetSymbol(
                        asset.Name,
                        asset.AssetKind,
                        asset.FilePath,
                        assetType,
                        asset,
                        isMutable
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

                case ActorDecl actor:
                    if (!RegisterUniqueTopLevel(actor.Name, actor))
                        break;

                    int? resolvedCap = actor.PoolCapacity;
                    if (actor.PoolCapacityExpr is not null)
                    {
                        if (TryEvaluateIntConstant(actor.PoolCapacityExpr, scope: null, out int evalCap))
                        {
                            resolvedCap = evalCap;
                            if (evalCap <= 0)
                            {
                                ReportError(
                                    $"Actor '{actor.Name}' pool capacity must be a positive integer, got {evalCap}.",
                                    actor.PoolCapacityExpr.Span
                                );
                            }
                        }
                        else
                        {
                            ReportError(
                                $"Actor '{actor.Name}' pool capacity must be a compile-time constant positive integer.",
                                actor.PoolCapacityExpr.Span
                            );
                            resolvedCap = 1;
                        }
                    }
                    else if (resolvedCap is <= 0)
                    {
                        ReportError(
                            $"Actor '{actor.Name}' pool capacity must be a positive integer, got {resolvedCap}.",
                            actor.Span
                        );
                    }

                    _symbols.Actors[actor.Name] = new ActorSymbol(
                        actor.Name,
                        resolvedCap,
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

    private static readonly string[] ValidGameConfigKeys =
    [
        "ram_budget", "fps", "entry", "id", "version",
        "extra_ram", "window_scale", "icon", "background", "overlay"
    ];

    private void ExtractGameConfigSettings(GameConfigDecl gameConfig)
    {
        foreach (var (key, value) in gameConfig.Settings)
        {
            switch (key)
            {
                case "ram_budget":
                    switch (value)
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
                    break;

                case "fps":
                    if (value is IntConfigValue fpsVal && fpsVal.Value > 0)
                    {
                        // Validated positive target FPS
                    }
                    else
                    {
                        ReportError("'fps' in game config must be a positive integer (e.g., 60).", gameConfig.Span);
                    }
                    break;

                case "entry":
                    // Validated in ResolveGameEntryState() after all states are collected
                    break;

                case "id":
                    if (value is StringConfigValue idVal &&
                        idVal.Value.Length is >= 1 and <= 9 &&
                        idVal.Value.All(static c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                    {
                        _symbols.GameId = idVal.Value;
                    }
                    else
                    {
                        ReportError("'id' in game config must be a 1-9 character uppercase alphanumeric string (e.g., \"PSPS00001\").", gameConfig.Span);
                    }
                    break;

                case "version":
                    if (value is StringConfigValue verVal && !string.IsNullOrWhiteSpace(verVal.Value))
                    {
                        _symbols.GameVersion = verVal.Value.Trim();
                    }
                    else
                    {
                        ReportError("'version' in game config must be a non-empty string (e.g., \"01.00\").", gameConfig.Span);
                    }
                    break;

                case "extra_ram":
                    if (value is BoolConfigValue extraRamVal)
                    {
                        _symbols.ExtraRam = extraRamVal.Value;
                    }
                    else
                    {
                        ReportError("'extra_ram' in game config must be a boolean ('true' or 'false').", gameConfig.Span);
                    }
                    break;

                case "window_scale":
                    if (value is IntConfigValue scaleVal && scaleVal.Value is >= 1 and <= 4)
                    {
                        _symbols.WindowScale = scaleVal.Value;
                    }
                    else
                    {
                        ReportError("'window_scale' in game config must be an integer between 1 and 4.", gameConfig.Span);
                    }
                    break;

                case "icon":
                    if (value is StringConfigValue iconVal && !string.IsNullOrWhiteSpace(iconVal.Value))
                    {
                        _symbols.IconPath = iconVal.Value.Trim();
                    }
                    else
                    {
                        ReportError("'icon' in game config must be a non-empty PNG file path string.", gameConfig.Span);
                    }
                    break;

                case "background":
                    if (value is StringConfigValue bgVal && !string.IsNullOrWhiteSpace(bgVal.Value))
                    {
                        _symbols.BackgroundPath = bgVal.Value.Trim();
                    }
                    else
                    {
                        ReportError("'background' in game config must be a non-empty PNG file path string.", gameConfig.Span);
                    }
                    break;

                case "overlay":
                    if (value is StringConfigValue ovVal && !string.IsNullOrWhiteSpace(ovVal.Value))
                    {
                        _symbols.OverlayPath = ovVal.Value.Trim();
                    }
                    else
                    {
                        ReportError("'overlay' in game config must be a non-empty PNG file path string.", gameConfig.Span);
                    }
                    break;

                default:
                    ReportError(
                        $"Unknown game config key '{key}'. Valid keys: {string.Join(", ", ValidGameConfigKeys)}.",
                        gameConfig.Span
                    );
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

            ResolvedType resolvedType;
            if (field.Type is not null)
            {
                resolvedType = ResolveTypeRef(field.Type);
                if (resolvedType == ResolvedType.Void)
                {
                    ReportError($"Field '{field.Name}' in {ownerDescription} cannot have type 'void'.", field.Span);
                    resolvedType = ResolvedType.Error;
                }
            }
            else if (field.DefaultValue is not null)
            {
                var inferred = ResolveExpr(field.DefaultValue, new LexicalScope(null), expectedType: null);
                if (inferred is NullType || inferred == ResolvedType.Void)
                {
                    ReportError(
                        $"Cannot infer type of field '{field.Name}' in {ownerDescription} from '{inferred.DisplayName}'. Specify an explicit type.",
                        field.Span
                    );
                    resolvedType = ResolvedType.Error;
                }
                else
                {
                    resolvedType = inferred;
                }
            }
            else
            {
                ReportError($"Field '{field.Name}' in {ownerDescription} must specify a type or a default value.", field.Span);
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

            FunctionTypeRef fnRef => ResolveFunctionType(fnRef),

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
        int resolvedLength = 1;
        if (TryEvaluateIntConstant(arrayRef.LengthExpr, scope: null, out int evalLen))
        {
            resolvedLength = evalLen;
            if (evalLen <= 0)
            {
                ReportError($"Fixed array length must be greater than 0, got {evalLen}.", arrayRef.Span);
                resolvedLength = 1;
            }
        }
        else
        {
            ReportError("Fixed array length must be a compile-time constant positive integer.", arrayRef.LengthExpr.Span);
        }

        var elementType = ResolveTypeRef(arrayRef.ElementType);
        if (elementType == ResolvedType.Void)
        {
            ReportError("Fixed array element type cannot be 'void'.", arrayRef.Span);
            elementType = ResolvedType.Error;
        }

        return new FixedArrayType(resolvedLength, elementType);
    }

    private ResolvedType ResolveFunctionType(FunctionTypeRef fnRef)
    {
        var paramTypes = new List<ResolvedType>(fnRef.ParameterTypes.Count);
        foreach (var paramTypeRef in fnRef.ParameterTypes)
        {
            var paramType = ResolveTypeRef(paramTypeRef);
            if (paramType == ResolvedType.Void)
            {
                ReportError("Function type parameter cannot have type 'void'.", paramTypeRef.Span);
                paramType = ResolvedType.Error;
            }
            paramTypes.Add(paramType);
        }

        var returnType = ResolveTypeRef(fnRef.ReturnType);
        return new FunctionType(paramTypes, returnType);
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
                    ValidateAssetOptions(asset, rootScope);
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

    private void ValidateAssetOptions(AssetDecl asset, LexicalScope rootScope)
    {
        foreach (var opt in asset.Options)
        {
            if (asset.AssetKind is "sound" or "music" &&
                string.Equals(opt.Name, "rate", StringComparison.OrdinalIgnoreCase))
            {
                if (opt.Value is IntLiteralExpr { Value: 44100 or 22050 })
                {
                    _model.RecordType(opt.Value, ResolvedType.Int);
                    _model.RecordType(opt, ResolvedType.Int);
                }
                else
                {
                    ReportError(
                        $"Audio asset '{asset.Name}' option 'rate' must be 44100 or 22050.",
                        opt.Span
                    );
                    _model.RecordType(opt, ResolvedType.Error);
                }
                continue;
            }

            if (string.Equals(asset.AssetKind, "tilemap", StringComparison.Ordinal))
            {
                if (string.Equals(opt.Name, "tileset", StringComparison.OrdinalIgnoreCase))
                {
                    if (opt.Value is IdentifierExpr tsId &&
                        _symbols.Assets.TryGetValue(tsId.Name, out var tsSym))
                    {
                        if (!string.Equals(tsSym.AssetKind, "texture", StringComparison.Ordinal))
                        {
                            ReportError(
                                $"Tilemap '{asset.Name}' option 'tileset' must reference a 'texture' asset, got '{tsSym.AssetKind}'.",
                                opt.Span
                            );
                        }
                        _model.RecordType(opt.Value, tsSym.Type);
                        _model.RecordType(opt, tsSym.Type);
                    }
                    else
                    {
                        ReportError(
                            $"Tilemap '{asset.Name}' option 'tileset' must reference a declared 'texture' asset.",
                            opt.Span
                        );
                        _model.RecordType(opt, ResolvedType.Error);
                    }
                    continue;
                }

                if (string.Equals(opt.Name, "mutable", StringComparison.OrdinalIgnoreCase))
                {
                    if (opt.Value is not BooleanLiteralExpr)
                    {
                        ReportError(
                            $"Tilemap '{asset.Name}' option 'mutable' must be a boolean literal ('true' or 'false').",
                            opt.Span
                        );
                        _model.RecordType(opt, ResolvedType.Error);
                    }
                    else
                    {
                        _model.RecordType(opt.Value, ResolvedType.Bool);
                        _model.RecordType(opt, ResolvedType.Bool);
                    }
                    continue;
                }

                if (string.Equals(opt.Name, "collision_layer", StringComparison.OrdinalIgnoreCase))
                {
                    if (opt.Value is not StringLiteralExpr)
                    {
                        ReportError(
                            $"Tilemap '{asset.Name}' option 'collision_layer' must be a string literal.",
                            opt.Span
                        );
                        _model.RecordType(opt, ResolvedType.Error);
                    }
                    else
                    {
                        _model.RecordType(opt.Value, ResolvedType.String);
                        _model.RecordType(opt, ResolvedType.String);
                    }
                    continue;
                }

                if (string.Equals(opt.Name, "draw_collision", StringComparison.OrdinalIgnoreCase))
                {
                    if (opt.Value is not BooleanLiteralExpr)
                    {
                        ReportError(
                            $"Tilemap '{asset.Name}' option 'draw_collision' must be a boolean literal ('true' or 'false').",
                            opt.Span
                        );
                        _model.RecordType(opt, ResolvedType.Error);
                    }
                    else
                    {
                        _model.RecordType(opt.Value, ResolvedType.Bool);
                        _model.RecordType(opt, ResolvedType.Bool);
                    }
                    continue;
                }
            }

            ResolvedType optType = opt.Value is IdentifierExpr optId &&
                                   !_symbols.Globals.ContainsKey(optId.Name) &&
                                   !_symbols.Assets.ContainsKey(optId.Name)
                ? _model.RecordType(opt.Value, ResolvedType.String)
                : ResolveExpr(opt.Value, rootScope, expectedType: null);
            _model.RecordType(opt, optType);
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

            case LocalConstStmt localConst:
                ValidateLocalConstStmt(localConst, scope);
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

            case ContinueStmt continueStmt:
                ValidateContinueStmt(continueStmt, scope);
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

    private void ValidateLocalConstStmt(LocalConstStmt localConst, LexicalScope scope)
    {
        ResolvedType? explicitType = localConst.ExplicitType is not null
            ? ResolveTypeRef(localConst.ExplicitType)
            : null;

        if (explicitType == ResolvedType.Void)
        {
            ReportError($"Local constant '{localConst.Name}' cannot have type 'void'.", localConst.Span);
            explicitType = ResolvedType.Error;
        }

        ResolvedType finalType = explicitType ?? ResolvedType.Error;
        ConstValue? constVal = null;

        if (TryEvaluateConstExpr(localConst.Initializer, scope, explicitType, out var evaluated, out var initType))
        {
            constVal = evaluated;
            if (explicitType is not null)
            {
                if (!ResolvedType.IsAssignableTo(initType, explicitType))
                {
                    ReportError(
                        $"Cannot initialize local constant '{localConst.Name}' of type '{explicitType}' with value of type '{initType}'.",
                        localConst.Span
                    );
                }
                finalType = explicitType;
            }
            else
            {
                finalType = initType;
            }
        }
        else
        {
            ReportError(
                $"Constant '{localConst.Name}' initializer must be a compile-time constant expression.",
                localConst.Initializer.Span
            );
        }

        var varSym = new VariableSymbol(
            Name: localConst.Name,
            Scope: MemoryScope.Stack,
            Type: finalType,
            IsReadOnly: true,
            Declaration: localConst,
            ConstantValue: constVal
        );

        if (!scope.TryDeclareLocal(varSym))
        {
            ReportError($"Constant '{localConst.Name}' is already declared in this scope.", localConst.Span);
        }

        _model.RecordType(localConst, finalType);
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
                string kind = localSym.ConstantValue is not null ? "compile-time constant" : "read-only binding";
                ReportError($"Cannot assign to {kind} '{idTarget.Name}'.", assign.Span);
            }
            else if (_symbols.Constants.ContainsKey(idTarget.Name))
            {
                ReportError($"Cannot assign to compile-time constant '{idTarget.Name}'.", assign.Span);
            }
            else if (_symbols.Assets.ContainsKey(idTarget.Name))
            {
                ReportError($"Cannot assign to compile-time asset '{idTarget.Name}'.", assign.Span);
            }
            else if (_symbols.Functions.ContainsKey(idTarget.Name) &&
                     !scope.TryLookupLocal(idTarget.Name, out _) &&
                     !(scope.EnclosingActor?.Fields.ContainsKey(idTarget.Name) ?? false) &&
                     !(scope.EnclosingState?.Fields.ContainsKey(idTarget.Name) ?? false))
            {
                ReportError($"Cannot assign to function '{idTarget.Name}'.", assign.Span);
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
        else if (assign.Operator is AssignOp.BitAndAssign or AssignOp.BitOrAssign or AssignOp.BitXorAssign
                                 or AssignOp.ShiftLeftAssign or AssignOp.ShiftRightAssign)
        {
            if (!targetType.IsInteger || !valueType.IsInteger)
            {
                ReportError(
                    $"Operator '{FormatAssignOp(assign.Operator)}' requires integer operands, got '{targetType}' and '{valueType}'.",
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

        if (!scrutineeType.IsError)
        {
            var armPatterns = matchStmt.Arms
                .Select(static a => (a.Patterns, a.Span))
                .ToList();
            ValidateMatchArmsPatterns(scrutineeType, armPatterns, scope, matchStmt.Span, matchStmt.Scrutinee.Span);
        }

        foreach (var arm in matchStmt.Arms)
        {
            ValidateMatchArmBody(arm.Body, scope);
        }
    }

    private void ValidateMatchArmsPatterns(
        ResolvedType scrutineeType,
        IReadOnlyList<(IReadOnlyList<MatchPattern> Patterns, SourceSpan ArmSpan)> arms,
        LexicalScope scope,
        SourceSpan matchSpan,
        SourceSpan scrutineeSpan)
    {
        if (scrutineeType is EnumType enumType && _symbols.Enums.TryGetValue(enumType.Name, out var enumSym))
        {
            var coveredVariants = new HashSet<string>(StringComparer.Ordinal);
            bool hasWildcard = false;

            foreach (var (patterns, armSpan) in arms)
            {
                if (hasWildcard)
                {
                    ReportError("Unreachable 'match' arm after wildcard '_'.", armSpan);
                }

                foreach (var pattern in patterns)
                {
                    switch (pattern)
                    {
                        case WildcardPattern:
                            if (hasWildcard)
                            {
                                ReportError("Duplicate wildcard '_' arm in 'match' statement.", armSpan);
                            }
                            hasWildcard = true;
                            break;

                        case EnumVariantPattern variantPat:
                            if (!string.Equals(variantPat.EnumName, enumSym.Name, StringComparison.Ordinal))
                            {
                                ReportError(
                                    $"Match arm pattern '{variantPat.EnumName}.{variantPat.VariantName}' does not match scrutinee enum type '{enumSym.Name}'.",
                                    variantPat.Span
                                );
                            }
                            else if (!enumSym.Variants.Contains(variantPat.VariantName))
                            {
                                ReportError(
                                    $"Enum '{enumSym.Name}' does not contain a variant named '{variantPat.VariantName}'.",
                                    variantPat.Span
                                );
                            }
                            else if (!coveredVariants.Add(variantPat.VariantName))
                            {
                                ReportError(
                                    $"Duplicate match arm for '{enumSym.Name}.{variantPat.VariantName}'.",
                                    variantPat.Span
                                );
                            }
                            break;

                        case IntLiteralPattern intPat:
                            ReportError(
                                $"Cannot match integer literal '{intPat.Value}' against enum type '{enumSym.Name}'.",
                                intPat.Span
                            );
                            break;

                        case ConstPattern constPat:
                            if (!TryLookupConstValue(constPat.Name, scope, out var enumConstVal, out _))
                            {
                                ReportError($"Undeclared compile-time constant '{constPat.Name}' in 'match' pattern.", constPat.Span);
                            }
                            else if (enumConstVal is EnumConstValue ecv)
                            {
                                if (!string.Equals(ecv.EnumName, enumSym.Name, StringComparison.Ordinal))
                                {
                                    ReportError(
                                        $"Constant '{constPat.Name}' ({ecv.EnumName}.{ecv.VariantName}) does not match scrutinee enum type '{enumSym.Name}'.",
                                        constPat.Span
                                    );
                                }
                                else if (!coveredVariants.Add(ecv.VariantName))
                                {
                                    ReportError(
                                        $"Duplicate match arm for '{enumSym.Name}.{ecv.VariantName}' (via constant '{constPat.Name}').",
                                        constPat.Span
                                    );
                                }
                                else
                                {
                                    _model.RecordConstPattern(constPat, ecv);
                                }
                            }
                            else
                            {
                                ReportError(
                                    $"Constant '{constPat.Name}' cannot be matched against enum type '{enumSym.Name}'.",
                                    constPat.Span
                                );
                            }
                            break;
                    }
                }
            }

            if (!hasWildcard)
            {
                var missing = enumSym.Variants.Where(v => !coveredVariants.Contains(v)).ToList();
                if (missing.Count > 0)
                {
                    string missingList = string.Join(", ", missing.Select(static v => $"'{v}'"));
                    ReportError(
                        $"Non-exhaustive match on '{enumSym.Name}': missing cases {missingList}.",
                        matchSpan
                    );
                }
            }
        }
        else if (scrutineeType.IsInteger)
        {
            var coveredInts = new HashSet<int>();
            bool hasWildcard = false;

            foreach (var (patterns, armSpan) in arms)
            {
                if (hasWildcard)
                {
                    ReportError("Unreachable 'match' arm after wildcard '_'.", armSpan);
                }

                foreach (var pattern in patterns)
                {
                    switch (pattern)
                    {
                        case WildcardPattern:
                            if (hasWildcard)
                            {
                                ReportError("Duplicate wildcard '_' arm in 'match' statement.", armSpan);
                            }
                            hasWildcard = true;
                            break;

                        case IntLiteralPattern intPat:
                            if (!coveredInts.Add(intPat.Value))
                            {
                                ReportError(
                                    $"Duplicate match arm for integer literal '{intPat.Value}'.",
                                    intPat.Span
                                );
                            }
                            break;

                        case EnumVariantPattern variantPat:
                            ReportError(
                                $"Cannot match enum variant '{variantPat.EnumName}.{variantPat.VariantName}' against integer type '{scrutineeType}'.",
                                variantPat.Span
                            );
                            break;

                        case ConstPattern constPat:
                            if (!TryLookupConstValue(constPat.Name, scope, out var intConstVal, out _))
                            {
                                ReportError($"Undeclared compile-time constant '{constPat.Name}' in 'match' pattern.", constPat.Span);
                            }
                            else if (intConstVal is IntConstValue icv)
                            {
                                int val = (int)icv.Value;
                                if (!coveredInts.Add(val))
                                {
                                    ReportError(
                                        $"Duplicate match arm for integer value '{val}' (via constant '{constPat.Name}').",
                                        constPat.Span
                                    );
                                }
                                else
                                {
                                    _model.RecordConstPattern(constPat, icv);
                                }
                            }
                            else
                            {
                                ReportError(
                                    $"Constant '{constPat.Name}' must be an integer constant to match against '{scrutineeType}'.",
                                    constPat.Span
                                );
                            }
                            break;
                    }
                }
            }

            if (!hasWildcard)
            {
                ReportError(
                    $"Non-exhaustive match on integer type '{scrutineeType}': a wildcard '_' arm is required.",
                    matchSpan
                );
            }
        }
        else
        {
            ReportError(
                $"'match' scrutinee must be an enum or integer type, got '{scrutineeType}'.",
                scrutineeSpan
            );
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
            if (ownerType is AssetType { AssetKind: "tilemap" } &&
                fieldAccess.FieldName is "cols" or "rows" or "tile_size" or "layer_count")
            {
                propertyName = $"tilemap.{fieldAccess.FieldName}";
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

    private void ValidateContinueStmt(ContinueStmt continueStmt, LexicalScope scope)
    {
        if (!scope.IsInsideLoop)
        {
            ReportError("'continue' statement can only be used inside a 'for' or 'each' loop.", continueStmt.Span);
        }

        _model.RecordType(continueStmt, ResolvedType.Void);
    }

    private void ValidateReturnStmt(ReturnStmt returnStmt, LexicalScope scope)
    {
        if (scope.InferReturnType)
        {
            var rootLambda = scope.RootLambdaScope;
            if (returnStmt.Value is null)
            {
                if (rootLambda.InferredReturnType is not null &&
                    rootLambda.InferredReturnType != ResolvedType.Void &&
                    !rootLambda.InferredReturnType.IsError)
                {
                    ReportError($"Return statement must return a value of type '{rootLambda.InferredReturnType}'.", returnStmt.Span);
                }
                else
                {
                    rootLambda.InferredReturnType = ResolvedType.Void;
                }
                _model.RecordType(returnStmt, ResolvedType.Void);
                return;
            }

            var inferredVal = ResolveExpr(returnStmt.Value, scope, expectedType: rootLambda.InferredReturnType);
            if (rootLambda.InferredReturnType is null)
            {
                rootLambda.InferredReturnType = inferredVal;
            }
            else if (rootLambda.InferredReturnType == ResolvedType.Void)
            {
                ReportError("Cannot return a value from a void lambda.", returnStmt.Span);
            }
            else if (!ResolvedType.IsAssignableTo(inferredVal, rootLambda.InferredReturnType))
            {
                if (ResolvedType.IsAssignableTo(rootLambda.InferredReturnType, inferredVal))
                {
                    rootLambda.InferredReturnType = inferredVal;
                }
                else
                {
                    ReportError($"Cannot return value of type '{inferredVal}'; expected '{rootLambda.InferredReturnType}'.", returnStmt.Span);
                }
            }

            _model.RecordType(returnStmt, inferredVal);
            return;
        }

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
            NullLiteralExpr => expectedType is FunctionType ? expectedType : ResolvedType.Null,
            SelfExpr selfExpr => ResolveSelfExpr(selfExpr, scope),
            VecLiteralExpr vecExpr => ResolveVecLiteralExpr(vecExpr, scope, expectedType),
            IdentifierExpr idExpr => ResolveIdentifierExpr(idExpr, scope),
            UnaryExpr unaryExpr => ResolveUnaryExpr(unaryExpr, scope),
            BinaryExpr binaryExpr => ResolveBinaryExpr(binaryExpr, scope),
            TernaryExpr ternaryExpr => ResolveTernaryExpr(ternaryExpr, scope, expectedType),
            MatchExpr matchExpr => ResolveMatchExpr(matchExpr, scope, expectedType),
            FieldAccessExpr fieldExpr => ResolveFieldAccessExpr(fieldExpr, scope),
            IndexAccessExpr indexExpr => ResolveIndexAccessExpr(indexExpr, scope),
            CallExpr callExpr => ResolveCallExpr(callExpr, scope),
            LambdaExpr lambdaExpr => ResolveLambdaExpr(lambdaExpr, scope, expectedType),
            _ => ReportAndReturnError($"Unsupported expression node '{expr.GetType().Name}'.", expr.Span)
        };

        return _model.RecordType(expr, resolved);
    }

    private ResolvedType ResolveTernaryExpr(
        TernaryExpr ternary,
        LexicalScope scope,
        ResolvedType? expectedType)
    {
        var condType = ResolveExpr(ternary.Condition, scope, expectedType: ResolvedType.Bool);
        if (!condType.IsError && condType != ResolvedType.Bool)
        {
            ReportError($"Ternary condition must evaluate to 'bool', got '{condType}'.", ternary.Condition.Span);
        }

        var trueType = ResolveExpr(ternary.WhenTrue, scope, expectedType: expectedType);
        var falseType = ResolveExpr(ternary.WhenFalse, scope, expectedType: expectedType ?? (trueType.IsError ? null : trueType));

        if (trueType.IsError || falseType.IsError)
            return ResolvedType.Error;

        if (trueType == falseType)
            return trueType;

        if (ResolvedType.IsAssignableTo(trueType, falseType))
            return falseType;

        if (ResolvedType.IsAssignableTo(falseType, trueType))
            return trueType;

        ReportError(
            $"Ternary conditional branches have incompatible types: '{trueType}' and '{falseType}'.",
            ternary.Span
        );
        return ResolvedType.Error;
    }

    private ResolvedType ResolveMatchExpr(
        MatchExpr matchExpr,
        LexicalScope scope,
        ResolvedType? expectedType)
    {
        var scrutineeType = ResolveExpr(matchExpr.Scrutinee, scope, expectedType: null);
        if (matchExpr.Arms.Count == 0)
        {
            ReportError("'match' expression must have at least one arm.", matchExpr.Span);
            return ResolvedType.Error;
        }

        if (!scrutineeType.IsError)
        {
            var armPatterns = matchExpr.Arms
                .Select(static a => (a.Patterns, a.Span))
                .ToList();
            ValidateMatchArmsPatterns(scrutineeType, armPatterns, scope, matchExpr.Span, matchExpr.Scrutinee.Span);
        }

        ResolvedType? unifiedType = null;
        foreach (var arm in matchExpr.Arms)
        {
            var armType = ResolveExpr(arm.Value, scope, expectedType: expectedType ?? unifiedType);
            if (armType.IsError)
                continue;

            if (unifiedType is null)
            {
                unifiedType = armType;
            }
            else if (unifiedType == armType)
            {
                continue;
            }
            else if (ResolvedType.IsAssignableTo(armType, unifiedType))
            {
                continue;
            }
            else if (ResolvedType.IsAssignableTo(unifiedType, armType))
            {
                unifiedType = armType;
            }
            else
            {
                ReportError(
                    $"Match expression arm produces incompatible type '{armType}', expected '{unifiedType}'.",
                    arm.Value.Span
                );
            }
        }

        return unifiedType ?? ResolvedType.Error;
    }

    private ResolvedType ResolveSelfExpr(SelfExpr selfExpr, LexicalScope scope)
    {
        if (scope.IsInsideLambda)
        {
            ReportError(
                "Cannot capture 'self' inside a lambda. PSPScript lambdas are zero-heap non-capturing function pointers.",
                selfExpr.Span
            );
            return ResolvedType.Error;
        }

        if (scope.EnclosingActor is null)
        {
            ReportError("'self' is only valid inside an 'actor' declaration.", selfExpr.Span);
            return ResolvedType.Error;
        }

        return scope.EnclosingActor.RefType;
    }

    private ResolvedType ResolveLambdaExpr(LambdaExpr lambda, LexicalScope outerScope, ResolvedType? expectedType)
    {
        var expectedFn = expectedType as FunctionType;

        if (expectedFn is not null && lambda.Parameters.Count != expectedFn.ParameterTypes.Count)
        {
            ReportError(
                $"Lambda has {lambda.Parameters.Count} parameter(s), but expected '{expectedFn}' with {expectedFn.ParameterTypes.Count} parameter(s).",
                lambda.Span
            );
        }

        var paramTypes = new List<ResolvedType>(lambda.Parameters.Count);
        var seenParams = new HashSet<string>(StringComparer.Ordinal);
        bool inferReturn = expectedFn is null;
        var lambdaScope = new LexicalScope(
            parent: null,
            enclosingActor: null,
            enclosingState: null,
            expectedReturnType: expectedFn?.ReturnType ?? ResolvedType.Void,
            isInsideLoop: false,
            isInsideLambda: true,
            outerScopeForCaptureCheck: outerScope,
            inferReturnType: inferReturn
        );

        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            var param = lambda.Parameters[i];
            if (!seenParams.Add(param.Name))
            {
                ReportError($"Duplicate parameter '{param.Name}' in lambda.", param.Span);
            }

            ResolvedType paramType;
            if (param.ExplicitType is not null)
            {
                paramType = ResolveTypeRef(param.ExplicitType);
                if (paramType == ResolvedType.Void)
                {
                    ReportError($"Lambda parameter '{param.Name}' cannot have type 'void'.", param.Span);
                    paramType = ResolvedType.Error;
                }
            }
            else if (expectedFn is not null && i < expectedFn.ParameterTypes.Count)
            {
                paramType = expectedFn.ParameterTypes[i];
            }
            else
            {
                ReportError(
                    $"Cannot infer type for lambda parameter '{param.Name}'. Provide an explicit type annotation or assign the lambda to a typed function variable/field.",
                    param.Span
                );
                paramType = ResolvedType.Error;
            }

            _model.RecordType(param, paramType);
            paramTypes.Add(paramType);
            lambdaScope.TryDeclareLocal(new VariableSymbol(param.Name, MemoryScope.Stack, paramType, Declaration: param));
        }

        ResolvedType returnType;
        if (lambda.BodyExpr is not null)
        {
            var bodyType = ResolveExpr(lambda.BodyExpr, lambdaScope, expectedType: expectedFn?.ReturnType);
            if (expectedFn is not null)
            {
                if (expectedFn.ReturnType == ResolvedType.Void)
                {
                    if (bodyType != ResolvedType.Void && !bodyType.IsError)
                    {
                        ReportError(
                            $"Lambda expression returns '{bodyType}', but expected 'void'.",
                            lambda.BodyExpr.Span
                        );
                    }
                }
                else if (!ResolvedType.IsAssignableTo(bodyType, expectedFn.ReturnType))
                {
                    ReportError(
                        $"Lambda expression returns '{bodyType}', expected '{expectedFn.ReturnType}'.",
                        lambda.BodyExpr.Span
                    );
                }
                returnType = expectedFn.ReturnType;
            }
            else
            {
                returnType = bodyType;
            }
        }
        else if (lambda.BodyStmt is BlockStmt block)
        {
            ValidateBlockStmt(block, lambdaScope, createChildScope: false);
            returnType = expectedFn?.ReturnType ?? (lambdaScope.InferredReturnType ?? ResolvedType.Void);

            if (returnType != ResolvedType.Void && !returnType.IsError && !BlockDefinitelyReturns(block))
            {
                ReportError(
                    $"Lambda expects return type '{returnType}', but not all code paths return a value.",
                    lambda.Span
                );
            }
        }
        else if (lambda.BodyStmt is not null)
        {
            ValidateStmt(lambda.BodyStmt, lambdaScope);
            returnType = expectedFn?.ReturnType ?? (lambdaScope.InferredReturnType ?? ResolvedType.Void);

            if (expectedFn is not null &&
                expectedFn.ReturnType != ResolvedType.Void &&
                !expectedFn.ReturnType.IsError &&
                lambda.BodyStmt is not ReturnStmt)
            {
                ReportError(
                    $"Lambda statement body returns 'void', expected '{expectedFn.ReturnType}'.",
                    lambda.BodyStmt.Span
                );
            }
        }
        else
        {
            returnType = ResolvedType.Void;
        }

        _model.RegisterLambda(lambda);
        return new FunctionType(paramTypes, returnType);
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

        // Compile-time constants
        if (_symbols.Constants.TryGetValue(idExpr.Name, out var constSym))
            return constSym.Type;

        // Global & level variables
        if (_symbols.Globals.TryGetValue(idExpr.Name, out var globalSym))
            return globalSym.Type;

        // Compile-time assets
        if (_symbols.Assets.TryGetValue(idExpr.Name, out var assetSym))
            return assetSym.Type;

        // Singleton actors referenced directly by name resolve to ref<ActorName>
        if (_symbols.Actors.TryGetValue(idExpr.Name, out var actorSym) && actorSym.IsSingleton)
            return actorSym.RefType;

        // Top-level user-defined functions as first-class function values
        if (_symbols.Functions.TryGetValue(idExpr.Name, out var fnSym))
        {
            if (fnSym.IsBuiltin || fnSym.IsVariadic)
            {
                ReportError(
                    $"Built-in function '{idExpr.Name}' cannot be used as a function value; wrap it in a lambda instead.",
                    idExpr.Span
                );
                return ResolvedType.Error;
            }
            return fnSym.Signature;
        }

        if (_symbols.Enums.TryGetValue(idExpr.Name, out var enumSym))
        {
            string example = enumSym.Variants.Count > 0 ? enumSym.Variants[0] : "Variant";
            ReportError(
                $"'{idExpr.Name}' is an enum type; access a variant such as '{idExpr.Name}.{example}'.",
                idExpr.Span
            );
            return ResolvedType.Error;
        }

        if (scope.IsInsideLambda && scope.TryLookupOuterCaptured(idExpr.Name, out var captureKind))
        {
            ReportError(
                $"Cannot capture {captureKind} '{idExpr.Name}' inside a lambda. PSPScript lambdas are zero-heap non-capturing function pointers; pass values as parameters or use globals/constants.",
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

            case UnaryOp.BitNot:
                if (operandType.IsInteger)
                    return operandType == ResolvedType.U32 ? ResolvedType.U32 : ResolvedType.Int;

                ReportError($"Bitwise '~' operator requires an integer operand, got '{operandType}'.", unaryExpr.Span);
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

            case BinaryOp.BitAnd:
            case BinaryOp.BitOr:
            case BinaryOp.BitXor:
            case BinaryOp.ShiftLeft:
            case BinaryOp.ShiftRight:
            {
                if (leftType.IsInteger && rightType.IsInteger)
                {
                    bool unsigned32 = leftType == ResolvedType.U32 ||
                        (binaryExpr.Op is BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor && rightType == ResolvedType.U32);
                    return unsigned32 ? ResolvedType.U32 : ResolvedType.Int;
                }

                ReportError(
                    $"Bitwise operator '{FormatBinaryOp(binaryExpr.Op)}' requires integer operands, got '{leftType}' and '{rightType}'.",
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
                if (leftType is FunctionType && binaryExpr.Right is NullLiteralExpr)
                    _model.RecordType(binaryExpr.Right, leftType);
                else if (rightType is FunctionType && binaryExpr.Left is NullLiteralExpr)
                    _model.RecordType(binaryExpr.Left, rightType);

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

            case AssetType { AssetKind: "tilemap" }:
                if (field is "cols" or "rows" or "tile_size" or "layer_count")
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

        // Direct function, method, or function-pointer variable/field call
        if (callExpr.Callee is IdentifierExpr funcId)
        {
            // 1. Local variable or parameter (e.g., callback parameter `cb(x)`)
            if (scope.TryLookupLocal(funcId.Name, out var localSym))
            {
                _model.RecordType(callExpr.Callee, localSym!.Type);
                return ValidateFunctionValueCall(funcId.Name, localSym.Type, callExpr.Arguments, scope, callExpr.Span);
            }

            // 2. Enclosing actor method or function-typed field
            if (scope.EnclosingActor is not null)
            {
                if (scope.EnclosingActor.Methods.TryGetValue(funcId.Name, out var actorMethod))
                {
                    _model.RecordType(callExpr.Callee, actorMethod.Signature);
                    ValidateCallArguments(actorMethod.Name, actorMethod, callExpr.Arguments, scope, callExpr.Span);
                    return actorMethod.ReturnType;
                }
                if (scope.EnclosingActor.Fields.TryGetValue(funcId.Name, out var actorField))
                {
                    _model.RecordType(callExpr.Callee, actorField.Type);
                    return ValidateFunctionValueCall(funcId.Name, actorField.Type, callExpr.Arguments, scope, callExpr.Span);
                }
            }

            // 3. Enclosing state method or function-typed field
            if (scope.EnclosingState is not null)
            {
                if (scope.EnclosingState.Methods.TryGetValue(funcId.Name, out var stateMethod))
                {
                    _model.RecordType(callExpr.Callee, stateMethod.Signature);
                    ValidateCallArguments(stateMethod.Name, stateMethod, callExpr.Arguments, scope, callExpr.Span);
                    return stateMethod.ReturnType;
                }
                if (scope.EnclosingState.Fields.TryGetValue(funcId.Name, out var stateField))
                {
                    _model.RecordType(callExpr.Callee, stateField.Type);
                    return ValidateFunctionValueCall(funcId.Name, stateField.Type, callExpr.Arguments, scope, callExpr.Span);
                }
            }

            // 4. Top-level or built-in function
            if (_symbols.Functions.TryGetValue(funcId.Name, out var globalFn))
            {
                _model.RecordType(callExpr.Callee, globalFn.Signature);
                ValidateCallArguments(globalFn.Name, globalFn, callExpr.Arguments, scope, callExpr.Span);
                return globalFn.ReturnType;
            }

            // 5. Global/level variable of function type
            if (_symbols.Globals.TryGetValue(funcId.Name, out var globalVar))
            {
                _model.RecordType(callExpr.Callee, globalVar.Type);
                return ValidateFunctionValueCall(funcId.Name, globalVar.Type, callExpr.Arguments, scope, callExpr.Span);
            }

            if (scope.IsInsideLambda && scope.TryLookupOuterCaptured(funcId.Name, out var captureKind))
            {
                ReportError(
                    $"Cannot capture {captureKind} '{funcId.Name}' inside a lambda. PSPScript lambdas are zero-heap non-capturing function pointers.",
                    callExpr.Span
                );
            }
            else
            {
                ReportError($"Call to undeclared function '{funcId.Name}'.", callExpr.Span);
            }

            foreach (var arg in callExpr.Arguments)
            {
                ResolveExpr(arg, scope, expectedType: null);
            }
            return ResolvedType.Error;
        }

        // Indirect call through any function-typed expression (e.g., callbacks[i](), get_fn()())
        var calleeType = ResolveExpr(callExpr.Callee, scope, expectedType: null);
        if (calleeType.IsError)
        {
            foreach (var arg in callExpr.Arguments)
            {
                ResolveExpr(arg, scope, expectedType: null);
            }
            return ResolvedType.Error;
        }

        return ValidateFunctionValueCall(calleeType.DisplayName, calleeType, callExpr.Arguments, scope, callExpr.Span);
    }

    private ResolvedType ValidateFunctionValueCall(
        string callableName,
        ResolvedType calleeType,
        IReadOnlyList<Expr> arguments,
        LexicalScope scope,
        SourceSpan callSpan)
    {
        if (calleeType.IsError)
        {
            foreach (var arg in arguments)
                ResolveExpr(arg, scope, expectedType: null);
            return ResolvedType.Error;
        }

        if (calleeType is not FunctionType fnType)
        {
            ReportError($"Cannot call value '{callableName}' of non-function type '{calleeType}'.", callSpan);
            foreach (var arg in arguments)
                ResolveExpr(arg, scope, expectedType: null);
            return ResolvedType.Error;
        }

        int expectedCount = fnType.ParameterTypes.Count;
        if (arguments.Count != expectedCount)
        {
            ReportError(
                $"Function value '{callableName}' expects {expectedCount} argument(s), got {arguments.Count}.",
                callSpan
            );
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            var expectedParamType = i < expectedCount ? fnType.ParameterTypes[i] : null;
            var argType = ResolveExpr(arguments[i], scope, expectedType: expectedParamType);
            if (expectedParamType is not null && !ResolvedType.IsAssignableTo(argType, expectedParamType))
            {
                ReportError(
                    $"Argument {i + 1} of '{callableName}' expects '{expectedParamType}', got '{argType}'.",
                    arguments[i].Span
                );
            }
        }

        return fnType.ReturnType;
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

        // Built-in methods on 'sound' (SFX) and 'music' (BGM) assets
        if (receiverType is AssetType { AssetKind: "sound" or "music" } audioAsset)
        {
            switch (methodName)
            {
                case "play":
                    if (callExpr.Arguments.Count > 1)
                    {
                        ReportError($"Method '{audioAsset.AssetKind}.play([volume])' takes 0 or 1 argument, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else if (callExpr.Arguments.Count == 1)
                    {
                        var volType = ResolveExpr(callExpr.Arguments[0], scope, expectedType: ResolvedType.F32);
                        if (!ResolvedType.IsAssignableTo(volType, ResolvedType.F32))
                            ReportError($"Argument 'volume' to '{audioAsset.AssetKind}.play' must be 'f32', got '{volType}'.", callExpr.Arguments[0].Span);
                    }
                    _model.RecordType(memberAccess, new FunctionType([], ResolvedType.Void));
                    return ResolvedType.Void;

                case "stop" or "pause" or "resume" when audioAsset.AssetKind == "music":
                    if (callExpr.Arguments.Count != 0)
                    {
                        ReportError($"Method 'music.{methodName}()' takes 0 arguments, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    _model.RecordType(memberAccess, new FunctionType([], ResolvedType.Void));
                    return ResolvedType.Void;
            }
        }

        // Built-in methods on 'tilemap' assets
        if (receiverType is AssetType { AssetKind: "tilemap" })
        {
            bool isMutable = memberAccess.Target is IdentifierExpr mapId &&
                             _symbols.Assets.TryGetValue(mapId.Name, out var mapSym) &&
                             mapSym.IsMutable;
            string mapDisplayName = memberAccess.Target is IdentifierExpr idDisp ? idDisp.Name : "tilemap";

            switch (methodName)
            {
                case "draw":
                    if (callExpr.Arguments.Count > 1)
                    {
                        ReportError($"Method 'tilemap.draw([origin])' takes 0 or 1 argument, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else if (callExpr.Arguments.Count == 1)
                    {
                        var argType = ResolveExpr(callExpr.Arguments[0], scope, expectedType: ResolvedType.Vec2);
                        if (!ResolvedType.IsAssignableTo(argType, ResolvedType.Vec2))
                            ReportError($"Argument 'origin' to 'tilemap.draw' must be 'vec2', got '{argType}'.", callExpr.Arguments[0].Span);
                    }
                    _model.RecordType(memberAccess, new FunctionType([], ResolvedType.Void));
                    return ResolvedType.Void;

                case "draw_layer":
                    if (callExpr.Arguments.Count is < 1 or > 2)
                    {
                        ReportError($"Method 'tilemap.draw_layer(layer[, origin])' takes 1 or 2 arguments, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else
                    {
                        var layerType = ResolveExpr(callExpr.Arguments[0], scope, expectedType: ResolvedType.Int);
                        if (!layerType.IsError && !layerType.IsInteger)
                            ReportError($"Argument 'layer' to 'tilemap.draw_layer' must be an integer, got '{layerType}'.", callExpr.Arguments[0].Span);

                        if (callExpr.Arguments.Count == 2)
                        {
                            var originType = ResolveExpr(callExpr.Arguments[1], scope, expectedType: ResolvedType.Vec2);
                            if (!ResolvedType.IsAssignableTo(originType, ResolvedType.Vec2))
                                ReportError($"Argument 'origin' to 'tilemap.draw_layer' must be 'vec2', got '{originType}'.", callExpr.Arguments[1].Span);
                        }
                    }
                    _model.RecordType(memberAccess, new FunctionType([ResolvedType.Int], ResolvedType.Void));
                    return ResolvedType.Void;

                case "get_tile":
                    if (callExpr.Arguments.Count != 3)
                    {
                        ReportError($"Method 'tilemap.get_tile(layer, col, row)' takes 3 arguments, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            var t = ResolveExpr(callExpr.Arguments[i], scope, expectedType: ResolvedType.Int);
                            if (!t.IsError && !t.IsInteger)
                                ReportError($"Argument {i + 1} to 'tilemap.get_tile' must be an integer, got '{t}'.", callExpr.Arguments[i].Span);
                        }
                    }
                    _model.RecordType(memberAccess, new FunctionType([ResolvedType.Int, ResolvedType.Int, ResolvedType.Int], ResolvedType.Int));
                    return ResolvedType.Int;

                case "is_solid":
                    if (callExpr.Arguments.Count != 2)
                    {
                        ReportError($"Method 'tilemap.is_solid(col, row)' takes 2 arguments, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            var t = ResolveExpr(callExpr.Arguments[i], scope, expectedType: ResolvedType.Int);
                            if (!t.IsError && !t.IsInteger)
                                ReportError($"Argument {i + 1} to 'tilemap.is_solid' must be an integer, got '{t}'.", callExpr.Arguments[i].Span);
                        }
                    }
                    _model.RecordType(memberAccess, new FunctionType([ResolvedType.Int, ResolvedType.Int], ResolvedType.Bool));
                    return ResolvedType.Bool;

                case "collides_rect":
                    if (callExpr.Arguments.Count != 1)
                    {
                        ReportError($"Method 'tilemap.collides_rect(hitbox)' takes 1 argument, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else
                    {
                        var argType = ResolveExpr(callExpr.Arguments[0], scope, expectedType: ResolvedType.Rect);
                        if (!ResolvedType.IsAssignableTo(argType, ResolvedType.Rect))
                            ReportError($"Argument to 'tilemap.collides_rect' must be 'rect', got '{argType}'.", callExpr.Arguments[0].Span);
                    }
                    _model.RecordType(memberAccess, new FunctionType([ResolvedType.Rect], ResolvedType.Bool));
                    return ResolvedType.Bool;

                case "set_tile":
                    if (!isMutable)
                        ReportError($"Cannot call 'set_tile' on read-only tilemap '{mapDisplayName}'. Declare it with 'mutable: true'.", callExpr.Span);

                    if (callExpr.Arguments.Count != 4)
                    {
                        ReportError($"Method 'tilemap.set_tile(layer, col, row, tile_id)' takes 4 arguments, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            var t = ResolveExpr(callExpr.Arguments[i], scope, expectedType: ResolvedType.Int);
                            if (!t.IsError && !t.IsInteger)
                                ReportError($"Argument {i + 1} to 'tilemap.set_tile' must be an integer, got '{t}'.", callExpr.Arguments[i].Span);
                        }
                    }
                    _model.RecordType(memberAccess, new FunctionType([ResolvedType.Int, ResolvedType.Int, ResolvedType.Int, ResolvedType.Int], ResolvedType.Void));
                    return ResolvedType.Void;

                case "set_solid":
                    if (!isMutable)
                        ReportError($"Cannot call 'set_solid' on read-only tilemap '{mapDisplayName}'. Declare it with 'mutable: true'.", callExpr.Span);

                    if (callExpr.Arguments.Count != 3)
                    {
                        ReportError($"Method 'tilemap.set_solid(col, row, solid)' takes 3 arguments, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    else
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            var t = ResolveExpr(callExpr.Arguments[i], scope, expectedType: ResolvedType.Int);
                            if (!t.IsError && !t.IsInteger)
                                ReportError($"Argument {i + 1} to 'tilemap.set_solid' must be an integer, got '{t}'.", callExpr.Arguments[i].Span);
                        }
                        var solidType = ResolveExpr(callExpr.Arguments[2], scope, expectedType: ResolvedType.Bool);
                        if (!solidType.IsError && solidType != ResolvedType.Bool)
                            ReportError($"Argument 'solid' to 'tilemap.set_solid' must be 'bool', got '{solidType}'.", callExpr.Arguments[2].Span);
                    }
                    _model.RecordType(memberAccess, new FunctionType([ResolvedType.Int, ResolvedType.Int, ResolvedType.Bool], ResolvedType.Void));
                    return ResolvedType.Void;

                case "reset":
                    if (!isMutable)
                        ReportError($"Cannot call 'reset' on read-only tilemap '{mapDisplayName}'. Declare it with 'mutable: true'.", callExpr.Span);

                    if (callExpr.Arguments.Count != 0)
                    {
                        ReportError($"Method 'tilemap.reset()' takes 0 arguments, got {callExpr.Arguments.Count}.", callExpr.Span);
                        foreach (var arg in callExpr.Arguments)
                            ResolveExpr(arg, scope, expectedType: null);
                    }
                    _model.RecordType(memberAccess, new FunctionType([], ResolvedType.Void));
                    return ResolvedType.Void;
            }
        }

        // Actor methods via ref<ActorName>
        if (receiverType is ActorRefType actorRef &&
            _symbols.Actors.TryGetValue(actorRef.ActorName, out var actorSym))
        {
            if (actorSym.Methods.TryGetValue(methodName, out var actorMethod))
            {
                _model.RecordType(memberAccess, actorMethod.Signature);
                ValidateCallArguments($"{actorSym.Name}.{methodName}", actorMethod, callExpr.Arguments, scope, callExpr.Span);
                return actorMethod.ReturnType;
            }

            if (actorSym.Fields.TryGetValue(methodName, out var actorField) && actorField.Type is FunctionType actorFnField)
            {
                _model.RecordType(memberAccess, actorFnField);
                return ValidateFunctionValueCall($"{actorSym.Name}.{methodName}", actorFnField, callExpr.Arguments, scope, callExpr.Span);
            }
        }

        // Function-typed fields on structs and messages (e.g. cfg.on_done())
        if (receiverType is StructType structType &&
            _symbols.Structs.TryGetValue(structType.Name, out var structSym) &&
            structSym.Fields.TryGetValue(methodName, out var structField) &&
            structField.Type is FunctionType structFnField)
        {
            _model.RecordType(memberAccess, structFnField);
            return ValidateFunctionValueCall($"{structSym.Name}.{methodName}", structFnField, callExpr.Arguments, scope, callExpr.Span);
        }

        if (receiverType is MessageType msgType &&
            _symbols.Messages.TryGetValue(msgType.Name, out var msgSym) &&
            msgSym.Fields.TryGetValue(methodName, out var msgField) &&
            msgField.Type is FunctionType msgFnField)
        {
            _model.RecordType(memberAccess, msgFnField);
            return ValidateFunctionValueCall($"{msgSym.Name}.{methodName}", msgFnField, callExpr.Arguments, scope, callExpr.Span);
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

        if (string.Equals(callableName, "draw_subsprite", StringComparison.Ordinal) && arguments.Count > 3)
        {
            ValidateDrawSubspriteOptionalArgs(arguments, callSpan);
        }
    }

    private void ValidateDrawSubspriteOptionalArgs(IReadOnlyList<Expr> arguments, SourceSpan callSpan)
    {
        if (arguments.Count > 6)
        {
            ReportError($"Function 'draw_subsprite' expects at most 6 arguments, got {arguments.Count}.", callSpan);
            return;
        }

        ResolvedType arg3Type = _model.GetTypeOrDefault(arguments[3]);
        if (arg3Type.IsError)
            return;

        if (arg3Type == ResolvedType.Color)
        {
            if (arguments.Count >= 5)
            {
                ResolvedType arg4Type = _model.GetTypeOrDefault(arguments[4]);
                if (!ResolvedType.IsAssignableTo(arg4Type, ResolvedType.Bool))
                {
                    ReportError($"Argument 5 ('flip_x') of 'draw_subsprite' expects 'bool', got '{arg4Type}'.", arguments[4].Span);
                }
            }
            if (arguments.Count == 6)
            {
                ResolvedType arg5Type = _model.GetTypeOrDefault(arguments[5]);
                if (!ResolvedType.IsAssignableTo(arg5Type, ResolvedType.Bool))
                {
                    ReportError($"Argument 6 ('flip_y') of 'draw_subsprite' expects 'bool', got '{arg5Type}'.", arguments[5].Span);
                }
            }
        }
        else if (arg3Type == ResolvedType.Bool)
        {
            if (arguments.Count >= 5)
            {
                ResolvedType arg4Type = _model.GetTypeOrDefault(arguments[4]);
                if (!ResolvedType.IsAssignableTo(arg4Type, ResolvedType.Bool))
                {
                    ReportError($"Argument 5 ('flip_y') of 'draw_subsprite' expects 'bool', got '{arg4Type}'.", arguments[4].Span);
                }
            }
            if (arguments.Count == 6)
            {
                ReportError("Function 'draw_subsprite(tex, src, pos, flip_x, flip_y)' expects at most 5 arguments when tint is omitted.", callSpan);
            }
        }
        else
        {
            ReportError(
                $"Argument 4 of 'draw_subsprite' expects 'color' (tint) or 'bool' (flip_x), got '{arg3Type}'.",
                arguments[3].Span
            );
        }
    }

    // ========================================================================
    // Compile-Time Constant Folding & Evaluation
    // ========================================================================

    private bool TryGetOrEvaluateTopLevelConst(string name, out ConstSymbol? symbol)
    {
        if (_symbols.Constants.TryGetValue(name, out symbol))
            return true;

        if (!_pendingConstants.TryGetValue(name, out var decl))
        {
            symbol = null;
            return false;
        }

        if (!_evaluatingConstants.Add(name))
        {
            ReportError($"Circular constant definition involving '{name}'.", decl.Span);
            symbol = null;
            return false;
        }

        try
        {
            ResolvedType? explicitType = decl.ExplicitType is not null
                ? ResolveTypeRef(decl.ExplicitType)
                : null;

            if (explicitType == ResolvedType.Void)
            {
                ReportError($"Constant '{decl.Name}' cannot have type 'void'.", decl.Span);
                explicitType = ResolvedType.Error;
            }

            if (!TryEvaluateConstExpr(decl.Initializer, scope: null, explicitType, out var evaluated, out var initType) ||
                evaluated is null)
            {
                ReportError(
                    $"Constant '{decl.Name}' initializer must be a compile-time constant expression.",
                    decl.Initializer.Span
                );
                var errSym = new ConstSymbol(decl.Name, explicitType ?? ResolvedType.Error, new IntConstValue(0), decl);
                _symbols.Constants[decl.Name] = errSym;
                _model.RecordType(decl, errSym.Type);
                symbol = errSym;
                return false;
            }

            ResolvedType finalType;
            if (explicitType is not null)
            {
                if (!ResolvedType.IsAssignableTo(initType, explicitType))
                {
                    ReportError(
                        $"Cannot initialize constant '{decl.Name}' of type '{explicitType}' with value of type '{initType}'.",
                        decl.Span
                    );
                }
                finalType = explicitType;
            }
            else
            {
                finalType = initType;
            }

            symbol = new ConstSymbol(decl.Name, finalType, evaluated, decl);
            _symbols.Constants[decl.Name] = symbol;
            _model.RecordType(decl, finalType);
            return true;
        }
        finally
        {
            _evaluatingConstants.Remove(name);
            _pendingConstants.Remove(name);
        }
    }

    private bool TryLookupConstValue(string name, LexicalScope? scope, out ConstValue? value, out ResolvedType type)
    {
        if (scope is not null && scope.TryLookupLocal(name, out var localSym) && localSym!.ConstantValue is not null)
        {
            value = localSym.ConstantValue;
            type = localSym.Type;
            return true;
        }

        if (TryGetOrEvaluateTopLevelConst(name, out var constSym) && constSym is not null)
        {
            value = constSym.Value;
            type = constSym.Type;
            return true;
        }

        value = null;
        type = ResolvedType.Error;
        return false;
    }

    private bool TryEvaluateIntConstant(Expr expr, LexicalScope? scope, out int value)
    {
        if (TryEvaluateConstExpr(expr, scope, ResolvedType.Int, out var cv, out _) && cv is IntConstValue icv)
        {
            value = (int)icv.Value;
            return true;
        }
        value = 0;
        return false;
    }

    private bool TryEvaluateConstExpr(
        Expr expr,
        LexicalScope? scope,
        ResolvedType? expectedType,
        out ConstValue? value,
        out ResolvedType type)
    {
        switch (expr)
        {
            case IntLiteralExpr il:
                value = new IntConstValue(il.Value);
                type = _model.RecordType(expr, ResolvedType.Int);
                return true;

            case FloatLiteralExpr fl:
                value = new FloatConstValue(fl.Value);
                type = _model.RecordType(expr, ResolvedType.F32);
                return true;

            case TimeLiteralExpr tl:
                value = new TimeConstValue(tl.Seconds);
                type = _model.RecordType(expr, ResolvedType.Timer);
                return true;

            case ColorLiteralExpr cl:
                value = new ColorConstValue(cl.AbgrValue);
                type = _model.RecordType(expr, ResolvedType.Color);
                return true;

            case BooleanLiteralExpr bl:
                value = new BoolConstValue(bl.Value);
                type = _model.RecordType(expr, ResolvedType.Bool);
                return true;

            case StringLiteralExpr sl:
                value = new StringConstValue(sl.Text);
                type = _model.RecordType(expr, ResolvedType.String);
                return true;

            case VecLiteralExpr vl:
            {
                var comps = new List<float>(vl.Components.Count);
                foreach (var c in vl.Components)
                {
                    if (!TryEvaluateConstExpr(c, scope, ResolvedType.F32, out var cv, out _))
                    {
                        value = null;
                        type = ResolvedType.Error;
                        return false;
                    }
                    if (cv is IntConstValue ic) comps.Add(ic.Value);
                    else if (cv is FloatConstValue fc) comps.Add(fc.Value);
                    else
                    {
                        value = null;
                        type = ResolvedType.Error;
                        return false;
                    }
                }
                ResolvedType vecType = vl.Components.Count switch
                {
                    2 => ResolvedType.Vec2,
                    3 => ResolvedType.Vec3,
                    4 => ResolvedType.Rect,
                    _ => ResolvedType.Error
                };
                if (vecType.IsError)
                {
                    value = null;
                    type = ResolvedType.Error;
                    return false;
                }
                value = new VecConstValue(comps);
                type = _model.RecordType(expr, vecType);
                return true;
            }

            case IdentifierExpr id:
                if (TryLookupConstValue(id.Name, scope, out value, out type))
                {
                    _model.RecordType(expr, type);
                    return true;
                }
                return false;

            case FieldAccessExpr { Target: IdentifierExpr enumId } fa when _symbols.Enums.TryGetValue(enumId.Name, out var enumSym):
            {
                int ord = enumSym.Variants.ToList().IndexOf(fa.FieldName);
                if (ord < 0)
                {
                    ReportError($"Enum '{enumSym.Name}' does not contain a variant named '{fa.FieldName}'.", fa.Span);
                    value = null;
                    type = ResolvedType.Error;
                    return false;
                }
                value = new EnumConstValue(enumSym.Name, fa.FieldName, ord);
                type = _model.RecordType(expr, enumSym.Type);
                return true;
            }

            case UnaryExpr ue:
                if (!TryEvaluateConstExpr(ue.Operand, scope, expectedType, out var opVal, out var opType))
                {
                    value = null;
                    type = ResolvedType.Error;
                    return false;
                }
                switch (ue.Operator)
                {
                    case UnaryOp.Negate when opVal is IntConstValue iv:
                        value = new IntConstValue(-iv.Value);
                        type = _model.RecordType(expr, opType);
                        return true;
                    case UnaryOp.Negate when opVal is FloatConstValue fv:
                        value = new FloatConstValue(-fv.Value);
                        type = _model.RecordType(expr, ResolvedType.F32);
                        return true;
                    case UnaryOp.Not when opVal is BoolConstValue bv:
                        value = new BoolConstValue(!bv.Value);
                        type = _model.RecordType(expr, ResolvedType.Bool);
                        return true;
                    case UnaryOp.BitNot when opVal is IntConstValue biv:
                        value = new IntConstValue(~biv.Value);
                        type = _model.RecordType(expr, opType);
                        return true;
                    default:
                        value = null;
                        type = ResolvedType.Error;
                        return false;
                }

            case BinaryExpr be:
                return TryEvaluateBinaryConst(be, scope, out value, out type);

            case TernaryExpr te:
                if (!TryEvaluateConstExpr(te.Condition, scope, ResolvedType.Bool, out var cVal, out var cType) ||
                    cVal is not BoolConstValue bcv)
                {
                    value = null;
                    type = ResolvedType.Error;
                    return false;
                }
                _ = cType;
                return TryEvaluateConstExpr(bcv.Value ? te.WhenTrue : te.WhenFalse, scope, expectedType, out value, out type);

            case CallExpr { Callee: IdentifierExpr { Name: "vec2" or "vec3" or "rect" } ctorId } ctorCall:
            {
                int expectedArgs = ctorId.Name switch { "vec2" => 2, "vec3" => 3, _ => 4 };
                if (ctorCall.Arguments.Count != expectedArgs)
                {
                    value = null;
                    type = ResolvedType.Error;
                    return false;
                }
                var comps = new List<float>(expectedArgs);
                foreach (var arg in ctorCall.Arguments)
                {
                    if (!TryEvaluateConstExpr(arg, scope, ResolvedType.F32, out var argVal, out _))
                    {
                        value = null;
                        type = ResolvedType.Error;
                        return false;
                    }
                    if (argVal is IntConstValue ic) comps.Add(ic.Value);
                    else if (argVal is FloatConstValue fc) comps.Add(fc.Value);
                    else
                    {
                        value = null;
                        type = ResolvedType.Error;
                        return false;
                    }
                }
                ResolvedType ctorType = ctorId.Name switch
                {
                    "vec2" => ResolvedType.Vec2,
                    "vec3" => ResolvedType.Vec3,
                    _ => ResolvedType.Rect
                };
                value = new VecConstValue(comps);
                type = _model.RecordType(expr, ctorType);
                return true;
            }

            case MatchExpr me:
            {
                if (!TryEvaluateConstExpr(me.Scrutinee, scope, null, out var scrVal, out var scrType) || scrVal is null)
                {
                    value = null;
                    type = ResolvedType.Error;
                    return false;
                }
                _ = scrType;
                foreach (var arm in me.Arms)
                {
                    foreach (var pat in arm.Patterns)
                    {
                        bool matched = pat switch
                        {
                            WildcardPattern => true,
                            IntLiteralPattern il when scrVal is IntConstValue iv => iv.Value == il.Value,
                            EnumVariantPattern ev when scrVal is EnumConstValue ecv =>
                                string.Equals(ecv.EnumName, ev.EnumName, StringComparison.Ordinal) &&
                                string.Equals(ecv.VariantName, ev.VariantName, StringComparison.Ordinal),
                            ConstPattern cp when TryLookupConstValue(cp.Name, scope, out var pcv, out _) =>
                                (pcv is IntConstValue piv && scrVal is IntConstValue siv && piv.Value == siv.Value) ||
                                (pcv is EnumConstValue pev && scrVal is EnumConstValue sev &&
                                 string.Equals(pev.EnumName, sev.EnumName, StringComparison.Ordinal) &&
                                 string.Equals(pev.VariantName, sev.VariantName, StringComparison.Ordinal)),
                            _ => false
                        };
                        if (matched)
                        {
                            return TryEvaluateConstExpr(arm.Value, scope, expectedType, out value, out type);
                        }
                    }
                }
                value = null;
                type = ResolvedType.Error;
                return false;
            }

            default:
                value = null;
                type = ResolvedType.Error;
                return false;
        }
    }

    private bool TryEvaluateBinaryConst(
        BinaryExpr be,
        LexicalScope? scope,
        out ConstValue? value,
        out ResolvedType type)
    {
        if (!TryEvaluateConstExpr(be.Left, scope, null, out var lVal, out var lType) ||
            !TryEvaluateConstExpr(be.Right, scope, null, out var rVal, out var rType))
        {
            value = null;
            type = ResolvedType.Error;
            return false;
        }

        // Integer-integer operations
        if (lVal is IntConstValue li && rVal is IntConstValue ri)
        {
            switch (be.Op)
            {
                case BinaryOp.Add: value = new IntConstValue(li.Value + ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.Sub: value = new IntConstValue(li.Value - ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.Mul: value = new IntConstValue(li.Value * ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.Div:
                    if (ri.Value == 0)
                    {
                        ReportError("Division by zero in compile-time constant expression.", be.Span);
                        value = null; type = ResolvedType.Error; return false;
                    }
                    value = new IntConstValue(li.Value / ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.Mod:
                    if (ri.Value == 0)
                    {
                        ReportError("Modulo by zero in compile-time constant expression.", be.Span);
                        value = null; type = ResolvedType.Error; return false;
                    }
                    value = new IntConstValue(li.Value % ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.BitAnd: value = new IntConstValue(li.Value & ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.BitOr:  value = new IntConstValue(li.Value | ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.BitXor: value = new IntConstValue(li.Value ^ ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.ShiftLeft:  value = new IntConstValue(li.Value << (int)ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.ShiftRight: value = new IntConstValue(li.Value >> (int)ri.Value); type = _model.RecordType(be, ResolvedType.Int); return true;
                case BinaryOp.Equal:        value = new BoolConstValue(li.Value == ri.Value); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.NotEqual:     value = new BoolConstValue(li.Value != ri.Value); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.Less:         value = new BoolConstValue(li.Value < ri.Value);  type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.LessEqual:    value = new BoolConstValue(li.Value <= ri.Value); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.Greater:      value = new BoolConstValue(li.Value > ri.Value);  type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.GreaterEqual: value = new BoolConstValue(li.Value >= ri.Value); type = _model.RecordType(be, ResolvedType.Bool); return true;
            }
        }

        // Float / mixed numeric operations
        if ((lVal is FloatConstValue or IntConstValue) && (rVal is FloatConstValue or IntConstValue))
        {
            float lf = lVal is FloatConstValue lfc ? lfc.Value : ((IntConstValue)lVal).Value;
            float rf = rVal is FloatConstValue rfc ? rfc.Value : ((IntConstValue)rVal).Value;
            switch (be.Op)
            {
                case BinaryOp.Add: value = new FloatConstValue(lf + rf); type = _model.RecordType(be, ResolvedType.F32); return true;
                case BinaryOp.Sub: value = new FloatConstValue(lf - rf); type = _model.RecordType(be, ResolvedType.F32); return true;
                case BinaryOp.Mul: value = new FloatConstValue(lf * rf); type = _model.RecordType(be, ResolvedType.F32); return true;
                case BinaryOp.Div:
                    if (rf == 0.0f)
                    {
                        ReportError("Division by zero in compile-time constant expression.", be.Span);
                        value = null; type = ResolvedType.Error; return false;
                    }
                    value = new FloatConstValue(lf / rf); type = _model.RecordType(be, ResolvedType.F32); return true;
                case BinaryOp.Equal:        value = new BoolConstValue(lf == rf); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.NotEqual:     value = new BoolConstValue(lf != rf); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.Less:         value = new BoolConstValue(lf < rf);  type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.LessEqual:    value = new BoolConstValue(lf <= rf); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.Greater:      value = new BoolConstValue(lf > rf);  type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.GreaterEqual: value = new BoolConstValue(lf >= rf); type = _model.RecordType(be, ResolvedType.Bool); return true;
            }
        }

        // Boolean operations
        if (lVal is BoolConstValue lb && rVal is BoolConstValue rb)
        {
            switch (be.Op)
            {
                case BinaryOp.LogicalAnd: value = new BoolConstValue(lb.Value && rb.Value); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.LogicalOr:  value = new BoolConstValue(lb.Value || rb.Value); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.Equal:      value = new BoolConstValue(lb.Value == rb.Value); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.NotEqual:   value = new BoolConstValue(lb.Value != rb.Value); type = _model.RecordType(be, ResolvedType.Bool); return true;
            }
        }

        // Enum equality
        if (lVal is EnumConstValue le && rVal is EnumConstValue re && string.Equals(le.EnumName, re.EnumName, StringComparison.Ordinal))
        {
            switch (be.Op)
            {
                case BinaryOp.Equal:    value = new BoolConstValue(le.Ordinal == re.Ordinal); type = _model.RecordType(be, ResolvedType.Bool); return true;
                case BinaryOp.NotEqual: value = new BoolConstValue(le.Ordinal != re.Ordinal); type = _model.RecordType(be, ResolvedType.Bool); return true;
            }
        }

        _ = lType;
        _ = rType;
        value = null;
        type = ResolvedType.Error;
        return false;
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

    private void ValidateAssetAndConfigPathsOnDisk()
    {
        string? entryDir = _symbols.GameConfig?.SourceDirectory;

        foreach (var asset in _symbols.Assets.Values)
        {
            string? sourceDir = asset.Declaration.SourceDirectory ?? Path.GetDirectoryName(asset.Declaration.Span.FilePath);
            if (string.IsNullOrEmpty(sourceDir) && string.IsNullOrEmpty(entryDir))
                continue;

            bool found = TryResolveFileOnDisk(asset.FilePath, sourceDir, entryDir);
            if (!found)
            {
                ReportWarning(
                    $"Asset file '{asset.FilePath}' for '{asset.Name}' was not found on disk.",
                    asset.Declaration.Span
                );
            }
        }

        if (_symbols.GameConfig is not null)
        {
            string? cfgDir = _symbols.GameConfig.SourceDirectory ?? Path.GetDirectoryName(_symbols.GameConfig.Span.FilePath);
            if (!string.IsNullOrEmpty(cfgDir))
            {
                CheckConfigPngOnDisk(_symbols.IconPath, "icon", cfgDir, entryDir, _symbols.GameConfig.Span);
                CheckConfigPngOnDisk(_symbols.BackgroundPath, "background", cfgDir, entryDir, _symbols.GameConfig.Span);
                CheckConfigPngOnDisk(_symbols.OverlayPath, "overlay", cfgDir, entryDir, _symbols.GameConfig.Span);
            }
        }
    }

    private void CheckConfigPngOnDisk(string? relPath, string key, string? cfgDir, string? entryDir, SourceSpan span)
    {
        if (string.IsNullOrEmpty(relPath))
            return;

        if (!TryResolveFileOnDisk(relPath, cfgDir, entryDir))
        {
            ReportWarning($"Game config '{key}' file '{relPath}' was not found on disk.", span);
        }
    }

    private static bool TryResolveFileOnDisk(string rawPath, string? primaryDir, string? fallbackDir)
    {
        if (Path.IsPathRooted(rawPath))
            return File.Exists(rawPath);

        if (!string.IsNullOrEmpty(primaryDir))
        {
            string candidate = Path.GetFullPath(Path.Combine(primaryDir, rawPath));
            if (File.Exists(candidate))
                return true;
        }

        if (!string.IsNullOrEmpty(fallbackDir))
        {
            string candidate = Path.GetFullPath(Path.Combine(fallbackDir, rawPath));
            if (File.Exists(candidate))
                return true;
        }

        return false;
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
        BinaryOp.ShiftLeft => "<<",
        BinaryOp.ShiftRight => ">>",
        BinaryOp.BitAnd => "&",
        BinaryOp.BitXor => "^",
        BinaryOp.BitOr => "|",
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
        AssignOp.BitAndAssign => "&=",
        AssignOp.BitOrAssign => "|=",
        AssignOp.BitXorAssign => "^=",
        AssignOp.ShiftLeftAssign => "<<=",
        AssignOp.ShiftRightAssign => ">>=",
        _ => op.ToString()
    };
}
