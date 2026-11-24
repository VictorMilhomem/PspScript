using PspScript.Ast;
using PspScript.Semantics;

namespace PspScript.CodeGen;

public sealed partial class CCodeGenerator
{
    // ========================================================================
    // Statement Emission
    // ========================================================================

    private void EmitBlockStatements(BlockStmt block, EmissionContext parentCtx)
    {
        var ctx = parentCtx.Clone();
        foreach (var stmt in block.Statements)
        {
            EmitStmt(stmt, ctx);
        }
    }

    private void EmitStmt(Stmt stmt, EmissionContext ctx)
    {
        switch (stmt)
        {
            case BlockStmt block:
                _writer.OpenBlock();
                EmitBlockStatements(block, ctx);
                _writer.CloseBlock();
                break;

            case LocalVarStmt localVar:
                EmitLocalVarStmt(localVar, ctx);
                break;

            case AssignStmt assign:
                EmitAssignStmt(assign, ctx);
                break;

            case IfStmt ifStmt:
                EmitIfStmt(ifStmt, ctx);
                break;

            case MatchStmt matchStmt:
                EmitMatchStmt(matchStmt, ctx);
                break;

            case ForStmt forStmt:
                _writer.OpenBlock($"while ({EmitExpr(forStmt.Condition, ctx)})");
                EmitBlockStatements(forStmt.Body, ctx);
                _writer.CloseBlock();
                break;

            case ForRangeStmt forRangeStmt:
                EmitForRangeStmt(forRangeStmt, ctx);
                break;

            case ForEachStmt forEachStmt:
                EmitForEachStmt(forEachStmt, ctx);
                break;

            case BreakStmt:
                _writer.WriteLine("break;");
                break;

            case ReturnStmt returnStmt:
                if (returnStmt.Value is null)
                {
                    _writer.WriteLine("return;");
                }
                else
                {
                    _writer.WriteLine($"return {EmitExpr(returnStmt.Value, ctx)};");
                }
                break;

            case SendStmt sendStmt:
                EmitSendStmt(sendStmt, ctx);
                break;

            case SpawnStmt spawnStmt:
                EmitSpawnStmt(spawnStmt, ctx);
                break;

            case DestroyStmt destroyStmt:
                EmitDestroyStmt(destroyStmt, ctx);
                break;

            case TransitionStmt transitionStmt:
                _writer.WriteLine($"psps_request_state_transition(STATE_{transitionStmt.TargetStateName});");
                break;

            case ExprStmt exprStmt:
                _writer.WriteLine($"{EmitExpr(exprStmt.Expression, ctx)};");
                break;
        }
    }

    private void EmitLocalVarStmt(LocalVarStmt localVar, EmissionContext ctx)
    {
        var varType = _model.GetTypeOrDefault(localVar);

        if (localVar.Scope == MemoryScope.FrameArena)
        {
            string cType = CTypeMapper.FormatCType(varType);
            _writer.WriteLine($"{cType}* {localVar.Name} = psps_arena_alloc(&g_frame_arena, {cType}, 1);");
            if (localVar.Initializer is not null)
            {
                EmitFieldOrVarInitializer($"(*{localVar.Name})", varType, localVar.Initializer, ctx);
            }
            ctx.LocalVariables.Add(localVar.Name);
            ctx.FrameArenaPointerLocals.Add(localVar.Name);
        }
        else
        {
            if (varType is FixedArrayType)
            {
                _writer.WriteLine($"{CTypeMapper.FormatFieldDeclaration(varType, localVar.Name)};");
                _writer.WriteLine($"memset({localVar.Name}, 0, sizeof({localVar.Name}));");
            }
            else if (localVar.Initializer is not null)
            {
                string initCode = FormatInitializerValue(varType, localVar.Initializer, ctx);
                _writer.WriteLine($"{CTypeMapper.FormatFieldDeclaration(varType, localVar.Name)} = {initCode};");
            }
            else
            {
                _writer.WriteLine($"{CTypeMapper.FormatFieldDeclaration(varType, localVar.Name)} = {CTypeMapper.FormatDefaultValue(varType)};");
            }
            ctx.LocalVariables.Add(localVar.Name);
        }
    }

