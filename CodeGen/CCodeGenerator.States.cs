using PspScript.Ast;
using PspScript.Semantics;

namespace PspScript.CodeGen;

public sealed partial class CCodeGenerator
{
    // ========================================================================
    // Game States & Transitions
    // ========================================================================

    private void EmitGameStatesAndTransitions()
    {
        _writer.WriteSectionHeader("Game States & LevelArena Transition Manager");

        foreach (var state in _symbols.States.Values)
        {
            string structType = $"State_{state.Name}_t";

            _writer.OpenBlock($"struct State_{state.Name}");
            if (state.Fields.Count == 0)
            {
                _writer.WriteLine("uint8_t _unused;");
            }
            else
            {
                foreach (var field in state.Fields.Values)
                {
                    _writer.WriteLine($"{CTypeMapper.FormatFieldDeclaration(field.Type, field.Name)};");
                }
            }
            _writer.CloseBlock(";");
            _writer.WriteLine($"static {structType} g_state_{state.Name};");
            _writer.WriteLine();
        }

        _writer.WriteLine("static PspsStateId g_current_state = STATE_NONE;");
        _writer.WriteLine("static PspsStateId g_pending_state = STATE_NONE;");
        _writer.WriteLine();

        _writer.OpenBlock("static void psps_request_state_transition(PspsStateId next_state)");
        _writer.WriteLine("g_pending_state = next_state;");
        _writer.CloseBlock();
        _writer.WriteLine();

        foreach (var state in _symbols.States.Values)
        {
            string structType = $"State_{state.Name}_t";

            _writer.OpenBlock($"static void State_{state.Name}_init_fields({structType}* self)");
            _writer.WriteLine("(void)self;");
            _writer.WriteLine($"memset(self, 0, sizeof({structType}));");
            var initCtx = new EmissionContext { CurrentState = state };
            foreach (var fieldDecl in state.Declaration.Fields)
            {
                if (!state.Fields.TryGetValue(fieldDecl.Name, out var fieldSym))
                    continue;
                if (fieldDecl.DefaultValue is not null)
                {
                    EmitFieldOrVarInitializer($"self->{fieldDecl.Name}", fieldSym.Type, fieldDecl.DefaultValue, initCtx);
                }
            }
            _writer.CloseBlock();
            _writer.WriteLine();

            foreach (var methodDecl in state.Declaration.Methods)
            {
                if (!state.Methods.TryGetValue(methodDecl.Name, out var methodSym))
                    continue;

                _writer.OpenBlock(CTypeMapper.FormatStateMethodSignature(state, methodSym));
                _writer.WriteLine("(void)self;");

                var methodCtx = new EmissionContext { CurrentState = state };
                foreach (var p in methodSym.Parameters)
                {
                    methodCtx.LocalVariables.Add(p.Name);
                    _writer.WriteLine($"(void){p.Name};");
                }

                EmitBlockStatements(methodDecl.Body, methodCtx);
                _writer.CloseBlock();
                _writer.WriteLine();
            }

            EmitStateLifecycleHook(state, LifecycleKind.Init, $"static void State_{state.Name}_init({structType}* self)");
            EmitStateLifecycleHook(state, LifecycleKind.Update, $"static void State_{state.Name}_update({structType}* self, float dt)");
            EmitStateLifecycleHook(state, LifecycleKind.Draw, $"static void State_{state.Name}_draw({structType}* self)");
            EmitStateLifecycleHook(state, LifecycleKind.Exit, $"static void State_{state.Name}_exit({structType}* self)");
        }

        _writer.OpenBlock("static void psps_apply_state_transition(void)");
        _writer.WriteLine("if (g_pending_state == STATE_NONE) return;");
        _writer.WriteLine("PspsStateId next = g_pending_state;");
        _writer.WriteLine("g_pending_state = STATE_NONE;");
        _writer.WriteLine();
        _writer.WriteLine("/* 1. Run exit() on the outgoing state */");
        _writer.OpenBlock("switch (g_current_state)");
        foreach (var state in _symbols.States.Values)
        {
            _writer.WriteLine($"case STATE_{state.Name}: State_{state.Name}_exit(&g_state_{state.Name}); break;");
        }
        _writer.WriteLine("default: break;");
        _writer.CloseBlock();
        _writer.WriteLine();
        _writer.WriteLine("/* 2. Reset LevelArena and level-scoped variables */");
        _writer.WriteLine("psps_arena_reset(&g_level_arena);");
        _writer.WriteLine("psps_reset_level_globals();");
        _writer.WriteLine("g_current_state = next;");
        _writer.WriteLine();
        _writer.WriteLine("/* 3. Initialize fields and run init() on the incoming state */");
        _writer.OpenBlock("switch (g_current_state)");
        foreach (var state in _symbols.States.Values)
        {
            _writer.OpenBlock($"case STATE_{state.Name}:");
            _writer.WriteLine($"State_{state.Name}_init_fields(&g_state_{state.Name});");
            _writer.WriteLine($"State_{state.Name}_init(&g_state_{state.Name});");
            _writer.WriteLine("break;");
            _writer.CloseBlock();
        }
        _writer.WriteLine("default: break;");
        _writer.CloseBlock();
        _writer.CloseBlock();
    }

