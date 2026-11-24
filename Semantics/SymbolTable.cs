using PspScript.Ast;

namespace PspScript.Semantics;

public sealed record AssetSymbol(
    string Name,
    string AssetKind,
    string FilePath,
    AssetType Type,
    AssetDecl Declaration,
    bool IsMutable = false
);

public sealed record FieldSymbol(
    string Name,
    ResolvedType Type,
    bool HasDefaultValue,
    FieldDecl Declaration
);

public sealed record MessageSymbol(
    string Name,
    IReadOnlyDictionary<string, FieldSymbol> Fields,
    MessageDecl Declaration
);

public sealed record StructSymbol(
    string Name,
    StructType Type,
    IReadOnlyDictionary<string, FieldSymbol> Fields,
    StructDecl Declaration
);

public sealed record EnumSymbol(
    string Name,
    EnumType Type,
    IReadOnlyList<string> Variants,
    EnumDecl Declaration
);

public sealed record FunctionSymbol(
    string Name,
    IReadOnlyList<ParamSymbol> Parameters,
    ResolvedType ReturnType,
    bool IsBuiltin = false,
    bool IsVariadic = false,
    FuncDecl? Declaration = null
)
{
    public FunctionType Signature => new(
        Parameters.Select(static p => p.Type).ToList(),
        ReturnType,
        IsVariadic
    );
}

public sealed record ParamSymbol(
    string Name,
    ResolvedType Type,
    ParamDecl? Declaration = null
);

public sealed record ActorSymbol(
    string Name,
    int? PoolCapacity,
    Dictionary<string, FieldSymbol> Fields,
    Dictionary<LifecycleKind, LifecycleHook> Hooks,
    Dictionary<string, MessageHandler> Handlers,
    Dictionary<string, FunctionSymbol> Methods,
    ActorDecl Declaration
)
{
    public bool IsPooled => PoolCapacity.HasValue;
    public bool IsSingleton => !PoolCapacity.HasValue;
    public ActorRefType RefType => new(Name);
}

public sealed record StateSymbol(
    string Name,
    Dictionary<string, FieldSymbol> Fields,
    Dictionary<LifecycleKind, LifecycleHook> Hooks,
    Dictionary<string, MessageHandler> Handlers,
    Dictionary<string, FunctionSymbol> Methods,
    StateDecl Declaration
);

public sealed class VariableSymbol
{
    public string Name { get; }
    public MemoryScope Scope { get; }
    public ResolvedType Type { get; set; }
    public bool IsReadOnly { get; }
    public AstNode? Declaration { get; }

    public VariableSymbol(
        string Name,
        MemoryScope Scope,
        ResolvedType Type,
        bool IsReadOnly = false,
        AstNode? Declaration = null)
    {
        this.Name = Name;
        this.Scope = Scope;
        this.Type = Type;
        this.IsReadOnly = IsReadOnly;
        this.Declaration = Declaration;
    }
}


public sealed class LexicalScope
{
    private readonly Dictionary<string, VariableSymbol> _locals = new(StringComparer.Ordinal);

    public LexicalScope? Parent { get; }
    public ActorSymbol? EnclosingActor { get; }
    public StateSymbol? EnclosingState { get; }
    public ResolvedType ExpectedReturnType { get; }
    public bool IsInsideLoop { get; }

    public LexicalScope(
        LexicalScope? parent,
        ActorSymbol? enclosingActor = null,
        StateSymbol? enclosingState = null,
        ResolvedType? expectedReturnType = null,
        bool? isInsideLoop = null)
    {
        Parent = parent;
        EnclosingActor = enclosingActor ?? parent?.EnclosingActor;
        EnclosingState = enclosingState ?? parent?.EnclosingState;
        ExpectedReturnType = expectedReturnType ?? parent?.ExpectedReturnType ?? ResolvedType.Void;
        IsInsideLoop = isInsideLoop ?? parent?.IsInsideLoop ?? false;
    }

    public bool TryDeclareLocal(VariableSymbol symbol)
    {
        return _locals.TryAdd(symbol.Name, symbol);
    }

    public bool TryLookupLocal(string name, out VariableSymbol? symbol)
    {
        if (_locals.TryGetValue(name, out symbol))
            return true;

        if (Parent is not null)
            return Parent.TryLookupLocal(name, out symbol);

        symbol = null;
        return false;
    }

