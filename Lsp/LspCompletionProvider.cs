using System.Text.RegularExpressions;
using PspScript.Ast;
using PspScript.Parsing;
using PspScript.Semantics;

namespace PspScript.Lsp;

public sealed record LspCompletionItem(
    string Label,
    int Kind,
    string? Detail = null,
    string? Documentation = null,
    string? InsertText = null
);

internal sealed record BuiltinDocInfo(
    string Signature,
    string Description,
    string[] Parameters
);

/// <summary>
/// Context-aware completion, hover, go-to-definition, signature help, and document symbol provider for PSPScript (.psps).
/// </summary>
public static class LspCompletionProvider
{
    // LSP CompletionItemKind constants
    private const int KindMethod = 2;
    private const int KindFunction = 3;
    private const int KindField = 5;
    private const int KindVariable = 6;
    private const int KindClass = 7;
    private const int KindModule = 9;
    private const int KindProperty = 10;
    private const int KindEnum = 13;
    private const int KindKeyword = 14;
    private const int KindEnumMember = 20;
    private const int KindConstant = 21;
    private const int KindStruct = 22;
    private const int KindTypeParameter = 25;

    // LSP SymbolKind constants (for DocumentSymbol / Outline)
    private const int SymPackage = 4;
    private const int SymClass = 5;
    private const int SymMethod = 6;
    private const int SymProperty = 7;
    private const int SymField = 8;
    private const int SymEnum = 10;
    private const int SymFunction = 12;
    private const int SymVariable = 13;
    private const int SymConstant = 14;
    private const int SymEnumMember = 22;
    private const int SymStruct = 23;
    private const int SymEvent = 24;

    private static readonly string[] LanguageKeywords =
    [
        "import", "game", "asset", "const", "message", "struct", "enum", "actor", "state",
        "global", "level", "temp", "var", "fn",
        "init", "update", "draw", "exit", "on",
        "if", "else", "match", "for", "each", "in", "break", "continue", "return",
        "send", "spawn", "destroy", "goto",
        "true", "false", "null", "self"
    ];

    private static readonly string[] PrimitiveTypeNames =
    [
        "int", "u8", "u16", "u32", "f32", "bool", "void",
        "vec2", "vec3", "rect", "color", "timer", "ref"
    ];

    private static readonly Dictionary<string, BuiltinDocInfo> BuiltinDocs = new(StringComparer.Ordinal)
    {
        ["sprite"] = new("fn sprite(texture: asset<texture>, pos: vec2[, tint: color]) -> void",
            "Draws a compile-time baked texture at `pos` with optional ABGR8888 `tint`.",
            ["texture: asset<texture>", "pos: vec2", "tint?: color"]),
        ["draw_subsprite"] = new("fn draw_subsprite(texture: asset<texture>, src: rect, pos: vec2[, tint: color][, flip_x: bool[, flip_y: bool]]) -> void",
            "Draws a sub-rectangle (`src`) from a spritesheet at `pos`, with optional `tint`, horizontal `flip_x`, and vertical `flip_y`.",
            ["texture: asset<texture>", "src: rect", "pos: vec2", "tint_or_flip_x?: color|bool", "flip_x_or_y?: bool", "flip_y?: bool"]),
        ["draw_tilemap"] = new("fn draw_tilemap(tileset: asset<texture>, tiles: [N]u8, cols: int, rows: int, tile_size: int[, origin: vec2]) -> void",
            "Draws a frustum-culled raw `[N]u8` tile grid at optional `origin`.",
            ["tileset: asset<texture>", "tiles: [N]u8", "cols: int", "rows: int", "tile_size: int", "origin?: vec2"]),
        ["set_camera"] = new("fn set_camera(pos: vec2) -> void",
            "Sets the 2D world-space camera offset.",
            ["pos: vec2"]),
        ["get_camera"] = new("fn get_camera() -> vec2",
            "Returns the current 2D world-space camera offset.",
            []),
        ["set_camera_enabled"] = new("fn set_camera_enabled(enabled: bool) -> void",
            "Enables (`true`, world-space) or disables (`false`, screen-space HUD) the 2D camera offset.",
            ["enabled: bool"]),
        ["draw_rect"] = new("fn draw_rect(bounds: rect, tint: color) -> void",
            "Draws a filled 2D rectangle (`{ x, y, w, h }`) with an ABGR8888 color.",
            ["bounds: rect", "tint: color"]),
        ["draw_text"] = new("fn draw_text(text: string, pos: vec2[, tint: color[, scale: int]]) -> void",
            "Draws text using the embedded 8x8 ASCII bitmap font at `pos`.",
            ["text: string", "pos: vec2", "tint?: color", "scale?: int"]),
        ["draw_number"] = new("fn draw_number(value: int, pos: vec2[, tint: color[, scale: int]]) -> void",
            "Draws a 32-bit integer using the embedded 8x8 ASCII bitmap font at `pos`.",
            ["value: int", "pos: vec2", "tint?: color", "scale?: int"]),
        ["clear_screen"] = new("fn clear_screen(color: color) -> void",
            "Clears the 480x272 framebuffer with an ABGR8888 color.",
            ["color: color"]),
        ["save_game"] = new("fn save_game() -> bool",
            "Persists all `global var` state to PSP Memory Stick (`sceUtilitySavedata`) or desktop save file.",
            []),
        ["load_game"] = new("fn load_game() -> bool",
            "Loads and checksum-verifies persistent `global var` save data.",
            []),
        ["play_sound"] = new("fn play_sound(sound: asset<sound>[, volume: f32]) -> void",
            "Plays a baked mono WAV sound effect on the 8-voice mixer (`volume` 0.0..1.0).",
            ["sound: asset<sound>", "volume?: f32"]),
        ["set_master_volume"] = new("fn set_master_volume(volume: f32) -> void",
            "Sets global audio mixer master volume (`0.0` to `1.0`).",
            ["volume: f32"]),
        ["stop_all_sounds"] = new("fn stop_all_sounds() -> void",
            "Immediately stops all active SFX voices and looping BGM.",
            []),
        ["button_pressed"] = new("fn button_pressed(button: int) -> bool",
            "Returns `true` on the single frame `button` transitions from released to pressed.",
            ["button: int"]),
        ["button_held"] = new("fn button_held(button: int) -> bool",
            "Returns `true` every frame `button` is held down.",
            ["button: int"]),
        ["sin"] = new("fn sin(radians: f32) -> f32", "Single-precision sine (`sinf`).", ["radians: f32"]),
        ["cos"] = new("fn cos(radians: f32) -> f32", "Single-precision cosine (`cosf`).", ["radians: f32"]),
        ["sqrt"] = new("fn sqrt(value: f32) -> f32", "Single-precision square root (`sqrtf`).", ["value: f32"]),
        ["abs"] = new("fn abs(value: f32) -> f32", "Single-precision absolute value (`fabsf`).", ["value: f32"]),
        ["floor"] = new("fn floor(value: f32) -> int", "Rounds `value` down to the nearest 32-bit integer (`(int32_t)floorf(value)`). Widens implicitly to `f32` when needed.", ["value: f32"]),
        ["min"] = new("fn min(a: f32, b: f32) -> f32", "Returns the smaller of `a` and `b`.", ["a: f32", "b: f32"]),
        ["max"] = new("fn max(a: f32, b: f32) -> f32", "Returns the larger of `a` and `b`.", ["a: f32", "b: f32"]),
        ["clamp"] = new("fn clamp(val: f32, low: f32, high: f32) -> f32", "Clamps `val` into `[low, high]`.", ["val: f32", "low: f32", "high: f32"]),
        ["lerp"] = new("fn lerp(a: f32, b: f32, t: f32) -> f32", "Linearly interpolates between `a` and `b` by `t`.", ["a: f32", "b: f32", "t: f32"]),
        ["vec2"] = new("fn vec2(x: f32, y: f32) -> vec2", "Constructs a 2D single-precision vector (`vec2`).", ["x: f32", "y: f32"]),
        ["vec3"] = new("fn vec3(x: f32, y: f32, z: f32) -> vec3", "Constructs a 3D single-precision vector (`vec3`).", ["x: f32", "y: f32", "z: f32"]),
        ["rect"] = new("fn rect(x: f32, y: f32, w: f32, h: f32) -> rect", "Constructs a 2D axis-aligned rectangle (`rect`).", ["x: f32", "y: f32", "w: f32", "h: f32"]),
        ["print"] = new("fn print(value: any) -> void", "Prints a debug line to stdout.", ["value: any"]),
        ["texture"] = new("texture(path: string[, format: ABGR8888 | RGBA4444])",
            "Compile-time PNG texture/spritesheet baker.",
            ["path: string", "format?: ABGR8888|RGBA4444"]),
        ["tilemap"] = new("tilemap(path: string[, tileset: asset<texture>, mutable: bool, collision_layer: string, draw_collision: bool])",
            "Compile-time Tiled (`.tmx`/`.tmj`) multi-layer & multi-tileset map baker.",
            ["path: string", "tileset?: asset<texture>", "mutable?: bool", "collision_layer?: string", "draw_collision?: bool"]),
        ["sound"] = new("sound(path: string[, rate: 44100 | 22050])",
            "Compile-time WAV sound effect baker (normalized to mono PCM).",
            ["path: string", "rate?: 44100|22050"]),
        ["music"] = new("music(path: string[, rate: 44100 | 22050])",
            "Compile-time looping WAV background music baker.",
            ["path: string", "rate?: 44100|22050"])
    };

