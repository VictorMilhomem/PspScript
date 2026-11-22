using PspScript.Ast;
using PspScript.Semantics;

namespace PspScript.CodeGen;

public sealed partial class CCodeGenerator
{
    // ========================================================================
    // Static Actor Pools & Singletons
    // ========================================================================

    private void EmitActorPoolsAndSingletons()
    {
        _writer.WriteSectionHeader("Static Actor Structs, Pools & Generational Handle Resolvers");

        foreach (var actor in _symbols.Actors.Values)
        {
            string structType = $"Actor_{actor.Name}_t";

            _writer.OpenBlock($"struct Actor_{actor.Name}");
            if (actor.Fields.Count == 0)
            {
                _writer.WriteLine("uint8_t _unused;");
            }
            else
            {
                foreach (var field in actor.Fields.Values)
                {
                    _writer.WriteLine($"{CTypeMapper.FormatFieldDeclaration(field.Type, field.Name)};");
                }
            }
            _writer.CloseBlock(";");
            _writer.WriteLine();

            _writer.WriteLine($"static void {actor.Name}_init_fields({structType}* self);");
            _writer.WriteLine($"static void {actor.Name}_init({structType}* self, psps_ref_t self_ref);");
            _writer.WriteLine($"static void {actor.Name}_update({structType}* self, psps_ref_t self_ref, float dt);");
            _writer.WriteLine($"static void {actor.Name}_draw({structType}* self, psps_ref_t self_ref);");
            _writer.WriteLine($"static void {actor.Name}_exit({structType}* self, psps_ref_t self_ref);");
            _writer.WriteLine($"static void {actor.Name}_on_message({structType}* self, psps_ref_t self_ref, const PspsMessage* _raw_msg);");
            foreach (var method in actor.Methods.Values)
            {
                _writer.WriteLine($"{CTypeMapper.FormatFunctionSignature(actor, method)};");
            }
            _writer.WriteLine();

            _writer.OpenBlock($"static void {actor.Name}_init_fields({structType}* self)");
            _writer.WriteLine("(void)self;");
            _writer.WriteLine($"memset(self, 0, sizeof({structType}));");
            var initCtx = new EmissionContext { CurrentActor = actor };
            foreach (var fieldDecl in actor.Declaration.Fields)
            {
                if (!actor.Fields.TryGetValue(fieldDecl.Name, out var fieldSym))
                    continue;
                if (fieldDecl.DefaultValue is not null)
                {
                    EmitFieldOrVarInitializer($"self->{fieldDecl.Name}", fieldSym.Type, fieldDecl.DefaultValue, initCtx);
                }
            }
            _writer.CloseBlock();
            _writer.WriteLine();

            if (actor.IsSingleton)
            {
                EmitSingletonActorStorageAndHelpers(actor, structType);
            }
            else
            {
                EmitPooledActorStorageAndHelpers(actor, structType);
            }
        }
    }

    private void EmitSingletonActorStorageAndHelpers(ActorSymbol actor, string structType)
    {
        _writer.WriteLine($"static {structType} g_actor_{actor.Name};");
        _writer.WriteLine($"static {structType} g_dummy_{actor.Name};");
        _writer.WriteLine($"static uint16_t g_actor_{actor.Name}_gen = 1u;");
        _writer.WriteLine($"static bool     g_actor_{actor.Name}_active = false;");
        _writer.WriteLine();

        _writer.OpenBlock($"static inline psps_ref_t {actor.Name}_singleton_ref(void)");
        _writer.WriteLine($"if (!g_actor_{actor.Name}_active) return PSPS_NULL_REF;");
        _writer.WriteLine($"return (psps_ref_t){{ .index = 0u, .generation = g_actor_{actor.Name}_gen }};");
        _writer.CloseBlock();
        _writer.WriteLine();

        _writer.OpenBlock($"static inline psps_ref_t {actor.Name}_spawn_raw({structType}** out_ptr)");
        _writer.WriteLine($"if (g_actor_{actor.Name}_gen == 0u) g_actor_{actor.Name}_gen = 1u;");
        _writer.WriteLine($"g_actor_{actor.Name}_active = true;");
        _writer.WriteLine($"{actor.Name}_init_fields(&g_actor_{actor.Name});");
        _writer.WriteLine($"if (out_ptr != NULL) *out_ptr = &g_actor_{actor.Name};");
        _writer.WriteLine($"return (psps_ref_t){{ .index = 0u, .generation = g_actor_{actor.Name}_gen }};");
        _writer.CloseBlock();
        _writer.WriteLine();

        _writer.OpenBlock($"static inline {structType}* {actor.Name}_resolve(psps_ref_t handle)");
        _writer.WriteLine($"if (!g_actor_{actor.Name}_active) return NULL;");
        _writer.WriteLine($"if (handle.index != 0u || handle.generation != g_actor_{actor.Name}_gen) return NULL;");
        _writer.WriteLine($"return &g_actor_{actor.Name};");
        _writer.CloseBlock();
        _writer.WriteLine();

        _writer.OpenBlock($"static inline {structType}* {actor.Name}_resolve_mut(psps_ref_t handle)");
        _writer.WriteLine($"{structType}* ptr = {actor.Name}_resolve(handle);");
        _writer.WriteLine($"return (ptr != NULL) ? ptr : &g_dummy_{actor.Name};");
        _writer.CloseBlock();
        _writer.WriteLine();

        _writer.OpenBlock($"static inline void {actor.Name}_destroy(psps_ref_t handle)");
        _writer.WriteLine($"{structType}* ptr = {actor.Name}_resolve(handle);");
        _writer.WriteLine("if (ptr == NULL) return;");
        _writer.WriteLine($"{actor.Name}_exit(ptr, handle);");
        _writer.WriteLine($"g_actor_{actor.Name}_active = false;");
        _writer.WriteLine($"g_actor_{actor.Name}_gen++;");
        _writer.WriteLine($"if (g_actor_{actor.Name}_gen == 0u) g_actor_{actor.Name}_gen = 1u;");
        _writer.CloseBlock();
        _writer.WriteLine();
    }