    private void EmitStateLifecycleHook(StateSymbol state, LifecycleKind kind, string signature)
    {
        _writer.OpenBlock(signature);
        _writer.WriteLine("(void)self;");
        if (kind == LifecycleKind.Update)
        {
            _writer.WriteLine("(void)dt;");
            foreach (var field in state.Fields.Values)
            {
                if (field.Type == ResolvedType.Timer)
                {
                    _writer.WriteLine($"psps_timer_tick(&self->{field.Name}, dt);");
                }
            }
        }

        if (state.Hooks.TryGetValue(kind, out var hook))
        {
            var hookCtx = new EmissionContext { CurrentState = state };
            if (kind == LifecycleKind.Update && hook.Parameters.Count == 1)
            {
                string paramName = hook.Parameters[0].Name;
                if (paramName != "dt")
                {
                    _writer.WriteLine($"float {paramName} = dt;");
                    _writer.WriteLine($"(void){paramName};");
                }
                hookCtx.LocalVariables.Add(paramName);
            }

            EmitBlockStatements(hook.Body, hookCtx);
        }

        _writer.CloseBlock();
        _writer.WriteLine();
    }

    // ========================================================================
    // Top-Level Free Functions
    // ========================================================================

    private void EmitFreeFunctions()
    {
        var freeFuncs = _program.Declarations.OfType<FuncDecl>().ToList();
        if (freeFuncs.Count == 0)
            return;

        _writer.WriteSectionHeader("8. Free Functions");

        foreach (var fnDecl in freeFuncs)
        {
            if (!_symbols.Functions.TryGetValue(fnDecl.Name, out var fnSym))
                continue;

            _writer.OpenBlock(CTypeMapper.FormatFunctionSignature(null, fnSym));
            var fnCtx = new EmissionContext();
            foreach (var p in fnSym.Parameters)
            {
                fnCtx.LocalVariables.Add(p.Name);
                _writer.WriteLine($"(void){p.Name};");
            }

            EmitBlockStatements(fnDecl.Body, fnCtx);
            _writer.CloseBlock();
            _writer.WriteLine();
        }
    }

    // ========================================================================
    // Main Entry Point & Deterministic 4-Phase Frame Loop
    // ========================================================================

