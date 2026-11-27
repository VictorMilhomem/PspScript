using System.Text;
using System.Text.Json;
using PspScript.CodeGen;
using PspScript.Lsp;
using PspScript.Parsing;
using PspScript.Semantics;
using Xunit;

namespace PspScript.Tests;

public sealed class ConstTests
{
    private static (SemanticAnalysisResult Analysis, string? CSource) Compile(string source)
    {
        bool parsed = PspParser.TryParse(source, out var program, out var err);
        Assert.True(parsed, $"Parse failed: {err?.Message}");
        Assert.NotNull(program);

        var analysis = SemanticAnalyzer.Analyze(program!);
        if (!analysis.Success || analysis.Model is null)
            return (analysis, null);

        string cSource = CCodeGenerator.Generate(program!, analysis.Model);
        return (analysis, cSource);
    }

    [Fact]
    public void TopLevelAndLocalConst_FoldingAndZeroStaticRamFootprint()
    {
        const string source = """
            const SCREEN_W = 480
            const SCREEN_H = 272
            const HALF_W: f32 = SCREEN_W / 2.0
            const MASK = (1 << 4) | (1 << 5)
            const FLAG = !false && (SCREEN_W > SCREEN_H)
            const HUD_COLOR = #FFF275
            const TITLE = "PSP Const"
            const CENTER: vec2 = { 240.0, 136.0 }

            state Main {
                update(dt: f32) {
                    const LOCAL_SCALE = 2.0
                    var x: f32 = HALF_W * LOCAL_SCALE + CENTER.x
                    var m: int = MASK
                    if FLAG {
                        draw_rect({ x, 0.0, 8.0, 8.0 }, HUD_COLOR)
                        draw_text(TITLE, { 0.0, 0.0 })
                    }
                }
            }
            """;

        var (analysis, cSource) = Compile(source);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(cSource);

        var symbols = analysis.Model!.Symbols;
        Assert.Equal(new IntConstValue(480), symbols.Constants["SCREEN_W"].Value);
        Assert.Equal(new FloatConstValue(240.0f), symbols.Constants["HALF_W"].Value);
        Assert.Equal(new IntConstValue(48), symbols.Constants["MASK"].Value);
        Assert.Equal(new BoolConstValue(true), symbols.Constants["FLAG"].Value);
        Assert.Equal(0, analysis.Model.EstimatedStaticMemoryBytes);

        Assert.Contains("static const int32_t g_const_SCREEN_W = (int32_t)480;", cSource);
        Assert.Contains("static const float g_const_HALF_W = 240.0f;", cSource);
        Assert.Contains("static const int32_t g_const_MASK = (int32_t)48;", cSource);
        Assert.Contains("const float LOCAL_SCALE = 2.0f;", cSource);
    }

    [Fact]
    public void ConstInArraySizesAndActorPoolCapacities_SupportsForwardReferencesAndExpressions()
    {
        const string source = """
            actor Bullet[MAX_BULLETS] {
                pos: vec2 = { 0.0, 0.0 }
            }

            state Play {
                grid: [MAP_W * MAP_H]u8
                init() {
                    var n: int = grid.len
                }
            }

            const MAX_BULLETS = 2 * 16
            const MAP_W = 10
            const MAP_H = 6
            """;

        var (analysis, cSource) = Compile(source);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(cSource);

        Assert.Equal(32, analysis.Model!.Symbols.Actors["Bullet"].PoolCapacity);
        var gridField = analysis.Model.Symbols.States["Play"].Fields["grid"];
        Assert.Equal(new FixedArrayType(60, ResolvedType.U8), gridField.Type);
        Assert.Contains("uint8_t grid[60];", cSource);
    }

    [Fact]
    public void ConstInMatchPatterns_SupportsIntegerAndEnumConstantsAndExhaustiveness()
    {
        const string source = """
            enum Mode { Menu, Play }
            const MODE_MENU = Mode.Menu
            const MODE_PLAY = Mode.Play
            const CODE_A = 10
            const CODE_B = CODE_A + 10

            state Main {
                mode: Mode = MODE_MENU
                code: int = CODE_A

                update(dt: f32) {
                    const LOCAL_CODE = 30
                    match mode {
                        MODE_MENU => code = CODE_B,
                        MODE_PLAY => code = LOCAL_CODE
                    }
                    match code {
                        CODE_A => mode = MODE_MENU,
                        CODE_B => mode = MODE_PLAY,
                        LOCAL_CODE => mode = MODE_MENU,
                        _ => {}
                    }
                }
            }
            """;

        var (analysis, cSource) = Compile(source);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(cSource);

        Assert.Contains("case Mode_Menu:", cSource);
        Assert.Contains("case Mode_Play:", cSource);
        Assert.Contains("case 10:", cSource);
        Assert.Contains("case 20:", cSource);
        Assert.Contains("case 30:", cSource);
    }