    private void EmitAssignStmt(AssignStmt assign, EmissionContext ctx)
    {
        var targetType = _model.GetTypeOrDefault(assign.Target);
        var valueType = _model.GetTypeOrDefault(assign.Value);
        string targetCode = EmitExpr(assign.Target, ctx);
        string valueCode = EmitExpr(assign.Value, ctx);

        if (targetType == ResolvedType.Timer && assign.Operator == AssignOp.Assign)
        {
            string secondsExpr = FormatSecondsValue(assign.Value, valueType, ctx);
            _writer.WriteLine($"psps_timer_start(&({targetCode}), {secondsExpr});");
            return;
        }

        if (targetType.IsVector && assign.Operator != AssignOp.Assign)
        {
            string prefix = targetType == ResolvedType.Vec3 ? "vec3" : "vec2";
            string opCall = assign.Operator switch
            {
                AssignOp.AddAssign => $"{prefix}_add({targetCode}, {valueCode})",
                AssignOp.SubAssign => $"{prefix}_sub({targetCode}, {valueCode})",
                AssignOp.MulAssign when valueType.IsNumeric => $"{prefix}_scale({targetCode}, (float)({valueCode}))",
                AssignOp.MulAssign => $"{prefix}_mul({targetCode}, {valueCode})",
                AssignOp.DivAssign when valueType.IsNumeric => $"{prefix}_scale({targetCode}, 1.0f / (float)({valueCode}))",
                AssignOp.DivAssign => $"{prefix}_div({targetCode}, {valueCode})",
                _ => valueCode
            };
            _writer.WriteLine($"{targetCode} = {opCall};");
            return;
        }

        string cOp = assign.Operator switch
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
            _ => "="
        };