    private static readonly Dictionary<string, BuiltinDocInfo> BuiltinMemberDocs = new(StringComparer.Ordinal)
    {
        ["overlaps"] = new("fn rect.overlaps(other: rect) -> bool", "Returns `true` if two `rect` values intersect (AABB).", ["other: rect"]),
        ["contains"] = new("fn rect.contains(point: vec2) -> bool", "Returns `true` if `point` lies inside this `rect`.", ["point: vec2"]),
        ["center"] = new("rect.center: vec2 (read-only)", "Center point `{ x + w * 0.5, y + h * 0.5 }` of this rectangle.", []),
        ["length"] = new("fn vec.length() -> f32", "Euclidean magnitude of this vector.", []),
        ["normalized"] = new("fn vec.normalized() -> vec2|vec3", "Unit vector in the same direction (or zero vector if length is 0).", []),
        ["dot"] = new("fn vec.dot(other: vec2|vec3) -> f32", "Dot product of two vectors.", ["other: vec2|vec3"]),
        ["elapsed"] = new("fn timer.elapsed() -> bool", "Returns `true` when the auto-ticked countdown timer has reached `0.0`.", []),
        ["ready"] = new("fn timer.ready() -> bool", "Alias for `timer.elapsed()`.", []),
        ["reset"] = new("fn reset([duration: f32]) -> void", "Resets a `timer` countdown or restores a `mutable: true` tilemap to its initial state.", ["duration?: f32"]),
        ["draw"] = new("fn tilemap.draw([origin: vec2]) -> void", "Draws all visible layers of a baked Tiled tilemap with 480x272 camera culling.", ["origin?: vec2"]),
        ["draw_layer"] = new("fn tilemap.draw_layer(layer: int[, origin: vec2]) -> void", "Draws a single 0-based layer of a baked Tiled tilemap.", ["layer: int", "origin?: vec2"]),
        ["get_tile"] = new("fn tilemap.get_tile(layer: int, col: int, row: int) -> int", "Bounds-checked 1-based global tile ID lookup (`0` = empty or out of bounds).", ["layer: int", "col: int", "row: int"]),
        ["set_tile"] = new("fn tilemap.set_tile(layer: int, col: int, row: int, tile_id: int) -> void", "Mutates a tile cell at runtime (requires `mutable: true`).", ["layer: int", "col: int", "row: int", "tile_id: int"]),
        ["is_solid"] = new("fn tilemap.is_solid(col: int, row: int) -> bool", "Bounds-checked static/mutable collision cell query.", ["col: int", "row: int"]),
        ["set_solid"] = new("fn tilemap.set_solid(col: int, row: int, solid: bool) -> void", "Mutates a collision cell at runtime (requires `mutable: true`).", ["col: int", "row: int", "solid: bool"]),
        ["collides_rect"] = new("fn tilemap.collides_rect(hitbox: rect) -> bool", "Returns `true` if any solid tile overlaps `hitbox`.", ["hitbox: rect"]),
        ["play"] = new("fn audio.play([volume: f32]) -> void", "Plays a `sound` effect or starts looping a `music` track (`volume` 0.0..1.0).", ["volume?: f32"]),
        ["stop"] = new("fn music.stop() -> void", "Stops the looping BGM channel.", []),
        ["pause"] = new("fn music.pause() -> void", "Pauses the looping BGM channel.", []),
        ["resume"] = new("fn music.resume() -> void", "Resumes the paused BGM channel.", [])
    };

    // ========================================================================
    // 1. Context-Aware Autocomplete
    // ========================================================================

    public static IReadOnlyList<LspCompletionItem> GetCompletions(
        string documentText,
        string? documentFilePath,
        int line,
        int character,
        SymbolTable? symbols,
        ProgramNode? program = null)
    {
        string linePrefix = GetLinePrefix(documentText, line, character);

        // 1. Import completions: `import ` or `import "`
        if (Regex.IsMatch(linePrefix, @"^\s*import\s+""?[^""]*$"))
        {
            return GetImportCompletions(documentFilePath);
        }

        // 2. Actor handle completions: `ref<`
        if (Regex.IsMatch(linePrefix, @"\bref<\s*[A-Za-z0-9_]*$"))
        {
            return GetActorCompletions(symbols, pooledOnly: false);
        }

        // 3. State transition or game entry completions: `goto ` or `entry: `
        if (Regex.IsMatch(linePrefix, @"(\bgoto\s+|\bentry\s*:\s*)[A-Za-z0-9_]*$"))
        {
            return GetStateCompletions(symbols);
        }

        // 4. Pooled actor spawn completions: `spawn `
        if (Regex.IsMatch(linePrefix, @"\bspawn\s+[A-Za-z0-9_]*$"))
        {
            return GetActorCompletions(symbols, pooledOnly: true);
        }

        // 5. Message handler / send message completions: `on ` or `send(target, `
        if (Regex.IsMatch(linePrefix, @"(\bon\s+|\bsend\s*\([^,]+,\s*)[A-Za-z0-9_]*$"))
        {
            return GetMessageCompletions(symbols);
        }

        // 6. Dot-member completions: `<ident>.` (including `self.`, `msg.`, `item.`, assets, enums, structs)
        var dotMatch = Regex.Match(linePrefix, @"([A-Za-z_][A-Za-z0-9_]*)\.\s*[A-Za-z0-9_]*$");
        if (dotMatch.Success)
        {
            string receiver = dotMatch.Groups[1].Value;
            var members = GetMemberCompletions(receiver, documentText, line, symbols);
            if (members.Count > 0)
                return members;
        }

        // 7. Asset loader named options: `tilemap("...", `, `texture("...", `, `sound("...", `, `music("...", `
        var assetOptItems = TryGetAssetOptionCompletions(linePrefix, symbols);
        if (assetOptItems is not null)
            return assetOptItems;

        // 8. Spawn / Send initializer field completions: `spawn Enemy { ` or `send(t, Hit { `
        var initFieldItems = TryGetInitializerFieldCompletions(documentText, line, character, symbols);
        if (initFieldItems is not null)
            return initFieldItems;

        // 9. Game config block completions: `game "..." { `
        if (IsCursorInsideGameConfigBlock(documentText, line) && !linePrefix.Contains(':'))
        {
            return GetGameConfigKeyCompletions();
        }

        // 10. General identifier, enclosing actor/state members, keyword, type, builtin, and project symbol completions
        return GetGeneralCompletions(documentText, line, symbols);
    }

    // ========================================================================
    // 2. Rich Hover
    // ========================================================================

    public static string? GetHoverMarkdown(
        string documentText,
        int line,
        int character,
        SymbolTable? symbols,
        ProgramNode? program = null,
        string? documentFilePath = null)
    {
        string? word = GetWordAtPosition(documentText, line, character, out string? qualifierBeforeDot);
        if (string.IsNullOrEmpty(word))
            return null;

        // 1. Qualified hover: `Enum.Variant`, `self.field`, `msg.field`, `level.is_solid`, etc.
        if (!string.IsNullOrEmpty(qualifierBeforeDot) && symbols is not null)
        {
            if (symbols.Enums.TryGetValue(qualifierBeforeDot, out var qEnum))
            {
                int idx = qEnum.Variants.ToList().IndexOf(word);
                if (idx >= 0)
                {
                    return $"```pspscript\n{qEnum.Name}.{word} = {idx}\n```";
                }
            }

            ResolvedType? receiverType = ResolveReceiverTypeAtLine(qualifierBeforeDot, documentText, line, symbols);
            if (receiverType is ActorRefType ar && symbols.Actors.TryGetValue(ar.ActorName, out var actSym))
            {
                if (actSym.Fields.TryGetValue(word, out var f))
                    return $"```pspscript\n({actSym.Name}) {f.Name}: {f.Type}\n```";
                if (actSym.Methods.TryGetValue(word, out var m))
                {
                    string ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}: {p.Type}"));
                    return $"```pspscript\nfn {actSym.Name}.{m.Name}({ps}) -> {m.ReturnType}\n```";
                }
            }
            else if (receiverType is StructType st && symbols.Structs.TryGetValue(st.Name, out var stSym) &&
                     stSym.Fields.TryGetValue(word, out var sf))
            {
                return $"```pspscript\n({stSym.Name}) {sf.Name}: {sf.Type}\n```";
            }
            else if (symbols.Messages.TryGetValue(FindEnclosingMessageHandlerType(documentText, line, qualifierBeforeDot) ?? string.Empty, out var mhSym) &&
                     mhSym.Fields.TryGetValue(word, out var mf))
            {
                return $"```pspscript\n(message {mhSym.Name}) {mf.Name}: {mf.Type}\n```";
            }
            else if (qualifierBeforeDot == "self")
            {
                var (encKind, encName) = FindEnclosingContainer(documentText, line);
                if (encKind == "actor" && encName is not null && symbols.Actors.TryGetValue(encName, out var selfAct))
                {
                    if (selfAct.Fields.TryGetValue(word, out var af))
                        return $"```pspscript\n({selfAct.Name}) {af.Name}: {af.Type}\n```";
                }
                else if (encKind == "state" && encName is not null && symbols.States.TryGetValue(encName, out var selfSt))
                {
                    if (selfSt.Fields.TryGetValue(word, out var stf))
                        return $"```pspscript\n({selfSt.Name}) {stf.Name}: {stf.Type}\n```";
                }
            }

            if (BuiltinMemberDocs.TryGetValue(word, out var memberDoc))
            {
                return $"```pspscript\n{memberDoc.Signature}\n```\n{memberDoc.Description}";
            }
        }