    [Fact]
    public void AssigningToTopLevelOrLocalConst_ReportsSemanticError()
    {
        const string source = """
            const LIMIT = 10
            state Main {
                init() {
                    const LOCAL = 5
                    LIMIT = 20
                    LOCAL = 6
                }
            }
            """;

        var (analysis, _) = Compile(source);
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("Cannot assign to compile-time constant 'LIMIT'"));
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("Cannot assign to compile-time constant 'LOCAL'"));
    }

    [Fact]
    public void NonConstantInitializerAndCircularConst_ReportSemanticErrors()
    {
        const string nonConstSrc = """
            global var runtime_val: int = 5
            const BAD = runtime_val + 1
            state Main {}
            """;
        var (nonConstRes, _) = Compile(nonConstSrc);
        Assert.False(nonConstRes.Success);
        Assert.Contains(nonConstRes.Diagnostics, d => d.Message.Contains("compile-time constant expression"));

        const string cycleSrc = """
            const A = B + 1
            const B = A + 1
            state Main {}
            """;
        var (cycleRes, _) = Compile(cycleSrc);
        Assert.False(cycleRes.Success);
        Assert.Contains(cycleRes.Diagnostics, d => d.Message.Contains("Circular constant definition"));
    }

    [Fact]
    public void DuplicateConstMatchArm_ReportsSemanticError()
    {
        const string source = """
            const ONE = 1
            state Main {
                x: int = 1
                init() {
                    match x {
                        1 => x = 2,
                        ONE => x = 3,
                        _ => {}
                    }
                }
            }
            """;

        var (analysis, _) = Compile(source);
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("Duplicate match arm for integer value '1'"));
    }

    [Fact]
    public async Task Lsp_SupportsHoverDefinitionAndOutlineForConst()
    {
        const string uri = "file:///tmp/lsp_const_test.psps";
        const string source = """
            const MAX_SPEED = 240.0
            state Main {
                update(dt: f32) {
                    var s: f32 = MAX_SPEED
                }
            }
            """;

        static byte[] Frame(object msg)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(msg);
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
            byte[] framed = new byte[header.Length + json.Length];
            Buffer.BlockCopy(header, 0, framed, 0, header.Length);
            Buffer.BlockCopy(json, 0, framed, header.Length, json.Length);
            return framed;
        }

        using var input = new MemoryStream();
        foreach (var msg in new object[]
        {
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri, languageId = "pspscript", version = 1, text = source } } },
            new { jsonrpc = "2.0", id = 10, method = "textDocument/hover", @params = new { textDocument = new { uri }, position = new { line = 3, character = 25 } } },
            new { jsonrpc = "2.0", id = 11, method = "textDocument/definition", @params = new { textDocument = new { uri }, position = new { line = 3, character = 25 } } },
            new { jsonrpc = "2.0", id = 12, method = "textDocument/documentSymbol", @params = new { textDocument = new { uri } } },
            new { jsonrpc = "2.0", id = 2, method = "shutdown" },
            new { jsonrpc = "2.0", method = "exit" }
        })
        {
            byte[] framed = Frame(msg);
            input.Write(framed, 0, framed.Length);
        }
        input.Position = 0;

        using var output = new MemoryStream();
        var server = new PspLanguageServer(input, output);
        await server.RunAsync();

        output.Position = 0;
        var reader = new LspTransport(output, Stream.Null);
        var responses = new List<JsonDocument>();
        while (await reader.ReadMessageAsync() is { } doc)
            responses.Add(doc);

        string hover = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 10)
            .RootElement.GetProperty("result").GetProperty("contents").GetProperty("value").GetString()!;
        Assert.Contains("const MAX_SPEED: f32 = 240.0", hover);

        var defLoc = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 11)
            .RootElement.GetProperty("result");
        Assert.Equal(0, defLoc.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        var symbols = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 12)
            .RootElement.GetProperty("result")
            .EnumerateArray()
            .Select(static s => s.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("MAX_SPEED", symbols);
    }
}
