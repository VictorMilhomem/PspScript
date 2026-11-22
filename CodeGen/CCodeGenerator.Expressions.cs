using System.Globalization;
using PspScript.Ast;
using PspScript.Semantics;

namespace PspScript.CodeGen;

public sealed partial class CCodeGenerator
{
    // ========================================================================
    // Expression Emission
    // ========================================================================

    private string EmitExpr(Expr expr, EmissionContext ctx)
    {
        return expr switch
        {
            IntLiteralExpr intLit => intLit.Value.ToString(CultureInfo.InvariantCulture),
            FloatLiteralExpr floatLit => CSourceWriter.FormatFloat(floatLit.Value),
            TimeLiteralExpr timeLit => CSourceWriter.FormatFloat(timeLit.Seconds),
            ColorLiteralExpr colorLit => CSourceWriter.FormatColor(colorLit.AbgrValue),
            StringLiteralExpr strLit => $"\"{CTypeMapper.EscapeCString(strLit.Text)}\"",
            BooleanLiteralExpr boolLit => boolLit.Value ? "true" : "false",
            NullLiteralExpr => "PSPS_NULL_REF",
            SelfExpr => "self_ref",
            VecLiteralExpr vecLit => EmitVecLiteralExpr(vecLit, ctx),
            IdentifierExpr idExpr => EmitIdentifierExpr(idExpr, ctx),
            UnaryExpr unaryExpr => EmitUnaryExpr(unaryExpr, ctx),
            BinaryExpr binaryExpr => EmitBinaryExpr(binaryExpr, ctx),
            FieldAccessExpr fieldExpr => EmitFieldAccessExpr(fieldExpr, ctx),
            IndexAccessExpr indexExpr => $"{EmitExpr(indexExpr.Target, ctx)}[{EmitExpr(indexExpr.Index, ctx)}]",
            CallExpr callExpr => EmitCallExpr(callExpr, ctx),
            _ => "0"
        };
    }

    private string EmitVecLiteralExpr(VecLiteralExpr vecLit, EmissionContext ctx)
    {
        var args = vecLit.Components
            .Select(c => $"(float)({EmitExpr(c, ctx)})")
            .ToArray();

        return args.Length switch
        {
            2 => $"vec2_make({args[0]}, {args[1]})",
            3 => $"vec3_make({args[0]}, {args[1]}, {args[2]})",
            4 => $"rect_make({args[0]}, {args[1]}, {args[2]}, {args[3]})",
            _ => "vec2_make(0.0f, 0.0f)"
        };
    }

    private string EmitIdentifierExpr(IdentifierExpr idExpr, EmissionContext ctx)
    {
        string name = idExpr.Name;

        if (ctx.FrameArenaPointerLocals.Contains(name))
            return $"(*{name})";

        if (ctx.LocalVariables.Contains(name))
            return name;

        if (ctx.CurrentActor is not null && ctx.CurrentActor.Fields.ContainsKey(name))
            return $"self->{name}";

        if (ctx.CurrentState is not null && ctx.CurrentState.Fields.ContainsKey(name))
            return $"self->{name}";

        if (_symbols.Assets.ContainsKey(name))
            return $"g_asset_{name}";

        if (_symbols.Actors.TryGetValue(name, out var actorSym) && actorSym.IsSingleton)
            return $"{actorSym.Name}_singleton_ref()";

        if (_symbols.Globals.TryGetValue(name, out var gSym) && gSym.Scope == MemoryScope.Global)
            return $"g_globals.{name}";

        return name;
    }

    private string EmitUnaryExpr(UnaryExpr unaryExpr, EmissionContext ctx)
    {
        var operandType = _model.GetTypeOrDefault(unaryExpr.Operand);
        string operand = EmitExpr(unaryExpr.Operand, ctx);

        if (unaryExpr.Operator == UnaryOp.Negate)
        {
            if (operandType == ResolvedType.Vec2)
                return $"vec2_neg({operand})";
            if (operandType == ResolvedType.Vec3)
                return $"vec3_neg({operand})";
            return $"(-({operand}))";
        }

        return $"(!({operand}))";
    }

