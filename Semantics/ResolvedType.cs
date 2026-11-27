namespace PspScript.Semantics;

public enum PrimitiveKind
{
    Int,
    U8,
    U16,
    U32,
    F32,
    Bool,
    Void,
    String,
    Vec2,
    Vec3,
    Rect,
    Color,
    Timer
}

public abstract record ResolvedType
{
    public static readonly PrimitiveType Int = new(PrimitiveKind.Int, "int", 4);
    public static readonly PrimitiveType U8 = new(PrimitiveKind.U8, "u8", 1);
    public static readonly PrimitiveType U16 = new(PrimitiveKind.U16, "u16", 2);
    public static readonly PrimitiveType U32 = new(PrimitiveKind.U32, "u32", 4);
    public static readonly PrimitiveType F32 = new(PrimitiveKind.F32, "f32", 4);
    public static readonly PrimitiveType Bool = new(PrimitiveKind.Bool, "bool", 1);
    public static readonly PrimitiveType Void = new(PrimitiveKind.Void, "void", 0);
    public static readonly PrimitiveType String = new(PrimitiveKind.String, "string", 4); // 32-bit const char* on MIPS
    public static readonly PrimitiveType Vec2 = new(PrimitiveKind.Vec2, "vec2", 8);      // 2 x f32
    public static readonly PrimitiveType Vec3 = new(PrimitiveKind.Vec3, "vec3", 12);     // 3 x f32
    public static readonly PrimitiveType Rect = new(PrimitiveKind.Rect, "rect", 16);     // 4 x f32
    public static readonly PrimitiveType Color = new(PrimitiveKind.Color, "color", 4);   // ABGR8888 uint32_t
    public static readonly PrimitiveType Timer = new(PrimitiveKind.Timer, "timer", 4);   // f32 countdown seconds

    public static readonly NullType Null = new();
    public static readonly ErrorType Error = new();
    public static readonly AnyType Any = new();

    public abstract string DisplayName { get; }

    public virtual bool IsInteger => false;
    public virtual bool IsFloat => false;
    public virtual bool IsNumeric => IsInteger || IsFloat;
    public virtual bool IsVector => false;
    public virtual bool IsError => false;

    public sealed override string ToString() => DisplayName;

    public static PrimitiveType? TryGetBuiltin(string name) => name switch
    {
        "int" => Int,
        "u8" => U8,
        "u16" => U16,
        "u32" => U32,
        "f32" => F32,
        "bool" => Bool,
        "void" => Void,
        "vec2" => Vec2,
        "vec3" => Vec3,
        "rect" => Rect,
        "color" => Color,
        "timer" => Timer,
        _ => null
    };


    public static bool IsAssignableTo(ResolvedType source, ResolvedType target)
    {
        if (source.IsError || target.IsError)
            return true;

        if (target is AnyType || source is AnyType)
            return true;

        if (source == target)
            return true;

        // null can be assigned to any generational actor handle ref<T> or function type fn(...)
        if (source is NullType && (target is ActorRefType or FunctionType))
            return true;

        if (source is PrimitiveType srcPrim && target is PrimitiveType dstPrim)
        {
            // All integer types are mutually assignable in static game scripts (with C99 cast emission)
            if (srcPrim.IsInteger && dstPrim.IsInteger)
                return true;

            // Integer literals/values can widen to f32
            if (srcPrim.IsInteger && dstPrim.Kind == PrimitiveKind.F32)
                return true;

            // Timer accepts time literals (resolved as Timer) or f32/int seconds
            if (dstPrim.Kind == PrimitiveKind.Timer && (srcPrim.Kind == PrimitiveKind.Timer || srcPrim.IsNumeric))
                return true;

            // f32 can also receive a timer duration
            if (dstPrim.Kind == PrimitiveKind.F32 && srcPrim.Kind == PrimitiveKind.Timer)
                return true;

            // Color is packed ABGR8888 (u32)
            if (dstPrim.Kind == PrimitiveKind.Color && srcPrim.Kind == PrimitiveKind.U32)
                return true;
            if (dstPrim.Kind == PrimitiveKind.U32 && srcPrim.Kind == PrimitiveKind.Color)
                return true;
        }

        return false;
    }
}

public sealed record PrimitiveType(PrimitiveKind Kind, string Name, int ByteSize) : ResolvedType
{
    public override string DisplayName => Name;

    public override bool IsInteger =>
        Kind is PrimitiveKind.Int or PrimitiveKind.U8 or PrimitiveKind.U16 or PrimitiveKind.U32;

    public override bool IsFloat => Kind is PrimitiveKind.F32;

    public override bool IsVector => Kind is PrimitiveKind.Vec2 or PrimitiveKind.Vec3;
}


public sealed record ActorRefType(string ActorName) : ResolvedType
{
    public override string DisplayName => $"ref<{ActorName}>";
}


public sealed record FixedArrayType(int Length, ResolvedType ElementType) : ResolvedType
{
    public override string DisplayName => $"[{Length}]{ElementType.DisplayName}";
}

public sealed record StructType(string Name) : ResolvedType
{
    public override string DisplayName => Name;
}

public sealed record EnumType(string Name) : ResolvedType
{
    public override string DisplayName => Name;
}


public sealed record MessageType(string Name) : ResolvedType
{
    public override string DisplayName => Name;
}


public sealed record AssetType(string AssetKind) : ResolvedType
{
    public override string DisplayName => $"asset<{AssetKind}>";
}


public sealed record FunctionType(
    IReadOnlyList<ResolvedType> ParameterTypes,
    ResolvedType ReturnType,
    bool IsVariadic = false
) : ResolvedType
{
    public override string DisplayName =>
        $"fn({string.Join(", ", ParameterTypes.Select(static p => p.DisplayName))}) -> {ReturnType.DisplayName}";

    public bool Equals(FunctionType? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        if (IsVariadic != other.IsVariadic) return false;
        if (ReturnType != other.ReturnType) return false;
        if (ParameterTypes.Count != other.ParameterTypes.Count) return false;
        for (int i = 0; i < ParameterTypes.Count; i++)
        {
            if (ParameterTypes[i] != other.ParameterTypes[i])
                return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ReturnType);
        hash.Add(IsVariadic);
        foreach (var p in ParameterTypes)
            hash.Add(p);
        return hash.ToHashCode();
    }
}


public sealed record NullType : ResolvedType
{
    public override string DisplayName => "null";
}


public sealed record AnyType : ResolvedType
{
    public override string DisplayName => "any";
}


public sealed record ErrorType : ResolvedType
{
    public override string DisplayName => "<error>";
    public override bool IsError => true;
}
