using System.Globalization;
using System.Text;
using PspScript.Semantics;

namespace PspScript.CodeGen;

internal static class CTypeMapper
{
    public static string FormatCType(ResolvedType type) => type switch
    {
        PrimitiveType { Kind: PrimitiveKind.Int } => "int32_t",
        PrimitiveType { Kind: PrimitiveKind.U8 } => "uint8_t",
        PrimitiveType { Kind: PrimitiveKind.U16 } => "uint16_t",
        PrimitiveType { Kind: PrimitiveKind.U32 } => "uint32_t",
        PrimitiveType { Kind: PrimitiveKind.F32 } => "float",
        PrimitiveType { Kind: PrimitiveKind.Bool } => "bool",
        PrimitiveType { Kind: PrimitiveKind.Void } => "void",
        PrimitiveType { Kind: PrimitiveKind.String } => "const char*",
        PrimitiveType { Kind: PrimitiveKind.Vec2 } => "vec2_t",
        PrimitiveType { Kind: PrimitiveKind.Vec3 } => "vec3_t",
        PrimitiveType { Kind: PrimitiveKind.Rect } => "rect_t",
        PrimitiveType { Kind: PrimitiveKind.Color } => "psps_color_t",
        PrimitiveType { Kind: PrimitiveKind.Timer } => "psps_timer_t",
        ActorRefType => "psps_ref_t",
        AssetType => "psps_texture_t",
        StructType st => $"{st.Name}_t",
        MessageType msg => $"Msg_{msg.Name}_t",
        FixedArrayType arr => FormatCType(arr.ElementType),
        _ => "int32_t"
    };

    public static string FormatFieldDeclaration(ResolvedType type, string identifier)
    {
        var dimensions = new List<int>();
        ResolvedType current = type;
        while (current is FixedArrayType arr)
        {
            dimensions.Add(arr.Length);
            current = arr.ElementType;
        }

        string baseType = FormatCType(current);
        if (dimensions.Count == 0)
            return $"{baseType} {identifier}";

        var sb = new StringBuilder();
        sb.Append(baseType).Append(' ').Append(identifier);
        foreach (int dim in dimensions)
        {
            sb.Append('[').Append(dim.ToString(CultureInfo.InvariantCulture)).Append(']');
        }
        return sb.ToString();
    }

    public static string FormatDefaultValue(ResolvedType type) => type switch
    {
        PrimitiveType { Kind: PrimitiveKind.Int or PrimitiveKind.U8 or PrimitiveKind.U16 or PrimitiveKind.U32 } => "0",
        PrimitiveType { Kind: PrimitiveKind.F32 } => "0.0f",
        PrimitiveType { Kind: PrimitiveKind.Bool } => "false",
        PrimitiveType { Kind: PrimitiveKind.String } => "\"\"",
        PrimitiveType { Kind: PrimitiveKind.Vec2 } => "vec2_make(0.0f, 0.0f)",
        PrimitiveType { Kind: PrimitiveKind.Vec3 } => "vec3_make(0.0f, 0.0f, 0.0f)",
        PrimitiveType { Kind: PrimitiveKind.Rect } => "rect_make(0.0f, 0.0f, 0.0f, 0.0f)",
        PrimitiveType { Kind: PrimitiveKind.Color } => "PSPS_COLOR_WHITE",
        PrimitiveType { Kind: PrimitiveKind.Timer } => "psps_timer_make(0.0f)",
        ActorRefType => "PSPS_NULL_REF",
        StructType st => $"({st.Name}_t){{ 0 }}",
        _ => "0"
    };

    public static string FormatFunctionSignature(ActorSymbol? ownerActor, FunctionSymbol fn)
    {
        string retType = FormatCType(fn.ReturnType);
        string funcName = ownerActor is not null
            ? $"{ownerActor.Name}_{fn.Name}"
            : $"psps_fn_{fn.Name}";

        var paramList = new List<string>();
        if (ownerActor is not null)
        {
            paramList.Add($"Actor_{ownerActor.Name}_t* self");
            paramList.Add("psps_ref_t self_ref");
        }

        foreach (var param in fn.Parameters)
        {
            paramList.Add(FormatFieldDeclaration(param.Type, param.Name));
        }

        string paramsFormatted = paramList.Count > 0 ? string.Join(", ", paramList) : "void";
        return $"static {retType} {funcName}({paramsFormatted})";
    }

    public static string FormatStateMethodSignature(StateSymbol ownerState, FunctionSymbol fn)
    {
        string retType = FormatCType(fn.ReturnType);
        string funcName = $"State_{ownerState.Name}_{fn.Name}";

        var paramList = new List<string> { $"State_{ownerState.Name}_t* self" };
        foreach (var param in fn.Parameters)
        {
            paramList.Add(FormatFieldDeclaration(param.Type, param.Name));
        }

        return $"static {retType} {funcName}({string.Join(", ", paramList)})";
    }

    public static string EscapeCString(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