        // 2. Top-level project symbols
        if (symbols is not null)
        {
            if (symbols.Enums.TryGetValue(word, out var enumSym))
            {
                return $"```pspscript\nenum {enumSym.Name} {{ {string.Join(", ", enumSym.Variants)} }}\n```";
            }
            if (symbols.Structs.TryGetValue(word, out var structSym))
            {
                var fields = structSym.Fields.Values.Select(f => $"  {f.Name}: {f.Type}");
                return $"```pspscript\nstruct {structSym.Name} {{\n{string.Join(",\n", fields)}\n}}\n```";
            }
            if (symbols.Actors.TryGetValue(word, out var actorSym))
            {
                string cap = actorSym.PoolCapacity.HasValue ? $"[{actorSym.PoolCapacity.Value}]" : " (singleton)";
                return $"```pspscript\nactor {actorSym.Name}{cap}\n```";
            }
            if (symbols.States.TryGetValue(word, out var stateSym))
            {
                return $"```pspscript\nstate {stateSym.Name}\n```";
            }
            if (symbols.Messages.TryGetValue(word, out var msgSym))
            {
                var fields = msgSym.Fields.Values.Select(f => $"  {f.Name}: {f.Type}");
                return $"```pspscript\nmessage {msgSym.Name} {{\n{string.Join(",\n", fields)}\n}}\n```";
            }
            if (symbols.Assets.TryGetValue(word, out var assetSym))
            {
                string mut = assetSym.IsMutable ? ", mutable: true" : string.Empty;
                return $"```pspscript\nasset {assetSym.Name} = {assetSym.AssetKind}(\"{assetSym.FilePath}\"{mut})\n```";
            }
            if (symbols.Constants.TryGetValue(word, out var constSym))
            {
                return $"```pspscript\nconst {constSym.Name}: {constSym.Type} = {constSym.Value.FormatDisplay()}\n```";
            }
            if (symbols.Globals.TryGetValue(word, out var gSym))
            {
                string scopeKw = gSym.Scope == MemoryScope.LevelArena ? "level var" : "global var";
                return $"```pspscript\n{scopeKw} {gSym.Name}: {gSym.Type}\n```";
            }
            if (!BuiltinDocs.ContainsKey(word) && symbols.Functions.TryGetValue(word, out var fnSym))
            {
                string paramStr = string.Join(", ", fnSym.Parameters.Select(p => $"{p.Name}: {p.Type}"));
                return $"```pspscript\nfn {fnSym.Name}({paramStr}) -> {fnSym.ReturnType}\n```";
            }

            // Enclosing actor / state field or method
            var (containerKind, containerName) = FindEnclosingContainer(documentText, line);
            if (containerKind == "actor" && containerName is not null && symbols.Actors.TryGetValue(containerName, out var encActor))
            {
                if (encActor.Fields.TryGetValue(word, out var af))
                    return $"```pspscript\n({encActor.Name} field) {af.Name}: {af.Type}\n```";
                if (encActor.Methods.TryGetValue(word, out var am))
                {
                    string ps = string.Join(", ", am.Parameters.Select(p => $"{p.Name}: {p.Type}"));
                    return $"```pspscript\nfn {encActor.Name}.{am.Name}({ps}) -> {am.ReturnType}\n```";
                }
            }
            else if (containerKind == "state" && containerName is not null && symbols.States.TryGetValue(containerName, out var encState))
            {
                if (encState.Fields.TryGetValue(word, out var sf))
                    return $"```pspscript\n({encState.Name} field) {sf.Name}: {sf.Type}\n```";
                if (encState.Methods.TryGetValue(word, out var sm))
                {
                    string ps = string.Join(", ", sm.Parameters.Select(p => $"{p.Name}: {p.Type}"));
                    return $"```pspscript\nfn {encState.Name}.{sm.Name}({ps}) -> {sm.ReturnType}\n```";
                }
            }
        }

        // 3. Built-in functions & asset loaders
        if (BuiltinDocs.TryGetValue(word, out var doc))
        {
            return $"```pspscript\n{doc.Signature}\n```\n{doc.Description}";
        }

        if (BuiltinMemberDocs.TryGetValue(word, out var mDoc))
        {
            return $"```pspscript\n{mDoc.Signature}\n```\n{mDoc.Description}";
        }

        // 4. Local variable / parameter inferred from surrounding text
        if (symbols is not null)
        {
            var localType = TryInferIdentifierTypeAtLine(word, documentText, line, symbols);
            if (localType is not null)
            {
                return $"```pspscript\n(local) {word}: {localType.DisplayName}\n```";
            }
        }

