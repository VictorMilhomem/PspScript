using System.Text.RegularExpressions;
using PspScript.Semantics;

namespace PspScript.Lsp;

internal sealed record LspCompletionItem(
    string Label,
    int Kind,
    string? Detail = null,
    string? Documentation = null,
    string? InsertText = null
);

/// <summary>
/// Context-aware autocomplete and hover provider backed by PSPScript's <see cref="SymbolTable"/>.
/// </summary>
internal static class LspCompletionProvider
{
    // LSP CompletionItemKind values
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
    private const int KindStruct = 22;
    private const int KindTypeParameter = 25;

    private static readonly string[] LanguageKeywords =
    [
        "import", "game", "asset", "message", "struct", "enum", "actor", "state",
        "global", "level", "temp", "var", "fn",
        "init", "update", "draw", "exit", "on",
        "if", "else", "match", "for", "each", "in", "break", "return",
        "send", "spawn", "destroy", "goto",
        "true", "false", "null", "self"
    ];

    private static readonly string[] PrimitiveTypeNames =
    [
        "int", "u8", "u16", "u32", "f32", "bool", "void",
        "vec2", "vec3", "rect", "color", "timer", "ref"
    ];

    public static IReadOnlyList<LspCompletionItem> GetCompletions(
        string documentText,
        string? documentFilePath,
        int line,
        int character,
        SymbolTable? symbols)
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
            return GetActorCompletions(symbols);
        }

        // 3. Dot-member completions: `<ident>.`
        var dotMatch = Regex.Match(linePrefix, @"([A-Za-z_][A-Za-z0-9_]*)\.\s*[A-Za-z0-9_]*$");
        if (dotMatch.Success)
        {
            string receiver = dotMatch.Groups[1].Value;
            var members = GetMemberCompletions(receiver, documentText, symbols);
            if (members.Count > 0)
                return members;
        }

        // 4. General identifier, keyword, type, builtin, and project symbol completions
        return GetGeneralCompletions(symbols);
    }

    public static string? GetHoverMarkdown(string documentText, int line, int character, SymbolTable? symbols)
    {
        string? word = GetWordAtPosition(documentText, line, character);
        if (string.IsNullOrEmpty(word))
            return null;

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
            if (symbols.Globals.TryGetValue(word, out var gSym))
            {
                return $"```pspscript\n{gSym.Scope.ToString().ToLowerInvariant()} var {gSym.Name}: {gSym.Type}\n```";
            }
            if (symbols.Functions.TryGetValue(word, out var fnSym))
            {
                string paramStr = string.Join(", ", fnSym.Parameters.Select(p => $"{p.Name}: {p.Type}"));
                return $"```pspscript\nfn {fnSym.Name}({paramStr}) -> {fnSym.ReturnType}\n```";
            }
        }

        return null;
    }

    private static IReadOnlyList<LspCompletionItem> GetImportCompletions(string? documentFilePath)
    {
        var items = new List<LspCompletionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Bundled StdLib modules
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

        // Sibling .psps files in the same directory
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

    private static IReadOnlyList<LspCompletionItem> GetActorCompletions(SymbolTable? symbols)
    {
        if (symbols is null)
            return [];

        return symbols.Actors.Values
            .Select(a => new LspCompletionItem(
                a.Name,
                KindClass,
                a.PoolCapacity.HasValue ? $"actor {a.Name}[{a.PoolCapacity.Value}]" : $"actor {a.Name} (singleton)"
            ))
            .ToList();
    }

    private static List<LspCompletionItem> GetMemberCompletions(string receiver, string documentText, SymbolTable? symbols)
    {
        var items = new List<LspCompletionItem>();
        if (symbols is null)
            return items;

        // EnumName.<Variant>
        if (symbols.Enums.TryGetValue(receiver, out var enumSym))
        {
            for (int i = 0; i < enumSym.Variants.Count; i++)
            {
                items.Add(new LspCompletionItem(enumSym.Variants[i], KindEnumMember, $"{enumSym.Name}.{enumSym.Variants[i]} = {i}"));
            }
            return items;
        }

        // Singleton actor direct access: PlayerPaddle.<field|method>
        if (symbols.Actors.TryGetValue(receiver, out var actorSym))
        {
            AddActorMembers(actorSym, items);
            return items;
        }

        // Asset direct access: level1.<method|prop>, sfx_hit.<method>, bgm.<method>
        if (symbols.Assets.TryGetValue(receiver, out var assetSym))
        {
            AddAssetMembers(assetSym, items);
            return items;
        }

        // Resolve variable / field type (from globals, actor/state fields, or local declaration regex in current buffer)
        ResolvedType? resolvedType = TryInferIdentifierType(receiver, documentText, symbols);
        if (resolvedType is not null)
        {
            AddTypeMembers(resolvedType, symbols, items);
        }

        return items;
    }

    private static ResolvedType? TryInferIdentifierType(string name, string documentText, SymbolTable symbols)
    {
        if (symbols.Globals.TryGetValue(name, out var gSym))
            return gSym.Type;

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

        // Lightweight fallback for local variable / parameter annotations: `var name: Type` or `name: Type`
        var m = Regex.Match(documentText, $@"\b{Regex.Escape(name)}\s*:\s*(ref<\s*([A-Za-z_][A-Za-z0-9_]*)\s*>|([A-Za-z_][A-Za-z0-9_]*))");
        if (m.Success)
        {
            if (m.Groups[2].Success)
                return new ActorRefType(m.Groups[2].Value);

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

    private static IReadOnlyList<LspCompletionItem> GetGeneralCompletions(SymbolTable? symbols)
    {
        var items = new List<LspCompletionItem>();

        foreach (string kw in LanguageKeywords)
            items.Add(new LspCompletionItem(kw, KindKeyword, "keyword"));

        foreach (string t in PrimitiveTypeNames)
            items.Add(new LspCompletionItem(t, KindTypeParameter, "built-in type"));

        // Asset loaders
        items.Add(new LspCompletionItem("texture", KindFunction, "texture(path, format: RGBA4444|ABGR8888)"));
        items.Add(new LspCompletionItem("tilemap", KindFunction, "tilemap(path, tileset: ..., mutable: bool)"));
        items.Add(new LspCompletionItem("sound", KindFunction, "sound(path, rate: 44100|22050)"));
        items.Add(new LspCompletionItem("music", KindFunction, "music(path, rate: 44100|22050)"));

        var effectiveSymbols = symbols ?? new SymbolTable();

        foreach (var fn in effectiveSymbols.Functions.Values)
        {
            string p = string.Join(", ", fn.Parameters.Select(x => $"{x.Name}: {x.Type}"));
            items.Add(new LspCompletionItem(fn.Name, KindFunction, $"fn({p}) -> {fn.ReturnType}"));
        }

        foreach (var g in effectiveSymbols.Globals.Values)
            items.Add(new LspCompletionItem(g.Name, KindVariable, $"{g.Scope} {g.Type}"));

        foreach (var a in effectiveSymbols.Assets.Values)
            items.Add(new LspCompletionItem(a.Name, KindVariable, $"asset<{a.AssetKind}>"));

        foreach (var e in effectiveSymbols.Enums.Values)
            items.Add(new LspCompletionItem(e.Name, KindEnum, $"enum ({e.Variants.Count} variants)"));

        foreach (var s in effectiveSymbols.Structs.Values)
            items.Add(new LspCompletionItem(s.Name, KindStruct, "struct"));

        foreach (var act in effectiveSymbols.Actors.Values)
            items.Add(new LspCompletionItem(act.Name, KindClass, act.PoolCapacity.HasValue ? $"actor[{act.PoolCapacity.Value}]" : "actor"));

        foreach (var st in effectiveSymbols.States.Values)
            items.Add(new LspCompletionItem(st.Name, KindClass, "state"));

        foreach (var msg in effectiveSymbols.Messages.Values)
            items.Add(new LspCompletionItem(msg.Name, KindStruct, "message"));

        return items;
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

    private static string? GetWordAtPosition(string text, int line, int character)
    {
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

        return current[start..(end + 1)];
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