    private string EmitBinaryExpr(BinaryExpr binaryExpr, EmissionContext ctx)
    {
        var leftType = _model.GetTypeOrDefault(binaryExpr.Left);
        var rightType = _model.GetTypeOrDefault(binaryExpr.Right);
        string left = EmitExpr(binaryExpr.Left, ctx);
        string right = EmitExpr(binaryExpr.Right, ctx);

        if (leftType.IsVector || rightType.IsVector)
        {
            var vecType = leftType.IsVector ? leftType : rightType;
            string prefix = vecType == ResolvedType.Vec3 ? "vec3" : "vec2";

            switch (binaryExpr.Op)
            {
                case BinaryOp.Add:
                    return $"{prefix}_add({left}, {right})";
                case BinaryOp.Sub:
                    return $"{prefix}_sub({left}, {right})";
                case BinaryOp.Mul when leftType.IsVector && rightType.IsNumeric:
                    return $"{prefix}_scale({left}, (float)({right}))";
                case BinaryOp.Mul when leftType.IsNumeric && rightType.IsVector:
                    return $"{prefix}_scale({right}, (float)({left}))";
                case BinaryOp.Mul:
                    return $"{prefix}_mul({left}, {right})";
                case BinaryOp.Div when leftType.IsVector && rightType.IsNumeric:
                    return $"{prefix}_scale({left}, 1.0f / (float)({right}))";
                case BinaryOp.Div:
                    return $"{prefix}_div({left}, {right})";
            }
        }

        if ((leftType is ActorRefType or NullType) && (rightType is ActorRefType or NullType))
        {
            if (binaryExpr.Op == BinaryOp.Equal)
                return $"psps_ref_equals({left}, {right})";
            if (binaryExpr.Op == BinaryOp.NotEqual)
                return $"(!psps_ref_equals({left}, {right}))";
        }

        if (binaryExpr.Op == BinaryOp.Mod && (leftType.IsFloat || rightType.IsFloat))
        {
            return $"fmodf((float)({left}), (float)({right}))";
        }

        string cOp = binaryExpr.Op switch
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
            _ => "+"
        };