    private void EmitPooledActorStorageAndHelpers(ActorSymbol actor, string structType)
    {
        int cap = actor.PoolCapacity!.Value;
        _writer.WriteLine($"#define POOL_CAP_{actor.Name} {cap}u");
        _writer.WriteLine($"static {structType} g_pool_{actor.Name}[POOL_CAP_{actor.Name}];");
        _writer.WriteLine($"static {structType} g_dummy_{actor.Name};");
        _writer.WriteLine($"static uint16_t g_pool_{actor.Name}_gen[POOL_CAP_{actor.Name}];");
        _writer.WriteLine($"static bool     g_pool_{actor.Name}_active[POOL_CAP_{actor.Name}];");
        _writer.WriteLine();

        _writer.OpenBlock($"static inline psps_ref_t {actor.Name}_spawn_raw({structType}** out_ptr)");
        _writer.OpenBlock($"for (uint16_t i = 0u; i < POOL_CAP_{actor.Name}; ++i)");
        _writer.OpenBlock($"if (!g_pool_{actor.Name}_active[i])");
        _writer.WriteLine($"if (g_pool_{actor.Name}_gen[i] == 0u) g_pool_{actor.Name}_gen[i] = 1u;");
        _writer.WriteLine($"g_pool_{actor.Name}_active[i] = true;");
        _writer.WriteLine($"{actor.Name}_init_fields(&g_pool_{actor.Name}[i]);");
        _writer.WriteLine($"if (out_ptr != NULL) *out_ptr = &g_pool_{actor.Name}[i];");
        _writer.WriteLine($"return (psps_ref_t){{ .index = i, .generation = g_pool_{actor.Name}_gen[i] }};");
        _writer.CloseBlock();
        _writer.CloseBlock();
        _writer.WriteLine("if (out_ptr != NULL) *out_ptr = NULL;");
        _writer.WriteLine("return PSPS_NULL_REF;");
        _writer.CloseBlock();
        _writer.WriteLine();

        _writer.OpenBlock($"static inline {structType}* {actor.Name}_resolve(psps_ref_t handle)");
        _writer.WriteLine($"if (handle.index >= POOL_CAP_{actor.Name}) return NULL;");
        _writer.WriteLine($"if (!g_pool_{actor.Name}_active[handle.index]) return NULL;");
        _writer.WriteLine($"if (g_pool_{actor.Name}_gen[handle.index] != handle.generation) return NULL;");
        _writer.WriteLine($"return &g_pool_{actor.Name}[handle.index];");
        _writer.CloseBlock();
        _writer.WriteLine();

        _writer.OpenBlock($"static inline {structType}* {actor.Name}_resolve_mut(psps_ref_t handle)");
        _writer.WriteLine($"{structType}* ptr = {actor.Name}_resolve(handle);");
        _writer.WriteLine($"return (ptr != NULL) ? ptr : &g_dummy_{actor.Name};");
        _writer.CloseBlock();
        _writer.WriteLine();

        _writer.OpenBlock($"static inline void {actor.Name}_destroy(psps_ref_t handle)");
        _writer.WriteLine($"{structType}* ptr = {actor.Name}_resolve(handle);");
        _writer.WriteLine("if (ptr == NULL) return;");
        _writer.WriteLine($"{actor.Name}_exit(ptr, handle);");
        _writer.WriteLine($"g_pool_{actor.Name}_active[handle.index] = false;");
        _writer.WriteLine($"g_pool_{actor.Name}_gen[handle.index]++;");
        _writer.WriteLine($"if (g_pool_{actor.Name}_gen[handle.index] == 0u) g_pool_{actor.Name}_gen[handle.index] = 1u;");
        _writer.CloseBlock();
        _writer.WriteLine();
    }