        return null;
    }

    // ========================================================================
    // 3. Go to Definition (textDocument/definition)
    // ========================================================================

    internal static object? GetDefinitionLocation(
        string documentText,
        string documentUri,
        string? documentFilePath,
        int line,
        int character,
        SymbolTable? symbols,
        ProgramNode? program,
        IReadOnlyList<ResolvedImportInfo>? resolvedImports)
    {
        string[] lines = documentText.Replace("\r\n", "\n").Split('\n');
        if (line >= 0 && line < lines.Length)
        {
            string currentLine = lines[line];
            var importMatch = Regex.Match(currentLine, @"^\s*import\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))");
            if (importMatch.Success)
            {
                string rawSpec = importMatch.Groups[1].Success ? importMatch.Groups[1].Value : importMatch.Groups[2].Value;
                string? targetFile = TryResolveImportPathForDefinition(rawSpec, documentFilePath, line + 1, resolvedImports);
                if (!string.IsNullOrEmpty(targetFile) && File.Exists(targetFile))
                {
                    return MakeLocation(PspLanguageServer.LocalPathToUri(targetFile), 1, 1, 1);
                }
            }
        }

        string? word = GetWordAtPosition(documentText, line, character, out string? qualifierBeforeDot);
        if (string.IsNullOrEmpty(word))
            return null;

        // 1. Qualified access: `Enum.Variant`, `Actor.field`, `self.field`, `msg.field`
        if (!string.IsNullOrEmpty(qualifierBeforeDot) && symbols is not null)
        {
            if (symbols.Enums.TryGetValue(qualifierBeforeDot, out var qEnum))
            {
                return MakeLocationFromSpan(qEnum.Declaration.Span, documentUri);
            }

            if (qualifierBeforeDot == "self")
            {
                var (encKind, encName) = FindEnclosingContainer(documentText, line);
                if (encKind == "actor" && encName is not null && symbols.Actors.TryGetValue(encName, out var selfAct))
                {
                    if (selfAct.Fields.TryGetValue(word, out var af))
                        return MakeLocationFromSpan(af.Declaration.Span, documentUri);
                    if (selfAct.Methods.TryGetValue(word, out var am) && am.Declaration is not null)
                        return MakeLocationFromSpan(am.Declaration.Span, documentUri);
                }
                else if (encKind == "state" && encName is not null && symbols.States.TryGetValue(encName, out var selfSt))
                {
                    if (selfSt.Fields.TryGetValue(word, out var sf))
                        return MakeLocationFromSpan(sf.Declaration.Span, documentUri);
                    if (selfSt.Methods.TryGetValue(word, out var sm) && sm.Declaration is not null)
                        return MakeLocationFromSpan(sm.Declaration.Span, documentUri);
                }
            }

            string? msgType = FindEnclosingMessageHandlerType(documentText, line, qualifierBeforeDot);
            if (msgType is not null && symbols.Messages.TryGetValue(msgType, out var msgSym) &&
                msgSym.Fields.TryGetValue(word, out var mf))
            {
                return MakeLocationFromSpan(mf.Declaration.Span, documentUri);
            }

            ResolvedType? receiverType = ResolveReceiverTypeAtLine(qualifierBeforeDot, documentText, line, symbols);
            if (receiverType is ActorRefType ar && symbols.Actors.TryGetValue(ar.ActorName, out var actSym))
            {
                if (actSym.Fields.TryGetValue(word, out var af))
                    return MakeLocationFromSpan(af.Declaration.Span, documentUri);
                if (actSym.Methods.TryGetValue(word, out var am) && am.Declaration is not null)
                    return MakeLocationFromSpan(am.Declaration.Span, documentUri);
            }
            else if (receiverType is StructType st && symbols.Structs.TryGetValue(st.Name, out var stSym) &&
                     stSym.Fields.TryGetValue(word, out var sf))
            {
                return MakeLocationFromSpan(sf.Declaration.Span, documentUri);
            }
        }

        // 2. Top-level declarations in SymbolTable (works across multi-file projects & StdLib)
        if (symbols is not null)
        {
            if (symbols.States.TryGetValue(word, out var stateSym))
                return MakeLocationFromSpan(stateSym.Declaration.Span, documentUri);
            if (symbols.Actors.TryGetValue(word, out var actorSym))
                return MakeLocationFromSpan(actorSym.Declaration.Span, documentUri);
            if (symbols.Structs.TryGetValue(word, out var structSym))
                return MakeLocationFromSpan(structSym.Declaration.Span, documentUri);
            if (symbols.Enums.TryGetValue(word, out var enumSym))
                return MakeLocationFromSpan(enumSym.Declaration.Span, documentUri);
            if (symbols.Messages.TryGetValue(word, out var msgSym))
                return MakeLocationFromSpan(msgSym.Declaration.Span, documentUri);
            if (symbols.Assets.TryGetValue(word, out var assetSym))
                return MakeLocationFromSpan(assetSym.Declaration.Span, documentUri);
            if (symbols.Constants.TryGetValue(word, out var constSym))
                return MakeLocationFromSpan(constSym.Declaration.Span, documentUri);
            if (symbols.Globals.TryGetValue(word, out var globalSym) && globalSym.Declaration is not null)
                return MakeLocationFromSpan(globalSym.Declaration.Span, documentUri);
            if (symbols.Functions.TryGetValue(word, out var fnSym) && fnSym.Declaration is not null)
                return MakeLocationFromSpan(fnSym.Declaration.Span, documentUri);

            // 3. Enclosing actor/state fields or methods
            var (encKind, encName) = FindEnclosingContainer(documentText, line);
            if (encKind == "actor" && encName is not null && symbols.Actors.TryGetValue(encName, out var encAct))
            {
                if (encAct.Fields.TryGetValue(word, out var af))
                    return MakeLocationFromSpan(af.Declaration.Span, documentUri);
                if (encAct.Methods.TryGetValue(word, out var am) && am.Declaration is not null)
                    return MakeLocationFromSpan(am.Declaration.Span, documentUri);
            }
            else if (encKind == "state" && encName is not null && symbols.States.TryGetValue(encName, out var encSt))
            {
                if (encSt.Fields.TryGetValue(word, out var sf))
                    return MakeLocationFromSpan(sf.Declaration.Span, documentUri);
                if (encSt.Methods.TryGetValue(word, out var sm) && sm.Declaration is not null)
                    return MakeLocationFromSpan(sm.Declaration.Span, documentUri);
            }
        }

        // 4. Local variable / parameter definition scan in the current file above cursor
        var localLoc = TryFindLocalDeclarationInDocument(documentText, documentUri, line, word);
        if (localLoc is not null)
            return localLoc;

        return null;
    }

    // ========================================================================
    // 4. Signature Help (textDocument/signatureHelp)
    // ========================================================================

    internal static object? GetSignatureHelp(
        string documentText,
        int line,
        int character,
        SymbolTable? symbols)
    {
        string linePrefix = GetLinePrefix(documentText, line, character);
        var callCtx = TryFindEnclosingCallContext(linePrefix);
        if (callCtx is null)
            return null;

        var (calleeName, receiverName, activeParam) = callCtx.Value;

        BuiltinDocInfo? docInfo = null;
        if (!string.IsNullOrEmpty(receiverName))
        {
            if (symbols is not null && symbols.Actors.TryGetValue(receiverName, out var actSym) &&
                actSym.Methods.TryGetValue(calleeName, out var actMethod))
            {
                string[] ps = actMethod.Parameters.Select(p => $"{p.Name}: {p.Type}").ToArray();
                docInfo = new BuiltinDocInfo(
                    $"fn {actSym.Name}.{actMethod.Name}({string.Join(", ", ps)}) -> {actMethod.ReturnType}",
                    $"Method on actor `{actSym.Name}`.",
                    ps
                );
            }
            else
            {
                BuiltinMemberDocs.TryGetValue(calleeName, out docInfo);
            }
        }
        else
        {
            if (!BuiltinDocs.TryGetValue(calleeName, out docInfo) &&
                symbols is not null &&
                symbols.Functions.TryGetValue(calleeName, out var fnSym))
            {
                string[] ps = fnSym.Parameters.Select(p => $"{p.Name}: {p.Type}").ToArray();
                docInfo = new BuiltinDocInfo(
                    $"fn {fnSym.Name}({string.Join(", ", ps)}) -> {fnSym.ReturnType}",
                    $"User function `{fnSym.Name}`.",
                    ps
                );
            }
        }

        if (docInfo is null)
            return null;

        int clampedParam = docInfo.Parameters.Length > 0
            ? Math.Clamp(activeParam, 0, docInfo.Parameters.Length - 1)
            : 0;

        return new
        {
            signatures = new[]
            {
                new
                {
                    label = docInfo.Signature,
                    documentation = docInfo.Description,
                    parameters = docInfo.Parameters.Select(static p => new { label = p }).ToArray()
                }
            },
            activeSignature = 0,
            activeParameter = clampedParam
        };
    }

    // ========================================================================
    // 5. Hierarchical Document Symbols / Outline (textDocument/documentSymbol)
    // ========================================================================

    internal static object[] GetDocumentSymbols(
        string documentText,
        string? documentFilePath,
        ProgramNode? fallbackProgram = null)
    {
        string? canonicalDoc = !string.IsNullOrEmpty(documentFilePath) ? Path.GetFullPath(documentFilePath) : null;
        IEnumerable<TopDecl> declarations;

        if (PspParser.TryParse(documentText, out var parsed, out _, canonicalDoc) && parsed is not null)
        {
            declarations = parsed.Declarations;
        }
        else if (fallbackProgram is not null)
        {
            declarations = fallbackProgram.Declarations.Where(d =>
                canonicalDoc is null ||
                string.IsNullOrEmpty(d.Span.FilePath) ||
                string.Equals(Path.GetFullPath(d.Span.FilePath), canonicalDoc, StringComparison.Ordinal));
        }
        else
        {
            return [];
        }

        var result = new List<object>();

        foreach (var decl in declarations)
        {
            switch (decl)
            {
                case GameConfigDecl game:
                    result.Add(MakeDocSymbol($"game \"{game.Title}\"", "game config", SymPackage, game.Span));
                    break;

                case AssetDecl asset:
                    result.Add(MakeDocSymbol(asset.Name, $"asset<{asset.AssetKind}>", SymConstant, asset.Span));
                    break;

                case ConstDecl constDecl:
                    string constDetail = constDecl.ExplicitType is not null
                        ? $"const {FormatTypeRef(constDecl.ExplicitType)}"
                        : "const";
                    result.Add(MakeDocSymbol(constDecl.Name, constDetail, SymConstant, constDecl.Span));
                    break;

                case EnumDecl en:
                    var variants = en.Variants
                        .Select((v, idx) => MakeDocSymbol(v, $"= {idx}", SymEnumMember, en.Span))
                        .ToArray();
                    result.Add(MakeDocSymbol(en.Name, "enum", SymEnum, en.Span, variants));
                    break;

                case StructDecl st:
                    var sFields = st.Fields
                        .Select(f => MakeDocSymbol(f.Name, FormatTypeRef(f.Type), SymField, f.Span))
                        .ToArray();
                    result.Add(MakeDocSymbol(st.Name, "struct", SymStruct, st.Span, sFields));
                    break;

                case MessageDecl msg:
                    var mFields = msg.Fields
                        .Select(f => MakeDocSymbol(f.Name, FormatTypeRef(f.Type), SymField, f.Span))
                        .ToArray();
                    result.Add(MakeDocSymbol(msg.Name, "message", SymStruct, msg.Span, mFields));
                    break;

                case ActorDecl act:
                    string detail = act.PoolCapacity.HasValue ? $"actor[{act.PoolCapacity.Value}]" : "actor (singleton)";
                    result.Add(MakeDocSymbol(act.Name, detail, SymClass, act.Span,
                        BuildContainerChildren(act.Fields, act.Hooks, act.Handlers, act.Methods)));
                    break;

                case StateDecl state:
                    result.Add(MakeDocSymbol(state.Name, "state", SymClass, state.Span,
                        BuildContainerChildren(state.Fields, state.Hooks, state.Handlers, state.Methods)));
                    break;

                case GlobalVarDecl gvar:
                    string scopeLabel = gvar.Scope == MemoryScope.LevelArena ? "level var" : "global var";
                    result.Add(MakeDocSymbol(gvar.Name, scopeLabel, SymVariable, gvar.Span));
                    break;

                case FuncDecl fn:
                    result.Add(MakeDocSymbol(fn.Name, $"fn -> {FormatTypeRef(fn.ReturnType)}", SymFunction, fn.Span));
                    break;
            }
        }

        return result.ToArray();
    }

    // ========================================================================
    // Internal Helpers
    // ========================================================================

    private static object[] BuildContainerChildren(
        IReadOnlyList<FieldDecl> fields,
        IReadOnlyList<LifecycleHook> hooks,
        IReadOnlyList<MessageHandler> handlers,
        IReadOnlyList<FuncDecl> methods)
    {
        var children = new List<object>();

        foreach (var f in fields)
            children.Add(MakeDocSymbol(f.Name, FormatTypeRef(f.Type), SymField, f.Span));

        foreach (var h in hooks)
            children.Add(MakeDocSymbol(h.Kind.ToString().ToLowerInvariant(), "lifecycle hook", SymMethod, h.Span));

        foreach (var mh in handlers)
            children.Add(MakeDocSymbol($"on {mh.MessageName}({mh.ParamName})", "message handler", SymEvent, mh.Span));

        foreach (var fn in methods)
            children.Add(MakeDocSymbol(fn.Name, $"fn -> {FormatTypeRef(fn.ReturnType)}", SymMethod, fn.Span));

        return children.ToArray();
    }

    private static object MakeDocSymbol(string name, string detail, int kind, SourceSpan span, object[]? children = null)
    {
        int l = Math.Max(0, span.Line - 1);
        int c = Math.Max(0, span.Column - 1);
        int len = Math.Max(1, span.Length);
        var range = new
        {
            start = new { line = l, character = c },
            end = new { line = l, character = c + len }
        };

        return new
        {
            name,
            detail,
            kind,
            range,
            selectionRange = range,
            children = children ?? []
        };
    }

    private static string FormatTypeRef(TypeRef? tr) => tr switch
    {
        BuiltinTypeRef b => b.Name,
        NamedTypeRef n => n.Name,
        ActorRefTypeRef a => $"ref<{a.ActorName}>",
        FixedArrayTypeRef f => f.LengthExpr is IntLiteralExpr il
            ? $"[{il.Value}]{FormatTypeRef(f.ElementType)}"
            : f.LengthExpr is IdentifierExpr id
                ? $"[{id.Name}]{FormatTypeRef(f.ElementType)}"
                : $"[...]{FormatTypeRef(f.ElementType)}",
        null => "inferred",
        _ => "any"
    };

    private static IReadOnlyList<LspCompletionItem> GetImportCompletions(string? documentFilePath)
    {
        var items = new List<LspCompletionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string stdLibDir in EnumerateStdLibDirectories())
        {
            if (!Directory.Exists(stdLibDir))
                continue;

            foreach (string file in Directory.GetFiles(stdLibDir, "*.psps"))
            {
                string modName = Path.GetFileNameWithoutExtension(file);
                if (seen.Add(modName))
                {
                    items.Add(new LspCompletionItem(modName, KindModule, "StdLib module", $"Standard library module `StdLib/{modName}.psps`"));
                }
            }
        }

        string? docDir = !string.IsNullOrEmpty(documentFilePath) ? Path.GetDirectoryName(documentFilePath) : null;
        if (!string.IsNullOrEmpty(docDir) && Directory.Exists(docDir))
        {
            string? selfName = Path.GetFileName(documentFilePath);
            foreach (string file in Directory.GetFiles(docDir, "*.psps"))
            {
                string fileName = Path.GetFileName(file);
                if (string.Equals(fileName, selfName, StringComparison.Ordinal))
                    continue;
                string modName = Path.GetFileNameWithoutExtension(file);
                if (seen.Add(modName))
                {
                    items.Add(new LspCompletionItem(modName, KindModule, "Local module"));
                }
            }
        }

        return items;
    }

    private static IEnumerable<string> EnumerateStdLibDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "StdLib");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir?.Parent is not null)
        {
            dir = dir.Parent;
            yield return Path.Combine(dir.FullName, "StdLib");
        }
        yield return Path.Combine(Directory.GetCurrentDirectory(), "StdLib");
    }

    private static IReadOnlyList<LspCompletionItem> GetActorCompletions(SymbolTable? symbols, bool pooledOnly)
    {
        if (symbols is null)
            return [];

        return symbols.Actors.Values
            .Where(a => !pooledOnly || a.PoolCapacity.HasValue)
            .Select(a => new LspCompletionItem(
                a.Name,
                KindClass,
                a.PoolCapacity.HasValue ? $"actor {a.Name}[{a.PoolCapacity.Value}]" : $"actor {a.Name} (singleton)"
            ))
            .ToList();
    }

    private static IReadOnlyList<LspCompletionItem> GetStateCompletions(SymbolTable? symbols)
    {
        if (symbols is null)
            return [];

        return symbols.States.Values
            .Select(s => new LspCompletionItem(s.Name, KindClass, $"state {s.Name}"))
            .ToList();
    }

    private static IReadOnlyList<LspCompletionItem> GetMessageCompletions(SymbolTable? symbols)
    {
        if (symbols is null)
            return [];

        return symbols.Messages.Values
            .Select(m => new LspCompletionItem(m.Name, KindStruct, $"message {m.Name}"))
            .ToList();
    }

    private static IReadOnlyList<LspCompletionItem>? TryGetAssetOptionCompletions(string linePrefix, SymbolTable? symbols)
    {
        var match = Regex.Match(linePrefix, @"\b(texture|tilemap|sound|music)\s*\(\s*""[^""]*""\s*,\s*[A-Za-z_0-9]*$");
        if (!match.Success)
            return null;

        string loader = match.Groups[1].Value;
        var items = new List<LspCompletionItem>();

        switch (loader)
        {
            case "texture":
                items.Add(new LspCompletionItem("format: ABGR8888", KindProperty, "32-bit full RGBA color", InsertText: "format: ABGR8888"));
                items.Add(new LspCompletionItem("format: RGBA4444", KindProperty, "16-bit compact texture (halves VRAM)", InsertText: "format: RGBA4444"));
                break;

            case "tilemap":
                items.Add(new LspCompletionItem("mutable: true", KindProperty, "Enable runtime set_tile / set_solid edits", InsertText: "mutable: true"));
                items.Add(new LspCompletionItem("collision_layer: \"Collision\"", KindProperty, "Collision tile layer name", InsertText: "collision_layer: \"Collision\""));
                items.Add(new LspCompletionItem("draw_collision: true", KindProperty, "Render collision layer tiles visually", InsertText: "draw_collision: true"));
                items.Add(new LspCompletionItem("tileset:", KindProperty, "Explicit shared texture asset", InsertText: "tileset: "));
                if (symbols is not null)
                {
                    foreach (var tex in symbols.Assets.Values.Where(static a => a.AssetKind == "texture"))
                    {
                        items.Add(new LspCompletionItem($"tileset: {tex.Name}", KindVariable, $"asset<texture> {tex.Name}", InsertText: $"tileset: {tex.Name}"));
                    }
                }
                break;

            case "sound" or "music":
                items.Add(new LspCompletionItem("rate: 44100", KindProperty, "44.1 kHz native PSP audio sample rate", InsertText: "rate: 44100"));
                items.Add(new LspCompletionItem("rate: 22050", KindProperty, "22.05 kHz half-rate (halves RAM footprint)", InsertText: "rate: 22050"));
                break;
        }

        return items;
    }

    private static IReadOnlyList<LspCompletionItem>? TryGetInitializerFieldCompletions(
        string documentText,
        int line,
        int character,
        SymbolTable? symbols)
    {
        if (symbols is null)
            return null;

        string linePrefix = GetLinePrefix(documentText, line, character);
        var spawnMatch = Regex.Match(linePrefix, @"\bspawn\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{[^}]*$");
        if (spawnMatch.Success && symbols.Actors.TryGetValue(spawnMatch.Groups[1].Value, out var actSym))
        {
            return actSym.Fields.Values
                .Select(f => new LspCompletionItem(f.Name, KindField, $"{f.Name}: {f.Type}", InsertText: $"{f.Name}: "))
                .ToList();
        }

        var sendMatch = Regex.Match(linePrefix, @"\bsend\s*\([^,]+,\s*([A-Za-z_][A-Za-z0-9_]*)\s*\{[^}]*$");
        if (sendMatch.Success && symbols.Messages.TryGetValue(sendMatch.Groups[1].Value, out var msgSym))
        {
            return msgSym.Fields.Values
                .Select(f => new LspCompletionItem(f.Name, KindField, $"{f.Name}: {f.Type}", InsertText: $"{f.Name}: "))
                .ToList();
        }

        return null;
    }

    private static IReadOnlyList<LspCompletionItem> GetGameConfigKeyCompletions() =>
    [
        new("entry", KindProperty, "Initial game state name", InsertText: "entry: "),
        new("ram_budget", KindProperty, "Static RAM budget (e.g. 16MB)", InsertText: "ram_budget: 16MB"),
        new("fps", KindProperty, "Target frame rate (60 or 30)", InsertText: "fps: 60"),
        new("id", KindProperty, "9-char PSP disc ID (e.g. \"PSPS00001\")", InsertText: "id: \"PSPS00001\""),
        new("version", KindProperty, "App version string (e.g. \"01.00\")", InsertText: "version: \"01.00\""),
        new("extra_ram", KindProperty, "Enable 64MB PSP-2000/3000 RAM mode", InsertText: "extra_ram: true"),
        new("window_scale", KindProperty, "Desktop preview window scale (1..4)", InsertText: "window_scale: 2"),
        new("icon", KindProperty, "XMB ICON0.PNG path (144x80)", InsertText: "icon: \"icon.png\""),
        new("background", KindProperty, "XMB PIC1.PNG path (480x272)", InsertText: "background: \"bg.png\""),
        new("overlay", KindProperty, "XMB PIC0.PNG path (310x180)", InsertText: "overlay: \"overlay.png\"")
    ];

    private static List<LspCompletionItem> GetMemberCompletions(
        string receiver,
        string documentText,
        int line,
        SymbolTable? symbols)
    {
        var items = new List<LspCompletionItem>();
        if (symbols is null)
            return items;

        // 1. `self.<field|method>` inside enclosing actor or state
        if (receiver == "self")
        {
            var (encKind, encName) = FindEnclosingContainer(documentText, line);
            if (encKind == "actor" && encName is not null && symbols.Actors.TryGetValue(encName, out var selfAct))
            {
                AddActorMembers(selfAct, items);
                return items;
            }
            if (encKind == "state" && encName is not null && symbols.States.TryGetValue(encName, out var selfSt))
            {
                AddStateMembers(selfSt, items);
                return items;
            }
        }

        // 2. `msg.<field>` inside `on MessageName(msg)`
        string? handlerMsgType = FindEnclosingMessageHandlerType(documentText, line, receiver);
        if (handlerMsgType is not null && symbols.Messages.TryGetValue(handlerMsgType, out var msgSym))
        {
            foreach (var f in msgSym.Fields.Values)
            {
                items.Add(new LspCompletionItem(f.Name, KindField, f.Type.DisplayName));
            }
            return items;
        }

        // 3. `EnumName.<Variant>`
        if (symbols.Enums.TryGetValue(receiver, out var enumSym))
        {
            for (int i = 0; i < enumSym.Variants.Count; i++)
            {
                items.Add(new LspCompletionItem(enumSym.Variants[i], KindEnumMember, $"{enumSym.Name}.{enumSym.Variants[i]} = {i}"));
            }
            return items;
        }

        // 4. Singleton actor direct access: `PlayerPaddle.<field|method>`
        if (symbols.Actors.TryGetValue(receiver, out var actorSym))
        {
            AddActorMembers(actorSym, items);
            return items;
        }

        // 5. Asset direct access: `level1.<method|prop>`, `sfx_hit.<method>`, `bgm.<method>`
        if (symbols.Assets.TryGetValue(receiver, out var assetSym))
        {
            AddAssetMembers(assetSym, items);
            return items;
        }

        // 6. Variable / field / `each item in Pool` type resolution
        ResolvedType? resolvedType = ResolveReceiverTypeAtLine(receiver, documentText, line, symbols);
        if (resolvedType is not null)
        {
            AddTypeMembers(resolvedType, symbols, items);
        }

        return items;
    }

    private static ResolvedType? ResolveReceiverTypeAtLine(
        string name,
        string documentText,
        int line,
        SymbolTable symbols)
    {
        // Check `each <name> in <PoolName>` enclosing loop first
        string? poolActor = FindEnclosingEachLoopActor(documentText, line, name);
        if (poolActor is not null && symbols.Actors.ContainsKey(poolActor))
        {
            return new ActorRefType(poolActor);
        }

        return TryInferIdentifierTypeAtLine(name, documentText, line, symbols);
    }

    private static ResolvedType? TryInferIdentifierTypeAtLine(
        string name,
        string documentText,
        int line,
        SymbolTable symbols)
    {
        if (symbols.Globals.TryGetValue(name, out var gSym))
            return gSym.Type;

        // Prefer enclosing actor/state fields first
        var (encKind, encName) = FindEnclosingContainer(documentText, line);
        if (encKind == "actor" && encName is not null && symbols.Actors.TryGetValue(encName, out var encActor) &&
            encActor.Fields.TryGetValue(name, out var af))
        {
            return af.Type;
        }
        if (encKind == "state" && encName is not null && symbols.States.TryGetValue(encName, out var encState) &&
            encState.Fields.TryGetValue(name, out var sf))
        {
            return sf.Type;
        }

        foreach (var actor in symbols.Actors.Values)
        {
            if (actor.Fields.TryGetValue(name, out var f))
                return f.Type;
        }
        foreach (var state in symbols.States.Values)
        {
            if (state.Fields.TryGetValue(name, out var f))
                return f.Type;
        }

        // `var e = spawn Enemy`
        var spawnMatch = Regex.Match(documentText, $@"\bvar\s+{Regex.Escape(name)}\s*=\s*spawn\s+([A-Za-z_][A-Za-z0-9_]*)");
        if (spawnMatch.Success)
            return new ActorRefType(spawnMatch.Groups[1].Value);

        // `for i in a..b`
        if (Regex.IsMatch(documentText, $@"\bfor\s+{Regex.Escape(name)}\s+in\b"))
            return ResolvedType.Int;

        // Explicit type annotation: `var name: Type` or `name: Type`
        var m = Regex.Match(documentText, $@"\b{Regex.Escape(name)}\s*:\s*(ref<\s*([A-Za-z_][A-Za-z0-9_]*)\s*>|\[[0-9]+\]\s*[A-Za-z_][A-Za-z0-9_]*|([A-Za-z_][A-Za-z0-9_]*))");
        if (m.Success)
        {
            if (m.Groups[2].Success)
                return new ActorRefType(m.Groups[2].Value);

            if (m.Groups[1].Value.StartsWith('['))
                return new FixedArrayType(1, ResolvedType.Int);

            string typeName = m.Groups[3].Value;
            var prim = ResolvedType.TryGetBuiltin(typeName);
            if (prim is not null)
                return prim;
            if (symbols.Structs.TryGetValue(typeName, out var st))
                return st.Type;
            if (symbols.Enums.TryGetValue(typeName, out var en))
                return en.Type;
        }

        return null;
    }

    private static void AddTypeMembers(ResolvedType type, SymbolTable symbols, List<LspCompletionItem> items)
    {
        switch (type)
        {
            case PrimitiveType { Kind: PrimitiveKind.Vec2 }:
                items.Add(new LspCompletionItem("x", KindField, "f32"));
                items.Add(new LspCompletionItem("y", KindField, "f32"));
                items.Add(new LspCompletionItem("length", KindMethod, "() -> f32"));
                items.Add(new LspCompletionItem("normalized", KindMethod, "() -> vec2"));
                items.Add(new LspCompletionItem("dot", KindMethod, "(other: vec2) -> f32"));
                break;

            case PrimitiveType { Kind: PrimitiveKind.Vec3 }:
                items.Add(new LspCompletionItem("x", KindField, "f32"));
                items.Add(new LspCompletionItem("y", KindField, "f32"));
                items.Add(new LspCompletionItem("z", KindField, "f32"));
                items.Add(new LspCompletionItem("length", KindMethod, "() -> f32"));
                items.Add(new LspCompletionItem("normalized", KindMethod, "() -> vec3"));
                items.Add(new LspCompletionItem("dot", KindMethod, "(other: vec3) -> f32"));
                break;

            case PrimitiveType { Kind: PrimitiveKind.Rect }:
                items.Add(new LspCompletionItem("x", KindField, "f32"));
                items.Add(new LspCompletionItem("y", KindField, "f32"));
                items.Add(new LspCompletionItem("w", KindField, "f32"));
                items.Add(new LspCompletionItem("h", KindField, "f32"));
                items.Add(new LspCompletionItem("center", KindProperty, "vec2 (read-only)"));
                items.Add(new LspCompletionItem("overlaps", KindMethod, "(other: rect) -> bool"));
                items.Add(new LspCompletionItem("contains", KindMethod, "(point: vec2) -> bool"));
                break;

            case PrimitiveType { Kind: PrimitiveKind.Color }:
                items.Add(new LspCompletionItem("r", KindProperty, "u8"));
                items.Add(new LspCompletionItem("g", KindProperty, "u8"));
                items.Add(new LspCompletionItem("b", KindProperty, "u8"));
                items.Add(new LspCompletionItem("a", KindProperty, "u8"));
                break;

            case PrimitiveType { Kind: PrimitiveKind.Timer }:
                items.Add(new LspCompletionItem("remaining", KindField, "f32"));
                items.Add(new LspCompletionItem("duration", KindField, "f32"));
                items.Add(new LspCompletionItem("elapsed", KindMethod, "() -> bool"));
                items.Add(new LspCompletionItem("ready", KindMethod, "() -> bool"));
                items.Add(new LspCompletionItem("reset", KindMethod, "([duration: f32]) -> void"));
                break;

            case FixedArrayType:
                items.Add(new LspCompletionItem("len", KindProperty, "int (constant capacity)"));
                break;

            case StructType st when symbols.Structs.TryGetValue(st.Name, out var structSym):
                foreach (var field in structSym.Fields.Values)
                {
                    items.Add(new LspCompletionItem(field.Name, KindField, field.Type.DisplayName));
                }
                break;

            case ActorRefType ar when symbols.Actors.TryGetValue(ar.ActorName, out var actorSym):
                AddActorMembers(actorSym, items);
                break;
        }
    }

    private static void AddActorMembers(ActorSymbol actorSym, List<LspCompletionItem> items)
    {
        foreach (var field in actorSym.Fields.Values)
        {
            items.Add(new LspCompletionItem(field.Name, KindField, field.Type.DisplayName));
        }
        foreach (var method in actorSym.Methods.Values)
        {
            string p = string.Join(", ", method.Parameters.Select(x => $"{x.Name}: {x.Type}"));
            items.Add(new LspCompletionItem(method.Name, KindMethod, $"({p}) -> {method.ReturnType}"));
        }
    }

    private static void AddStateMembers(StateSymbol stateSym, List<LspCompletionItem> items)
    {
        foreach (var field in stateSym.Fields.Values)
        {
            items.Add(new LspCompletionItem(field.Name, KindField, field.Type.DisplayName));
        }
        foreach (var method in stateSym.Methods.Values)
        {
            string p = string.Join(", ", method.Parameters.Select(x => $"{x.Name}: {x.Type}"));
            items.Add(new LspCompletionItem(method.Name, KindMethod, $"({p}) -> {method.ReturnType}"));
        }
    }

    private static void AddAssetMembers(AssetSymbol assetSym, List<LspCompletionItem> items)
    {
        if (assetSym.AssetKind == "tilemap")
        {
            items.Add(new LspCompletionItem("cols", KindProperty, "int (read-only)"));
            items.Add(new LspCompletionItem("rows", KindProperty, "int (read-only)"));
            items.Add(new LspCompletionItem("tile_size", KindProperty, "int (read-only)"));
            items.Add(new LspCompletionItem("layer_count", KindProperty, "int (read-only)"));
            items.Add(new LspCompletionItem("draw", KindMethod, "([origin: vec2]) -> void"));
            items.Add(new LspCompletionItem("draw_layer", KindMethod, "(layer: int[, origin: vec2]) -> void"));
            items.Add(new LspCompletionItem("get_tile", KindMethod, "(layer: int, col: int, row: int) -> int"));
            items.Add(new LspCompletionItem("is_solid", KindMethod, "(col: int, row: int) -> bool"));
            items.Add(new LspCompletionItem("collides_rect", KindMethod, "(hitbox: rect) -> bool"));
            if (assetSym.IsMutable)
            {
                items.Add(new LspCompletionItem("set_tile", KindMethod, "(layer: int, col: int, row: int, tile_id: int) -> void"));
                items.Add(new LspCompletionItem("set_solid", KindMethod, "(col: int, row: int, solid: bool) -> void"));
                items.Add(new LspCompletionItem("reset", KindMethod, "() -> void"));
            }
        }
        else if (assetSym.AssetKind == "sound")
        {
            items.Add(new LspCompletionItem("play", KindMethod, "([volume: f32]) -> void"));
        }
        else if (assetSym.AssetKind == "music")
        {
            items.Add(new LspCompletionItem("play", KindMethod, "([volume: f32]) -> void"));
            items.Add(new LspCompletionItem("stop", KindMethod, "() -> void"));
            items.Add(new LspCompletionItem("pause", KindMethod, "() -> void"));
            items.Add(new LspCompletionItem("resume", KindMethod, "() -> void"));
        }
    }

    private static IReadOnlyList<LspCompletionItem> GetGeneralCompletions(
        string documentText,
        int line,
        SymbolTable? symbols)
    {
        var items = new List<LspCompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddUnique(LspCompletionItem item)
        {
            if (seen.Add($"{item.Kind}:{item.Label}"))
                items.Add(item);
        }

        var effectiveSymbols = symbols ?? new SymbolTable();

        // Include enclosing actor/state fields and methods first when inside a container body
        var (encKind, encName) = FindEnclosingContainer(documentText, line);
        if (encKind == "actor" && encName is not null && effectiveSymbols.Actors.TryGetValue(encName, out var encAct))
        {
            var memberItems = new List<LspCompletionItem>();
            AddActorMembers(encAct, memberItems);
            foreach (var m in memberItems)
                AddUnique(m);
        }
        else if (encKind == "state" && encName is not null && effectiveSymbols.States.TryGetValue(encName, out var encSt))
        {
            var memberItems = new List<LspCompletionItem>();
            AddStateMembers(encSt, memberItems);
            foreach (var m in memberItems)
                AddUnique(m);
        }

        foreach (string kw in LanguageKeywords)
            AddUnique(new LspCompletionItem(kw, KindKeyword, "keyword"));

        foreach (string t in PrimitiveTypeNames)
            AddUnique(new LspCompletionItem(t, KindTypeParameter, "built-in type"));

        // Asset loaders
        AddUnique(new LspCompletionItem("texture", KindFunction, "texture(path, format: RGBA4444|ABGR8888)"));
        AddUnique(new LspCompletionItem("tilemap", KindFunction, "tilemap(path, tileset: ..., mutable: bool, collision_layer: string, draw_collision: bool)"));
        AddUnique(new LspCompletionItem("sound", KindFunction, "sound(path, rate: 44100|22050)"));
        AddUnique(new LspCompletionItem("music", KindFunction, "music(path, rate: 44100|22050)"));

        foreach (var fn in effectiveSymbols.Functions.Values)
        {
            string p = string.Join(", ", fn.Parameters.Select(x => $"{x.Name}: {x.Type}"));
            AddUnique(new LspCompletionItem(fn.Name, KindFunction, $"fn({p}) -> {fn.ReturnType}"));
        }

        foreach (var c in effectiveSymbols.Constants.Values)
            AddUnique(new LspCompletionItem(c.Name, KindConstant, $"const {c.Type} = {c.Value.FormatDisplay()}"));

        foreach (var g in effectiveSymbols.Globals.Values)
            AddUnique(new LspCompletionItem(g.Name, KindVariable, $"{g.Scope} {g.Type}"));

        foreach (var a in effectiveSymbols.Assets.Values)
            AddUnique(new LspCompletionItem(a.Name, KindVariable, $"asset<{a.AssetKind}>"));

        foreach (var e in effectiveSymbols.Enums.Values)
            AddUnique(new LspCompletionItem(e.Name, KindEnum, $"enum ({e.Variants.Count} variants)"));

        foreach (var s in effectiveSymbols.Structs.Values)
            AddUnique(new LspCompletionItem(s.Name, KindStruct, "struct"));

        foreach (var act in effectiveSymbols.Actors.Values)
            AddUnique(new LspCompletionItem(act.Name, KindClass, act.PoolCapacity.HasValue ? $"actor[{act.PoolCapacity.Value}]" : "actor"));

        foreach (var st in effectiveSymbols.States.Values)
            AddUnique(new LspCompletionItem(st.Name, KindClass, "state"));

        foreach (var msg in effectiveSymbols.Messages.Values)
            AddUnique(new LspCompletionItem(msg.Name, KindStruct, "message"));

        return items;
    }

    // ========================================================================
    // Context Scanning & Location Utilities
    // ========================================================================

    private static (string? Kind, string? Name) FindEnclosingContainer(string documentText, int zeroBasedLine)
    {
        string[] lines = documentText.Replace("\r\n", "\n").Split('\n');
        int maxLine = Math.Clamp(zeroBasedLine, 0, lines.Length - 1);
        int braceDepth = 0;

        for (int i = maxLine; i >= 0; i--)
        {
            string l = lines[i];
            for (int c = l.Length - 1; c >= 0; c--)
            {
                if (l[c] == '}') braceDepth++;
                else if (l[c] == '{') braceDepth--;
            }

            if (braceDepth < 0)
            {
                var m = Regex.Match(l, @"^\s*(actor|state)\s+([A-Za-z_][A-Za-z0-9_]*)");
                if (m.Success)
                {
                    return (m.Groups[1].Value, m.Groups[2].Value);
                }
            }
        }

        return (null, null);
    }

    private static string? FindEnclosingMessageHandlerType(string documentText, int zeroBasedLine, string paramName)
    {
        string[] lines = documentText.Replace("\r\n", "\n").Split('\n');
        int maxLine = Math.Clamp(zeroBasedLine, 0, lines.Length - 1);
        int braceDepth = 0;

        for (int i = maxLine; i >= 0; i--)
        {
            string l = lines[i];
            for (int c = l.Length - 1; c >= 0; c--)
            {
                if (l[c] == '}') braceDepth++;
                else if (l[c] == '{') braceDepth--;
            }

            if (braceDepth < 0)
            {
                var m = Regex.Match(l, $@"\bon\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*{Regex.Escape(paramName)}\s*\)");
                if (m.Success)
                    return m.Groups[1].Value;
            }
        }

        return null;
    }

    private static string? FindEnclosingEachLoopActor(string documentText, int zeroBasedLine, string iterName)
    {
        string[] lines = documentText.Replace("\r\n", "\n").Split('\n');
        int maxLine = Math.Clamp(zeroBasedLine, 0, lines.Length - 1);
        int braceDepth = 0;

        for (int i = maxLine; i >= 0; i--)
        {
            string l = lines[i];
            for (int c = l.Length - 1; c >= 0; c--)
            {
                if (l[c] == '}') braceDepth++;
                else if (l[c] == '{') braceDepth--;
            }

            if (braceDepth < 0)
            {
                var m = Regex.Match(l, $@"\beach\s+{Regex.Escape(iterName)}\s+in\s+([A-Za-z_][A-Za-z0-9_]*)");
                if (m.Success)
                    return m.Groups[1].Value;
            }
        }

        return null;
    }

    private static bool IsCursorInsideGameConfigBlock(string documentText, int zeroBasedLine)
    {
        string[] lines = documentText.Replace("\r\n", "\n").Split('\n');
        int maxLine = Math.Clamp(zeroBasedLine, 0, lines.Length - 1);
        int braceDepth = 0;

        for (int i = maxLine; i >= 0; i--)
        {
            string l = lines[i];
            for (int c = l.Length - 1; c >= 0; c--)
            {
                if (l[c] == '}') braceDepth++;
                else if (l[c] == '{') braceDepth--;
            }

            if (braceDepth < 0)
            {
                return Regex.IsMatch(l, @"^\s*game\s+""[^""]*""");
            }
        }

        return false;
    }

    private static (string Callee, string? Receiver, int ActiveParam)? TryFindEnclosingCallContext(string linePrefix)
    {
        int parenDepth = 0;
        int braceDepth = 0;
        int bracketDepth = 0;
        int commaCount = 0;
        bool inString = false;

        for (int i = linePrefix.Length - 1; i >= 0; i--)
        {
            char c = linePrefix[i];
            if (c == '"' && (i == 0 || linePrefix[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (inString)
                continue;

            switch (c)
            {
                case ')': parenDepth++; break;
                case '}': braceDepth++; break;
                case '{': if (braceDepth > 0) braceDepth--; break;
                case ']': bracketDepth++; break;
                case '[': if (bracketDepth > 0) bracketDepth--; break;
                case ',' when parenDepth == 0 && braceDepth == 0 && bracketDepth == 0:
                    commaCount++;
                    break;
                case '(':
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                    }
                    else
                    {
                        string beforeParen = linePrefix[..i].TrimEnd();
                        var m = Regex.Match(beforeParen, @"(?:([A-Za-z_][A-Za-z0-9_]*)\.)?([A-Za-z_][A-Za-z0-9_]*)$");
                        if (!m.Success)
                            return null;

                        string? receiver = m.Groups[1].Success ? m.Groups[1].Value : null;
                        string callee = m.Groups[2].Value;
                        return (callee, receiver, commaCount);
                    }
                    break;
            }
        }

        return null;
    }

    private static string? TryResolveImportPathForDefinition(
        string rawSpecifier,
        string? documentFilePath,
        int oneBasedLine,
        IReadOnlyList<ResolvedImportInfo>? resolvedImports)
    {
        string? canonicalDoc = !string.IsNullOrEmpty(documentFilePath) ? Path.GetFullPath(documentFilePath) : null;
        if (resolvedImports is not null)
        {
            var match = resolvedImports.FirstOrDefault(r =>
                (canonicalDoc is null || string.Equals(r.SourceFilePath, canonicalDoc, StringComparison.Ordinal)) &&
                (r.Line == oneBasedLine || string.Equals(r.RawSpecifier, rawSpecifier, StringComparison.Ordinal)));
            if (match is not null)
                return match.TargetFilePath;
        }

        string normalized = rawSpecifier.EndsWith(".psps", StringComparison.OrdinalIgnoreCase)
            ? rawSpecifier
            : $"{rawSpecifier}.psps";

        string? docDir = !string.IsNullOrEmpty(documentFilePath) ? Path.GetDirectoryName(documentFilePath) : null;
        if (!string.IsNullOrEmpty(docDir))
        {
            string localPath = Path.GetFullPath(Path.Combine(docDir, normalized));
            if (File.Exists(localPath))
                return localPath;
        }

        foreach (string stdDir in EnumerateStdLibDirectories())
        {
            string stdPath = Path.Combine(stdDir, normalized);
            if (File.Exists(stdPath))
                return Path.GetFullPath(stdPath);
        }

        return null;
    }

    private static object? TryFindLocalDeclarationInDocument(
        string documentText,
        string documentUri,
        int cursorLine,
        string word)
    {
        string[] lines = documentText.Replace("\r\n", "\n").Split('\n');
        int maxLine = Math.Clamp(cursorLine, 0, lines.Length - 1);

        var patterns = new[]
        {
            $@"\b(?:temp\s+)?var\s+({Regex.Escape(word)})\b",
            $@"\bconst\s+({Regex.Escape(word)})\b",
            $@"\b(?:for|each)\s+({Regex.Escape(word)})\s+in\b",
            $@"\bon\s+[A-Za-z_][A-Za-z0-9_]*\s*\(\s*({Regex.Escape(word)})\s*\)",
            $@"\b({Regex.Escape(word)})\s*:\s*(?:ref<|[A-Za-z_\[])"
        };

        for (int i = maxLine; i >= 0; i--)
        {
            foreach (string pat in patterns)
            {
                var m = Regex.Match(lines[i], pat);
                if (m.Success)
                {
                    int col1Based = m.Groups[1].Index + 1;
                    return MakeLocation(documentUri, i + 1, col1Based, word.Length);
                }
            }
        }

        return null;
    }

    private static object MakeLocationFromSpan(SourceSpan span, string fallbackUri)
    {
        string targetUri = !string.IsNullOrEmpty(span.FilePath)
            ? PspLanguageServer.LocalPathToUri(span.FilePath)
            : fallbackUri;
        int line = span.Line > 0 ? span.Line : 1;
        int col = span.Column > 0 ? span.Column : 1;
        int len = Math.Max(1, span.Length);
        return MakeLocation(targetUri, line, col, len);
    }

    private static object MakeLocation(string uri, int oneBasedLine, int oneBasedCol, int length)
    {
        int l = Math.Max(0, oneBasedLine - 1);
        int c = Math.Max(0, oneBasedCol - 1);
        return new
        {
            uri,
            range = new
            {
                start = new { line = l, character = c },
                end = new { line = l, character = c + Math.Max(1, length) }
            }
        };
    }

    private static string GetLinePrefix(string text, int line, int character)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (line < 0 || line >= lines.Length)
            return string.Empty;

        string current = lines[line];
        int len = Math.Clamp(character, 0, current.Length);
        return current[..len];
    }

    private static string? GetWordAtPosition(string text, int line, int character, out string? qualifierBeforeDot)
    {
        qualifierBeforeDot = null;
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (line < 0 || line >= lines.Length)
            return null;

        string current = lines[line];
        if (current.Length == 0)
            return null;

        int pos = Math.Clamp(character, 0, current.Length - 1);
        if (!IsIdentChar(current[pos]) && pos > 0 && IsIdentChar(current[pos - 1]))
            pos--;
        if (!IsIdentChar(current[pos]))
            return null;

        int start = pos;
        while (start > 0 && IsIdentChar(current[start - 1])) start--;
        int end = pos;
        while (end + 1 < current.Length && IsIdentChar(current[end + 1])) end++;

        if (start > 0 && current[start - 1] == '.')
        {
            int qEnd = start - 1;
            int qStart = qEnd;
            while (qStart > 0 && IsIdentChar(current[qStart - 1])) qStart--;
            if (qStart < qEnd)
            {
                qualifierBeforeDot = current[qStart..qEnd];
            }
        }

        return current[start..(end + 1)];
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