    public LexicalScope CreateChild(ResolvedType? overrideReturnType = null, bool? isInsideLoop = null) =>
        new(this, EnclosingActor, EnclosingState, overrideReturnType ?? ExpectedReturnType, isInsideLoop);
}


public sealed class SymbolTable
{
    private readonly Dictionary<string, TopDecl> _allTopLevelNames = new(StringComparer.Ordinal);

    public GameConfigDecl? GameConfig { get; set; }
    public long? ConfiguredRamBudgetBytes { get; set; }
    public string? EntryStateName { get; set; }
    public string GameId { get; set; } = "PSPS00001";
    public string GameVersion { get; set; } = "01.00";
    public bool ExtraRam { get; set; } = true;
    public int WindowScale { get; set; } = 2;
    public string? IconPath { get; set; }
    public string? BackgroundPath { get; set; }
    public string? OverlayPath { get; set; }

    public Dictionary<string, AssetSymbol> Assets { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, MessageSymbol> Messages { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, StructSymbol> Structs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, EnumSymbol> Enums { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ActorSymbol> Actors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, StateSymbol> States { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, VariableSymbol> Globals { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FunctionSymbol> Functions { get; } = new(StringComparer.Ordinal);

    public SymbolTable()
    {
        RegisterBuiltinIntrinsics();
    }

    public bool TryRegisterTopLevelName(string name, TopDecl declaration)
    {
        return _allTopLevelNames.TryAdd(name, declaration);
    }

    
    public long EstimateTypeByteSize(ResolvedType type, HashSet<string>? visitedStructs = null)
    {
        return type switch
        {
            PrimitiveType prim => prim.ByteSize,
            EnumType => 4,     // 32-bit int32_t enum value
            ActorRefType => 4, // 32-bit generational handle (16-bit index + 16-bit generation)
            AssetType => 4,    // 32-bit static asset table index / pointer
            FixedArrayType arr => Math.Max(0, arr.Length) * EstimateTypeByteSize(arr.ElementType, visitedStructs),
            StructType st when Structs.TryGetValue(st.Name, out var structSym) =>
                EstimateStructByteSize(structSym, visitedStructs),
            MessageType msg when Messages.TryGetValue(msg.Name, out var msgSym) =>
                Align4(msgSym.Fields.Values.Sum(f => EstimateTypeByteSize(f.Type, visitedStructs))),
            _ => 4
        };
    }

    private long EstimateStructByteSize(StructSymbol structSym, HashSet<string>? visitedStructs)
    {
        visitedStructs ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visitedStructs.Add(structSym.Name))
            return 0; // Guard against cyclic struct definitions

        long total = 0;
        foreach (var field in structSym.Fields.Values)
        {
            total += EstimateTypeByteSize(field.Type, visitedStructs);
        }

        visitedStructs.Remove(structSym.Name);
        return Align4(total);
    }

    
    public long EstimateTotalStaticMemoryBytes()
    {
        long total = 0;

        // Global & Level variables
        foreach (var global in Globals.Values)
        {
            total += Align4(EstimateTypeByteSize(global.Type));
        }

        // State fields
        foreach (var state in States.Values)
        {
            long stateBytes = 0;
            foreach (var field in state.Fields.Values)
            {
                stateBytes += EstimateTypeByteSize(field.Type);
            }
            total += Align4(stateBytes);
        }

        // Static actor pools (each slot has 4-byte generational header + aligned fields)
        foreach (var actor in Actors.Values)
        {
            long slotBytes = 4; // active + generation header
            foreach (var field in actor.Fields.Values)
            {
                slotBytes += EstimateTypeByteSize(field.Type);
            }
            slotBytes = Align4(slotBytes);
            int capacity = actor.PoolCapacity ?? 1;
            total += slotBytes * Math.Max(1, capacity);
        }

        return total;
    }

    private static long Align4(long size) => (size + 3) & ~3L;

    private void RegisterBuiltinIntrinsics()
    {
        // PSP 2D Rendering, Camera, Tilemap, Font, Save & Input / Math runtime intrinsics
        RegisterBuiltin("sprite", ResolvedType.Void, isVariadic: true,
            new ParamSymbol("texture", new AssetType("texture")),
            new ParamSymbol("pos", ResolvedType.Vec2));

        RegisterBuiltin("draw_subsprite", ResolvedType.Void, isVariadic: true,
            new ParamSymbol("texture", new AssetType("texture")),
            new ParamSymbol("src", ResolvedType.Rect),
            new ParamSymbol("pos", ResolvedType.Vec2));

        RegisterBuiltin("draw_tilemap", ResolvedType.Void, isVariadic: true,
            new ParamSymbol("tileset", new AssetType("texture")),
            new ParamSymbol("tiles", ResolvedType.Any),
            new ParamSymbol("cols", ResolvedType.Int),
            new ParamSymbol("rows", ResolvedType.Int),
            new ParamSymbol("tile_size", ResolvedType.Int));

        RegisterBuiltin("set_camera", ResolvedType.Void, isVariadic: false,
            new ParamSymbol("pos", ResolvedType.Vec2));

        RegisterBuiltin("get_camera", ResolvedType.Vec2, isVariadic: false);

        RegisterBuiltin("set_camera_enabled", ResolvedType.Void, isVariadic: false,
            new ParamSymbol("enabled", ResolvedType.Bool));

        RegisterBuiltin("draw_rect", ResolvedType.Void, isVariadic: false,
            new ParamSymbol("bounds", ResolvedType.Rect),
            new ParamSymbol("tint", ResolvedType.Color));

        RegisterBuiltin("draw_text", ResolvedType.Void, isVariadic: true,
            new ParamSymbol("text", ResolvedType.String),
            new ParamSymbol("pos", ResolvedType.Vec2));

        RegisterBuiltin("draw_number", ResolvedType.Void, isVariadic: true,
            new ParamSymbol("value", ResolvedType.Int),
            new ParamSymbol("pos", ResolvedType.Vec2));

        RegisterBuiltin("clear_screen", ResolvedType.Void, isVariadic: false,
            new ParamSymbol("color", ResolvedType.Color));

        RegisterBuiltin("save_game", ResolvedType.Bool, isVariadic: false);

        RegisterBuiltin("load_game", ResolvedType.Bool, isVariadic: false);

        RegisterBuiltin("play_sound", ResolvedType.Void, isVariadic: true,
            new ParamSymbol("sound", new AssetType("sound")));

        RegisterBuiltin("set_master_volume", ResolvedType.Void, isVariadic: false,
            new ParamSymbol("volume", ResolvedType.F32));

        RegisterBuiltin("stop_all_sounds", ResolvedType.Void, isVariadic: false);

        RegisterBuiltin("button_pressed", ResolvedType.Bool, isVariadic: false,
            new ParamSymbol("button", ResolvedType.Int));

        RegisterBuiltin("button_held", ResolvedType.Bool, isVariadic: false,
            new ParamSymbol("button", ResolvedType.Int));

        RegisterBuiltin("sin", ResolvedType.F32, isVariadic: false,
            new ParamSymbol("radians", ResolvedType.F32));

        RegisterBuiltin("cos", ResolvedType.F32, isVariadic: false,
            new ParamSymbol("radians", ResolvedType.F32));

        RegisterBuiltin("sqrt", ResolvedType.F32, isVariadic: false,
            new ParamSymbol("value", ResolvedType.F32));

        RegisterBuiltin("abs", ResolvedType.F32, isVariadic: false,
            new ParamSymbol("value", ResolvedType.F32));

        RegisterBuiltin("min", ResolvedType.F32, isVariadic: false,
            new ParamSymbol("a", ResolvedType.F32),
            new ParamSymbol("b", ResolvedType.F32));

        RegisterBuiltin("max", ResolvedType.F32, isVariadic: false,
            new ParamSymbol("a", ResolvedType.F32),
            new ParamSymbol("b", ResolvedType.F32));

        RegisterBuiltin("clamp", ResolvedType.F32, isVariadic: false,
            new ParamSymbol("val", ResolvedType.F32),
            new ParamSymbol("low", ResolvedType.F32),
            new ParamSymbol("high", ResolvedType.F32));

        RegisterBuiltin("lerp", ResolvedType.F32, isVariadic: false,
            new ParamSymbol("a", ResolvedType.F32),
            new ParamSymbol("b", ResolvedType.F32),
            new ParamSymbol("t", ResolvedType.F32));

        RegisterBuiltin("print", ResolvedType.Void, isVariadic: true,
            new ParamSymbol("value", ResolvedType.Any));
    }

    private void RegisterBuiltin(string name, ResolvedType returnType, bool isVariadic, params ParamSymbol[] parameters)
    {
        Functions[name] = new FunctionSymbol(
            Name: name,
            Parameters: parameters,
            ReturnType: returnType,
            IsBuiltin: true,
            IsVariadic: isVariadic
        );
    }
}
