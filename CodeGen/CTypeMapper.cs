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
        AssetType { AssetKind: "sound" or "music" } => "psps_sound_t",
        AssetType { AssetKind: "tilemap" } => "psps_baked_tilemap_t",
        AssetType => "psps_texture_t",
        StructType st => $"{st.Name}_t",
        EnumType en => $"{en.Name}_t",
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

        var dimSuffix = new StringBuilder();
        foreach (int dim in dimensions)
        {
            dimSuffix.Append('[').Append(dim.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        if (current is FunctionType fnType)
        {
            return FormatFunctionPointerDeclaration(fnType, $"{identifier}{dimSuffix}");
        }

        string baseType = FormatCType(current);
        return $"{baseType} {identifier}{dimSuffix}";
    }

    public static string FormatFunctionPointerDeclaration(FunctionType fnType, string declarator)
    {
        string paramList = fnType.ParameterTypes.Count == 0
            ? "void"
            : string.Join(", ", fnType.ParameterTypes.Select(FormatFunctionParamType));

        if (fnType.ReturnType is FunctionType retFn)
        {
            return FormatFunctionPointerDeclaration(retFn, $"(*{declarator})({paramList})");
        }

        string retType = FormatCType(fnType.ReturnType);
        return $"{retType} (*{declarator})({paramList})";
    }

    private static string FormatFunctionParamType(ResolvedType paramType)
    {
        if (paramType is FunctionType nestedFn)
            return FormatFunctionPointerDeclaration(nestedFn, string.Empty);
        return FormatCType(paramType);
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
        FunctionType => "NULL",
        StructType st => $"({st.Name}_t){{ 0 }}",
        EnumType en => $"({en.Name}_t)0",
        _ => "0"
    };

    public static string FormatFunctionSignature(ActorSymbol? ownerActor, FunctionSymbol fn)
    {
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
        if (fn.ReturnType is FunctionType retFn)
        {
            return $"static {FormatFunctionPointerDeclaration(retFn, $"{funcName}({paramsFormatted})")}";
        }
        return $"static {FormatCType(fn.ReturnType)} {funcName}({paramsFormatted})";
    }

    public static string FormatStateMethodSignature(StateSymbol ownerState, FunctionSymbol fn)
    {
        string funcName = $"State_{ownerState.Name}_{fn.Name}";

        var paramList = new List<string> { $"State_{ownerState.Name}_t* self" };
        foreach (var param in fn.Parameters)
        {
            paramList.Add(FormatFieldDeclaration(param.Type, param.Name));
        }

        string paramsFormatted = string.Join(", ", paramList);
        if (fn.ReturnType is FunctionType retFn)
        {
            return $"static {FormatFunctionPointerDeclaration(retFn, $"{funcName}({paramsFormatted})")}";
        }
        return $"static {FormatCType(fn.ReturnType)} {funcName}({paramsFormatted})";
    }

    public static string FormatLambdaSignature(int id, PspScript.Ast.LambdaExpr lambda, FunctionType fnType)
    {
        var paramList = new List<string>(lambda.Parameters.Count);
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            var pType = i < fnType.ParameterTypes.Count ? fnType.ParameterTypes[i] : ResolvedType.Int;
            paramList.Add(FormatFieldDeclaration(pType, lambda.Parameters[i].Name));
        }

        string paramsFormatted = paramList.Count > 0 ? string.Join(", ", paramList) : "void";
        string funcName = $"psps_lambda_{id}";
        if (fnType.ReturnType is FunctionType retFn)
        {
            return $"static {FormatFunctionPointerDeclaration(retFn, $"{funcName}({paramsFormatted})")}";
        }
        return $"static {FormatCType(fnType.ReturnType)} {funcName}({paramsFormatted})";
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