    // ========================================================================
    // Actor Methods, Lifecycle Hooks & Message Dispatchers
    // ========================================================================

    private void EmitActorMethodsAndHandlers()
    {
        _writer.WriteSectionHeader("Actor Lifecycle Hooks, Methods & Message Dispatchers");

        foreach (var actor in _symbols.Actors.Values)
        {
            string structType = $"Actor_{actor.Name}_t";

            foreach (var methodDecl in actor.Declaration.Methods)
            {
                if (!actor.Methods.TryGetValue(methodDecl.Name, out var methodSym))
                    continue;

                _writer.OpenBlock(CTypeMapper.FormatFunctionSignature(actor, methodSym));
                _writer.WriteLine("(void)self;");
                _writer.WriteLine("(void)self_ref;");

                var methodCtx = new EmissionContext { CurrentActor = actor };
                foreach (var p in methodSym.Parameters)
                {
                    methodCtx.LocalVariables.Add(p.Name);
                    _writer.WriteLine($"(void){p.Name};");
                }

                EmitBlockStatements(methodDecl.Body, methodCtx);
                _writer.CloseBlock();
                _writer.WriteLine();
            }

            EmitActorLifecycleHook(actor, LifecycleKind.Init, $"static void {actor.Name}_init({structType}* self, psps_ref_t self_ref)");
            EmitActorLifecycleHook(actor, LifecycleKind.Update, $"static void {actor.Name}_update({structType}* self, psps_ref_t self_ref, float dt)");
            EmitActorLifecycleHook(actor, LifecycleKind.Draw, $"static void {actor.Name}_draw({structType}* self, psps_ref_t self_ref)");
            EmitActorLifecycleHook(actor, LifecycleKind.Exit, $"static void {actor.Name}_exit({structType}* self, psps_ref_t self_ref)");

            _writer.OpenBlock($"static void {actor.Name}_on_message({structType}* self, psps_ref_t self_ref, const PspsMessage* _raw_msg)");
            _writer.WriteLine("(void)self;");
            _writer.WriteLine("(void)self_ref;");
            _writer.WriteLine("(void)_raw_msg;");

            if (actor.Handlers.Count > 0)
            {
                _writer.OpenBlock("switch (_raw_msg->kind)");
                foreach (var handler in actor.Declaration.Handlers)
                {
                    _writer.OpenBlock($"case MSG_{handler.MessageName}:");
                    _writer.WriteLine($"const Msg_{handler.MessageName}_t* {handler.ParamName} = &_raw_msg->data.{handler.MessageName};");
                    _writer.WriteLine($"(void){handler.ParamName};");

                    var handlerCtx = new EmissionContext
                    {
                        CurrentActor = actor,
                        ActiveMessageParamName = handler.ParamName,
                        ActiveMessageName = handler.MessageName
                    };
                    handlerCtx.LocalVariables.Add(handler.ParamName);

                    EmitBlockStatements(handler.Body, handlerCtx);
                    _writer.WriteLine("break;");
                    _writer.CloseBlock();
                }
                _writer.WriteLine("default: break;");
                _writer.CloseBlock();
            }

            _writer.CloseBlock();
            _writer.WriteLine();
        }
    }

    private void EmitActorLifecycleHook(ActorSymbol actor, LifecycleKind kind, string signature)
    {
        _writer.OpenBlock(signature);
        _writer.WriteLine("(void)self;");
        _writer.WriteLine("(void)self_ref;");
        if (kind == LifecycleKind.Update)
        {
            _writer.WriteLine("(void)dt;");
            foreach (var field in actor.Fields.Values)
            {
                if (field.Type == ResolvedType.Timer)
                {
                    _writer.WriteLine($"psps_timer_tick(&self->{field.Name}, dt);");
                }
            }
        }

        if (actor.Hooks.TryGetValue(kind, out var hook))
        {
            var hookCtx = new EmissionContext { CurrentActor = actor };
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
}