    private void EmitMainEntryPoint()
    {
        _writer.WriteSectionHeader("Entry Point & Deterministic 4-Phase Frame Loop");

        _writer.OpenBlock("static void psps_deliver_frame_messages(void)");
        _writer.WriteLine("uint32_t count = g_mailbox_count;");
        _writer.WriteLine("g_mailbox_count = 0u;");
        _writer.OpenBlock("for (uint32_t i = 0u; i < count; ++i)");
        _writer.WriteLine("const PspsMessage* msg = &g_mailbox_queue[i];");
        _writer.WriteLine("(void)msg;");
        if (_symbols.Actors.Count > 0)
        {
            _writer.OpenBlock("switch ((PspsActorTypeId)msg->target_type)");
            foreach (var actor in _symbols.Actors.Values)
            {
                _writer.OpenBlock($"case ACTOR_TYPE_{actor.Name}:");
                _writer.WriteLine($"Actor_{actor.Name}_t* target_ptr = {actor.Name}_resolve(msg->target);");
                _writer.OpenBlock("if (target_ptr != NULL)");
                _writer.WriteLine($"{actor.Name}_on_message(target_ptr, msg->target, msg);");
                _writer.CloseBlock();
                _writer.WriteLine("break;");
                _writer.CloseBlock();
            }
            _writer.WriteLine("default: break;");
            _writer.CloseBlock();
        }
        _writer.CloseBlock();
        _writer.CloseBlock();
        _writer.WriteLine();

        string gameTitle = _symbols.GameConfig?.Title ?? "PSPScript Homebrew";

        _writer.OpenBlock("int main(int argc, char* argv[])");
        _writer.WriteLine("(void)argc;");
        _writer.WriteLine("(void)argv;");
        _writer.WriteLine();

        _writer.WriteLine("/* Initialize Static Memory Arenas */");
        _writer.WriteLine("g_level_arena = psps_arena_init(g_level_arena_buf, PSPS_LEVEL_ARENA_SIZE);");
        _writer.WriteLine("g_frame_arena = psps_arena_init(g_frame_arena_buf, PSPS_FRAME_ARENA_SIZE);");
        _writer.WriteLine();

        _writer.WriteLine("/* Initialize Video (480x272) & Controller Input */");
        _writer.OpenBlock($"if (!psps_video_init(\"{CTypeMapper.EscapeCString(gameTitle)}\"))");
        _writer.WriteLine("return 1;");
        _writer.CloseBlock();
        _writer.WriteLine("psps_input_init();");
        _writer.WriteLine("psps_audio_init();");
        _writer.WriteLine();

        if (_texturesToUpload.Count > 0)
        {
            _writer.WriteLine("/* Upload Compile-Time Baked Textures to VRAM */");
            foreach (var (cVarName, baked) in _texturesToUpload)
            {
                string loader = baked.Format == BakedPixelFormat.Abgr8888
                    ? "psps_load_texture_abgr8888"
                    : "psps_load_texture_rgba4444";
                _writer.WriteLine($"{cVarName} = {loader}({cVarName}_pixels, {baked.Width}u, {baked.Height}u);");
            }
            _writer.WriteLine();
        }

        _writer.WriteLine("/* Initialize Global Variables & Singleton Actors */");
        _writer.WriteLine("psps_init_all_globals();");
        foreach (var actor in _symbols.Actors.Values.Where(static a => a.IsSingleton))
        {
            _writer.OpenBlock();
            _writer.WriteLine($"Actor_{actor.Name}_t* inst = NULL;");
            _writer.WriteLine($"psps_ref_t ref = {actor.Name}_spawn_raw(&inst);");
            _writer.WriteLine($"{actor.Name}_init(inst, ref);");
            _writer.CloseBlock();
        }
        _writer.WriteLine();

        string? entryStateName = _symbols.EntryStateName ?? _symbols.States.Values.FirstOrDefault()?.Name;
        if (!string.IsNullOrEmpty(entryStateName))
        {
            _writer.WriteLine($"/* Enter Configured Entry Game State: '{entryStateName}' */");
            _writer.WriteLine($"psps_request_state_transition(STATE_{entryStateName});");
            _writer.WriteLine("psps_apply_state_transition();");
            _writer.WriteLine();
        }

        _writer.WriteLine("/* Deterministic 4-Phase Frame Loop */");
        _writer.OpenBlock("while (!psps_input_should_quit())");

        _writer.WriteLine("/* --- Phase 1: Input & Update --- */");
        _writer.WriteLine("psps_input_update();");
        _writer.WriteLine("float dt = psps_video_delta_time();");
        _writer.WriteLine("(void)dt;");
        if (_symbols.States.Count > 0)
        {
            _writer.OpenBlock("switch (g_current_state)");
            foreach (var state in _symbols.States.Values)
            {
                _writer.WriteLine($"case STATE_{state.Name}: State_{state.Name}_update(&g_state_{state.Name}, dt); break;");
            }
            _writer.WriteLine("default: break;");
            _writer.CloseBlock();
        }

        foreach (var actor in _symbols.Actors.Values)
        {
            if (actor.IsSingleton)
            {
                _writer.OpenBlock($"if (g_actor_{actor.Name}_active)");
                _writer.WriteLine($"psps_ref_t ref = {actor.Name}_singleton_ref();");
                _writer.WriteLine($"{actor.Name}_update(&g_actor_{actor.Name}, ref, dt);");
                _writer.CloseBlock();
            }
            else
            {
                _writer.OpenBlock($"for (uint16_t i = 0u; i < POOL_CAP_{actor.Name}; ++i)");
                _writer.WriteLine($"if (!g_pool_{actor.Name}_active[i]) continue;");
                _writer.WriteLine($"psps_ref_t ref = (psps_ref_t){{ .index = i, .generation = g_pool_{actor.Name}_gen[i] }};");
                _writer.WriteLine($"{actor.Name}_update(&g_pool_{actor.Name}[i], ref, dt);");
                _writer.CloseBlock();
            }
        }
        _writer.WriteLine();

        _writer.WriteLine("/* --- Phase 2: Frame Mailbox Message Delivery --- */");
        _writer.WriteLine("psps_deliver_frame_messages();");
        _writer.WriteLine();

        _writer.WriteLine("/* --- Phase 3: Render (480x272) --- */");
        _writer.WriteLine("psps_video_begin_frame(PSPS_COLOR_BLACK);");
        if (_symbols.States.Count > 0)
        {
            _writer.OpenBlock("switch (g_current_state)");
            foreach (var state in _symbols.States.Values)
            {
                _writer.WriteLine($"case STATE_{state.Name}: State_{state.Name}_draw(&g_state_{state.Name}); break;");
            }
            _writer.WriteLine("default: break;");
            _writer.CloseBlock();
        }

        foreach (var actor in _symbols.Actors.Values)
        {
            if (actor.IsSingleton)
            {
                _writer.OpenBlock($"if (g_actor_{actor.Name}_active)");
                _writer.WriteLine($"psps_ref_t ref = {actor.Name}_singleton_ref();");
                _writer.WriteLine($"{actor.Name}_draw(&g_actor_{actor.Name}, ref);");
                _writer.CloseBlock();
            }
            else
            {
                _writer.OpenBlock($"for (uint16_t i = 0u; i < POOL_CAP_{actor.Name}; ++i)");
                _writer.WriteLine($"if (!g_pool_{actor.Name}_active[i]) continue;");
                _writer.WriteLine($"psps_ref_t ref = (psps_ref_t){{ .index = i, .generation = g_pool_{actor.Name}_gen[i] }};");
                _writer.WriteLine($"{actor.Name}_draw(&g_pool_{actor.Name}[i], ref);");
                _writer.CloseBlock();
            }
        }
        _writer.WriteLine("psps_video_end_frame();");
        _writer.WriteLine();

        _writer.WriteLine("/* --- Phase 4: FrameArena Reset & Pending State Transition --- */");
        _writer.WriteLine("psps_arena_reset(&g_frame_arena);");
        _writer.WriteLine("psps_apply_state_transition();");

        _writer.CloseBlock();
        _writer.WriteLine();

        if (_texturesToUpload.Count > 0)
        {
            foreach (var (cVarName, _) in _texturesToUpload)
            {
                _writer.WriteLine($"psps_destroy_texture(&{cVarName});");
            }
        }
        _writer.WriteLine("psps_audio_shutdown();");
        _writer.WriteLine("psps_input_shutdown();");
        _writer.WriteLine("psps_video_shutdown();");
        _writer.WriteLine("return 0;");
        _writer.CloseBlock();
    }
}