        _writer.WriteLine($"{targetCode} {cOp} {valueCode};");
    }

    private void EmitIfStmt(IfStmt ifStmt, EmissionContext ctx, string keyword = "if")
    {
        _writer.OpenBlock($"{keyword} ({EmitExpr(ifStmt.Condition, ctx)})");
        EmitBlockStatements(ifStmt.ThenBranch, ctx);
        _writer.CloseBlock();

        if (ifStmt.ElseBranch is IfStmt elseIf)
        {
            EmitIfStmt(elseIf, ctx, "else if");
        }
        else if (ifStmt.ElseBranch is BlockStmt elseBlock)
        {
            _writer.OpenBlock("else");
            EmitBlockStatements(elseBlock, ctx);
            _writer.CloseBlock();
        }
    }

    private void EmitMatchStmt(MatchStmt matchStmt, EmissionContext ctx)
    {
        _writer.OpenBlock($"switch ({EmitExpr(matchStmt.Scrutinee, ctx)})");
        bool hasDefault = false;

        foreach (var arm in matchStmt.Arms)
        {
            string caseHeader = arm.Pattern switch
            {
                EnumVariantPattern ev => $"case {ev.EnumName}_{ev.VariantName}:",
                IntLiteralPattern il => $"case {il.Value}:",
                WildcardPattern => "default:",
                _ => "default:"
            };

            if (arm.Pattern is WildcardPattern)
            {
                hasDefault = true;
            }

            _writer.OpenBlock(caseHeader);
            if (arm.Body is BlockStmt block)
            {
                EmitBlockStatements(block, ctx);
            }
            else
            {
                EmitStmt(arm.Body, ctx.Clone());
            }
            _writer.WriteLine("break;");
            _writer.CloseBlock();
        }

        if (!hasDefault)
        {
            _writer.WriteLine("default: __builtin_unreachable();");
        }

        _writer.CloseBlock();
    }

    private void EmitForRangeStmt(ForRangeStmt forRangeStmt, EmissionContext ctx)
    {
        string startExpr = EmitExpr(forRangeStmt.Start, ctx);
        string endExpr = EmitExpr(forRangeStmt.End, ctx);

        bool needsEndTemp = forRangeStmt.End is not (IntLiteralExpr or IdentifierExpr);
        if (needsEndTemp)
        {
            int loopId = ++_loopCounter;
            string endVar = $"_range_end_{loopId}";
            _writer.OpenBlock();
            _writer.WriteLine($"int32_t {endVar} = (int32_t)({endExpr});");
            _writer.OpenBlock($"for (int32_t {forRangeStmt.IteratorName} = (int32_t)({startExpr}); {forRangeStmt.IteratorName} < {endVar}; ++{forRangeStmt.IteratorName})");
            _writer.WriteLine($"(void){forRangeStmt.IteratorName};");

            var loopCtx = ctx.Clone();
            loopCtx.LocalVariables.Add(forRangeStmt.IteratorName);
            EmitBlockStatements(forRangeStmt.Body, loopCtx);

            _writer.CloseBlock();
            _writer.CloseBlock();
        }
        else
        {
            _writer.OpenBlock($"for (int32_t {forRangeStmt.IteratorName} = (int32_t)({startExpr}); {forRangeStmt.IteratorName} < (int32_t)({endExpr}); ++{forRangeStmt.IteratorName})");
            _writer.WriteLine($"(void){forRangeStmt.IteratorName};");

            var loopCtx = ctx.Clone();
            loopCtx.LocalVariables.Add(forRangeStmt.IteratorName);
            EmitBlockStatements(forRangeStmt.Body, loopCtx);

            _writer.CloseBlock();
        }
    }

    private void EmitForEachStmt(ForEachStmt forEachStmt, EmissionContext ctx)
    {
        int loopId = ++_loopCounter;
        string idxVar = $"_pool_idx_{loopId}";
        string poolName = forEachStmt.PoolName;

        _writer.OpenBlock($"for (uint16_t {idxVar} = 0u; {idxVar} < POOL_CAP_{poolName}; ++{idxVar})");
        _writer.WriteLine($"if (!g_pool_{poolName}_active[{idxVar}]) continue;");
        _writer.WriteLine($"psps_ref_t {forEachStmt.IteratorName} = (psps_ref_t){{ .index = {idxVar}, .generation = g_pool_{poolName}_gen[{idxVar}] }};");
        _writer.WriteLine($"(void){forEachStmt.IteratorName};");

        var loopCtx = ctx.Clone();
        loopCtx.LocalVariables.Add(forEachStmt.IteratorName);
        EmitBlockStatements(forEachStmt.Body, loopCtx);
        _writer.CloseBlock();
    }

    private void EmitSendStmt(SendStmt sendStmt, EmissionContext ctx)
    {
        var targetType = _model.GetTypeOrDefault(sendStmt.Target);
        string actorName = targetType is ActorRefType actorRef ? actorRef.ActorName : "NONE";
        string targetExpr = EmitExpr(sendStmt.Target, ctx);

        _writer.OpenBlock();
        _writer.WriteLine("PspsMessage _msg;");
        _writer.WriteLine("memset(&_msg, 0, sizeof(_msg));");
        _writer.WriteLine($"_msg.kind = MSG_{sendStmt.MessageName};");
        _writer.WriteLine($"_msg.target_type = (uint16_t)ACTOR_TYPE_{actorName};");
        _writer.WriteLine($"_msg.target = {targetExpr};");

        if (_symbols.Messages.TryGetValue(sendStmt.MessageName, out var msgSym))
        {
            foreach (var field in msgSym.Fields.Values)
            {
                if (field.Declaration.DefaultValue is not null)
                {
                    EmitFieldOrVarInitializer($"_msg.data.{sendStmt.MessageName}.{field.Name}", field.Type, field.Declaration.DefaultValue, ctx);
                }
            }

            foreach (var arg in sendStmt.Arguments)
            {
                if (msgSym.Fields.TryGetValue(arg.Name, out var fieldSym))
                {
                    EmitFieldOrVarInitializer($"_msg.data.{sendStmt.MessageName}.{arg.Name}", fieldSym.Type, arg.Value, ctx);
                }
            }
        }

        _writer.WriteLine("psps_enqueue_message(_msg);");
        _writer.CloseBlock();
    }

    private void EmitSpawnStmt(SpawnStmt spawnStmt, EmissionContext ctx)
    {
        string actorName = spawnStmt.ActorName;
        string structType = $"Actor_{actorName}_t";
        int spawnId = ++_loopCounter;
        string ptrVar = $"_spawn_ptr_{spawnId}";
        string refVar = spawnStmt.BindingName ?? $"_spawn_ref_{spawnId}";

        if (spawnStmt.BindingName is not null)
        {
            _writer.WriteLine($"psps_ref_t {refVar} = PSPS_NULL_REF;");
            ctx.LocalVariables.Add(spawnStmt.BindingName);
        }

        _writer.OpenBlock();
        _writer.WriteLine($"{structType}* {ptrVar} = NULL;");
        if (spawnStmt.BindingName is not null)
        {
            _writer.WriteLine($"{refVar} = {actorName}_spawn_raw(&{ptrVar});");
        }
        else
        {
            _writer.WriteLine($"psps_ref_t {refVar} = {actorName}_spawn_raw(&{ptrVar});");
        }

        _writer.OpenBlock($"if ({ptrVar} != NULL)");
        if (_symbols.Actors.TryGetValue(actorName, out var actorSym))
        {
            foreach (var init in spawnStmt.Initializers)
            {
                if (actorSym.Fields.TryGetValue(init.Name, out var fieldSym))
                {
                    EmitFieldOrVarInitializer($"{ptrVar}->{init.Name}", fieldSym.Type, init.Value, ctx);
                }
            }
        }
        _writer.WriteLine($"{actorName}_init({ptrVar}, {refVar});");
        _writer.CloseBlock();
        _writer.CloseBlock();
    }

    private void EmitDestroyStmt(DestroyStmt destroyStmt, EmissionContext ctx)
    {
        var targetType = _model.GetTypeOrDefault(destroyStmt.Target);
        if (targetType is ActorRefType actorRef)
        {
            string targetExpr = EmitExpr(destroyStmt.Target, ctx);
            _writer.WriteLine($"{actorRef.ActorName}_destroy({targetExpr});");
        }
    }

    private void EmitFieldOrVarInitializer(
        string targetLValue,
        ResolvedType targetType,
        Expr? initializer,
        EmissionContext ctx)
    {
        if (targetType is FixedArrayType)
        {
            _writer.WriteLine($"memset(&({targetLValue}), 0, sizeof({targetLValue}));");
            return;
        }

        if (initializer is null)
        {
            _writer.WriteLine($"{targetLValue} = {CTypeMapper.FormatDefaultValue(targetType)};");
            return;
        }

        string valCode = FormatInitializerValue(targetType, initializer, ctx);
        _writer.WriteLine($"{targetLValue} = {valCode};");
    }

    private string FormatInitializerValue(ResolvedType targetType, Expr initializer, EmissionContext ctx)
    {
        var initType = _model.GetTypeOrDefault(initializer);
        if (targetType == ResolvedType.Timer)
        {
            string seconds = FormatSecondsValue(initializer, initType, ctx);
            return $"psps_timer_make({seconds})";
        }

        return EmitExpr(initializer, ctx);
    }

    private string FormatSecondsValue(Expr expr, ResolvedType exprType, EmissionContext ctx)
    {
        if (expr is TimeLiteralExpr timeLit)
            return CSourceWriter.FormatFloat(timeLit.Seconds);

        string raw = EmitExpr(expr, ctx);
        if (exprType == ResolvedType.Timer && expr is not TimeLiteralExpr)
            return $"({raw}).remaining";

        return $"(float)({raw})";
    }
}