        return $"({left} {cOp} {right})";
    }

    private string EmitFieldAccessExpr(FieldAccessExpr fieldExpr, EmissionContext ctx)
    {
        var targetType = _model.GetTypeOrDefault(fieldExpr.Target);
        string field = fieldExpr.FieldName;

        if (fieldExpr.Target is IdentifierExpr idTarget &&
            idTarget.Name == ctx.ActiveMessageParamName)
        {
            return $"{idTarget.Name}->{field}";
        }

        if (targetType is ActorRefType actorRef)
        {
            if (fieldExpr.Target is SelfExpr && ctx.CurrentActor is not null)
            {
                return $"self->{field}";
            }

            string handleExpr = EmitExpr(fieldExpr.Target, ctx);
            return $"{actorRef.ActorName}_resolve_mut({handleExpr})->{field}";
        }

        if (targetType == ResolvedType.Color && field is "r" or "g" or "b" or "a")
        {
            return $"psps_color_{field}({EmitExpr(fieldExpr.Target, ctx)})";
        }

        if (targetType is FixedArrayType fixedArr && field is "len" or "length" or "capacity")
        {
            return fixedArr.Length.ToString(CultureInfo.InvariantCulture);
        }

        return $"{EmitExpr(fieldExpr.Target, ctx)}.{field}";
    }

    private string EmitCallExpr(CallExpr callExpr, EmissionContext ctx)
    {
        if (callExpr.Callee is FieldAccessExpr memberAccess)
        {
            var receiverType = _model.GetTypeOrDefault(memberAccess.Target);
            string receiverExpr = EmitExpr(memberAccess.Target, ctx);
            string method = memberAccess.FieldName;

            if (receiverType == ResolvedType.Timer)
            {
                return method switch
                {
                    "elapsed" or "ready" => $"psps_timer_elapsed(&({receiverExpr}))",
                    "reset" when callExpr.Arguments.Count == 1 =>
                        $"psps_timer_start(&({receiverExpr}), {FormatSecondsValue(callExpr.Arguments[0], _model.GetTypeOrDefault(callExpr.Arguments[0]), ctx)})",
                    "reset" => $"psps_timer_reset(&({receiverExpr}))",
                    _ => "false"
                };
            }

            if (receiverType.IsVector)
            {
                string prefix = receiverType == ResolvedType.Vec3 ? "vec3" : "vec2";
                return method switch
                {
                    "length" => $"{prefix}_len({receiverExpr})",
                    "normalized" => $"{prefix}_normalize({receiverExpr})",
                    "dot" when callExpr.Arguments.Count == 1 =>
                        $"{prefix}_dot({receiverExpr}, {EmitExpr(callExpr.Arguments[0], ctx)})",
                    _ => "0.0f"
                };
            }

            if (receiverType is ActorRefType actorRef)
            {
                var argStrings = new List<string>(callExpr.Arguments.Count + 2);
                if (memberAccess.Target is SelfExpr && ctx.CurrentActor is not null)
                {
                    argStrings.Add("self");
                    argStrings.Add("self_ref");
                }
                else
                {
                    argStrings.Add($"{actorRef.ActorName}_resolve_mut({receiverExpr})");
                    argStrings.Add(receiverExpr);
                }

                foreach (var arg in callExpr.Arguments)
                {
                    argStrings.Add(EmitExpr(arg, ctx));
                }

                return $"{actorRef.ActorName}_{method}({string.Join(", ", argStrings)})";
            }
        }

        if (callExpr.Callee is IdentifierExpr funcId)
        {
            string name = funcId.Name;

            if (ctx.CurrentActor is not null && ctx.CurrentActor.Methods.ContainsKey(name))
            {
                var args = new List<string> { "self", "self_ref" };
                args.AddRange(callExpr.Arguments.Select(a => EmitExpr(a, ctx)));
                return $"{ctx.CurrentActor.Name}_{name}({string.Join(", ", args)})";
            }

            if (ctx.CurrentState is not null && ctx.CurrentState.Methods.ContainsKey(name))
            {
                var args = new List<string> { "self" };
                args.AddRange(callExpr.Arguments.Select(a => EmitExpr(a, ctx)));
                return $"State_{ctx.CurrentState.Name}_{name}({string.Join(", ", args)})";
            }

            if (_symbols.Functions.TryGetValue(name, out var fnSym) && fnSym.IsBuiltin)
            {
                return EmitBuiltinIntrinsicCall(name, callExpr.Arguments, ctx);
            }

            string joinedArgs = string.Join(", ", callExpr.Arguments.Select(a => EmitExpr(a, ctx)));
            return $"psps_fn_{name}({joinedArgs})";
        }

        return "0";
    }

    private string EmitBuiltinIntrinsicCall(string name, IReadOnlyList<Expr> args, EmissionContext ctx)
    {
        var emitted = args.Select(a => EmitExpr(a, ctx)).ToList();

        return name switch
        {
            "sprite" when emitted.Count == 2 =>
                $"psps_draw_sprite({emitted[0]}, {emitted[1]}, PSPS_COLOR_WHITE)",
            "sprite" when emitted.Count >= 3 =>
                $"psps_draw_sprite({emitted[0]}, {emitted[1]}, {emitted[2]})",

            "draw_subsprite" when emitted.Count == 3 =>
                $"psps_draw_subsprite({emitted[0]}, {emitted[1]}, {emitted[2]}, PSPS_COLOR_WHITE)",
            "draw_subsprite" when emitted.Count >= 4 =>
                $"psps_draw_subsprite({emitted[0]}, {emitted[1]}, {emitted[2]}, {emitted[3]})",

            "draw_tilemap" when emitted.Count == 5 =>
                $"psps_draw_tilemap({emitted[0]}, &(psps_tilemap_t){{ .tiles = (const uint8_t*)({emitted[1]}), .cols = (uint16_t)({emitted[2]}), .rows = (uint16_t)({emitted[3]}), .tile_size = (uint16_t)({emitted[4]}) }}, vec2_make(0.0f, 0.0f), PSPS_COLOR_WHITE)",
            "draw_tilemap" when emitted.Count >= 6 =>
                $"psps_draw_tilemap({emitted[0]}, &(psps_tilemap_t){{ .tiles = (const uint8_t*)({emitted[1]}), .cols = (uint16_t)({emitted[2]}), .rows = (uint16_t)({emitted[3]}), .tile_size = (uint16_t)({emitted[4]}) }}, {emitted[5]}, PSPS_COLOR_WHITE)",

            "set_camera" when emitted.Count == 1 =>
                $"psps_video_set_camera({emitted[0]})",
            "get_camera" =>
                "psps_video_get_camera()",
            "set_camera_enabled" when emitted.Count == 1 =>
                $"psps_video_set_camera_enabled({emitted[0]})",

            "draw_rect" when emitted.Count >= 2 =>
                $"psps_draw_rect({emitted[0]}, {emitted[1]})",

            "draw_text" when emitted.Count == 2 =>
                $"psps_draw_text({emitted[0]}, {emitted[1]}, PSPS_COLOR_WHITE)",
            "draw_text" when emitted.Count == 3 =>
                $"psps_draw_text({emitted[0]}, {emitted[1]}, {emitted[2]})",
            "draw_text" when emitted.Count >= 4 =>
                $"psps_draw_text_scaled({emitted[0]}, {emitted[1]}, (int)({emitted[3]}), {emitted[2]})",

            "draw_number" when emitted.Count == 2 =>
                $"psps_draw_number((int32_t)({emitted[0]}), {emitted[1]}, PSPS_COLOR_WHITE, 1)",
            "draw_number" when emitted.Count == 3 =>
                $"psps_draw_number((int32_t)({emitted[0]}), {emitted[1]}, {emitted[2]}, 1)",
            "draw_number" when emitted.Count >= 4 =>
                $"psps_draw_number((int32_t)({emitted[0]}), {emitted[1]}, {emitted[2]}, (int)({emitted[3]}))",

            "clear_screen" when emitted.Count >= 1 =>
                $"psps_clear_screen({emitted[0]})",

            "save_game" =>
                "psps_save_write(PSPS_SAVE_GAME_ID, PSPS_SAVE_TITLE, &g_globals, sizeof(g_globals))",
            "load_game" =>
                "psps_save_read(PSPS_SAVE_GAME_ID, &g_globals, sizeof(g_globals))",

            "button_pressed" when emitted.Count >= 1 =>
                $"psps_input_pressed((uint32_t)({emitted[0]}))",

            "button_held" when emitted.Count >= 1 =>
                $"psps_input_down((uint32_t)({emitted[0]}))",

            "sin" when emitted.Count == 1 => $"sinf((float)({emitted[0]}))",
            "cos" when emitted.Count == 1 => $"cosf((float)({emitted[0]}))",
            "sqrt" when emitted.Count == 1 => $"sqrtf((float)({emitted[0]}))",
            "abs" when emitted.Count == 1 => $"fabsf((float)({emitted[0]}))",
            "min" when emitted.Count == 2 => $"fminf((float)({emitted[0]}), (float)({emitted[1]}))",
            "max" when emitted.Count == 2 => $"fmaxf((float)({emitted[0]}), (float)({emitted[1]}))",
            "clamp" when emitted.Count == 3 =>
                $"fminf(fmaxf((float)({emitted[0]}), (float)({emitted[1]})), (float)({emitted[2]}))",
            "lerp" when emitted.Count == 3 =>
                $"((float)({emitted[0]}) + ((float)({emitted[1]}) - (float)({emitted[0]})) * (float)({emitted[2]}))",

            "print" when emitted.Count >= 1 =>
                $"printf(\"%s\\n\", {emitted[0]})",

            _ => $"/* intrinsic {name} */ (void)0"
        };
    }
}
