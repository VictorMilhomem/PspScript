using System.Text;
using System.Text.Json;
using PspScript.Lsp;
using Xunit;

namespace PspScript.Tests;

public sealed class LspServerTests
{
    private static byte[] Frame(object message)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
        byte[] framed = new byte[header.Length + json.Length];
        Buffer.BlockCopy(header, 0, framed, 0, header.Length);
        Buffer.BlockCopy(json, 0, framed, header.Length, json.Length);
        return framed;
    }

    private static async Task<List<JsonDocument>> RunSessionAsync(params object[] messages)
    {
        using var input = new MemoryStream();
        foreach (var msg in messages)
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
        while (true)
        {
            var doc = await reader.ReadMessageAsync();
            if (doc is null)
                break;
            responses.Add(doc);
        }
        return responses;
    }

    [Fact]
    public async Task Initialize_ReturnsCapabilitiesAndServerInfo()
    {
        var responses = await RunSessionAsync(
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
            new { jsonrpc = "2.0", id = 2, method = "shutdown" },
            new { jsonrpc = "2.0", method = "exit" }
        );

        Assert.Equal(2, responses.Count);
        var initResult = responses[0].RootElement.GetProperty("result");
        Assert.Equal("pspscript-lsp", initResult.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(initResult.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
    }

    [Fact]
    public async Task DidOpenAndDidChange_PublishesAndClearsRealTimeDiagnostics()
    {
        const string uri = "file:///tmp/lsp_diag_test.psps";

        var responses = await RunSessionAsync(
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
            new
            {
                jsonrpc = "2.0",
                method = "textDocument/didOpen",
                @params = new
                {
                    textDocument = new
                    {
                        uri,
                        languageId = "pspscript",
                        version = 1,
                        text = """
                            enum Season { Spring, Summer }
                            state S {
                                s: Season
                                init() {
                                    match s {
                                        Season.Spring => s = Season.Summer
                                    }
                                }
                            }
                            """
                    }
                }
            },
            new
            {
                jsonrpc = "2.0",
                method = "textDocument/didChange",
                @params = new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[]
                    {
                        new
                        {
                            text = """
                                enum Season { Spring, Summer }
                                state S {
                                    s: Season
                                    init() {
                                        match s {
                                            Season.Spring => s = Season.Summer,
                                            Season.Summer => s = Season.Spring
                                        }
                                    }
                                }
                                """
                        }
                    }
                }
            },
            new { jsonrpc = "2.0", id = 2, method = "shutdown" },
            new { jsonrpc = "2.0", method = "exit" }
        );

        var diagNotifications = responses
            .Where(d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics")
            .ToList();

        Assert.Equal(2, diagNotifications.Count);

        // First publish: non-exhaustive match error
        var firstDiags = diagNotifications[0].RootElement.GetProperty("params").GetProperty("diagnostics");
        Assert.Equal(1, firstDiags.GetArrayLength());
        Assert.Contains("Non-exhaustive match on 'Season'", firstDiags[0].GetProperty("message").GetString());

        // Second publish after fix: 0 diagnostics
        var secondDiags = diagNotifications[1].RootElement.GetProperty("params").GetProperty("diagnostics");
        Assert.Equal(0, secondDiags.GetArrayLength());
    }

    [Fact]
    public async Task Completion_ProvidesEnumVariantsRectMethodsAndStdLibImports()
    {
        const string uri = "file:///tmp/lsp_comp_test.psps";
        const string validSource = """
            enum Season { Spring, Summer, Fall, Winter }
            state S {
                box: rect = { 0.0, 0.0, 16.0, 16.0 }
                init() {
                    var s: Season = Season.Spring
                }
            }
            """;

        // Open valid file first to populate symbol table, then simulate typing `Season.`, `box.`, and `import `
        var responses = await RunSessionAsync(
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
            new
            {
                jsonrpc = "2.0",
                method = "textDocument/didOpen",
                @params = new { textDocument = new { uri, languageId = "pspscript", version = 1, text = validSource } }
            },
            new
            {
                jsonrpc = "2.0",
                method = "textDocument/didChange",
                @params = new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text = "Season.\nbox.\nimport " } }
                }
            },
            new
            {
                jsonrpc = "2.0",
                id = 10,
                method = "textDocument/completion",
                @params = new { textDocument = new { uri }, position = new { line = 0, character = 7 } }
            },
            new
            {
                jsonrpc = "2.0",
                id = 11,
                method = "textDocument/completion",
                @params = new { textDocument = new { uri }, position = new { line = 1, character = 4 } }
            },
            new
            {
                jsonrpc = "2.0",
                id = 12,
                method = "textDocument/completion",
                @params = new { textDocument = new { uri }, position = new { line = 2, character = 7 } }
            },
            new { jsonrpc = "2.0", id = 2, method = "shutdown" },
            new { jsonrpc = "2.0", method = "exit" }
        );

        static string[] ExtractLabels(List<JsonDocument> docs, int id) =>
            docs.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == id)
                .RootElement.GetProperty("result").GetProperty("items")
                .EnumerateArray()
                .Select(static item => item.GetProperty("label").GetString()!)
                .ToArray();

        string[] enumLabels = ExtractLabels(responses, 10);
        Assert.Contains("Spring", enumLabels);
        Assert.Contains("Winter", enumLabels);

        string[] rectLabels = ExtractLabels(responses, 11);
        Assert.Contains("overlaps", rectLabels);
        Assert.Contains("contains", rectLabels);
        Assert.Contains("center", rectLabels);

        string[] importLabels = ExtractLabels(responses, 12);
        Assert.Contains("input", importLabels);
        Assert.Contains("collision", importLabels);
    }

    [Fact]
    public async Task Hover_ReturnsMarkdownSignatureForDeclaredEnum()
    {
        const string uri = "file:///tmp/lsp_hover_test.psps";
        const string source = """
            enum Season { Spring, Summer }
            state S {}
            """;

        var responses = await RunSessionAsync(
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
            new
            {
                jsonrpc = "2.0",
                method = "textDocument/didOpen",
                @params = new { textDocument = new { uri, languageId = "pspscript", version = 1, text = source } }
            },
            new
            {
                jsonrpc = "2.0",
                id = 20,
                method = "textDocument/hover",
                @params = new { textDocument = new { uri }, position = new { line = 0, character = 6 } }
            },
            new { jsonrpc = "2.0", id = 2, method = "shutdown" },
            new { jsonrpc = "2.0", method = "exit" }
        );

        var hoverResp = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 20);
        string markdown = hoverResp.RootElement.GetProperty("result").GetProperty("contents").GetProperty("value").GetString()!;
        Assert.Contains("enum Season { Spring, Summer }", markdown);
    }

    [Fact]
    public async Task MultiFileProject_SubfileResolvesSiblingStatesViaParentMainPsps()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"psps_lsp_multifile_{Guid.NewGuid():N}");
        string statesDir = Path.Combine(tempRoot, "states");
        Directory.CreateDirectory(statesDir);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "main.psps"), """
                game "Multi Test" { entry: MainMenu }
                import "states/main_menu.psps"
                import "states/match.psps"
                import "states/game_over.psps"
                """);

            File.WriteAllText(Path.Combine(statesDir, "main_menu.psps"), "state MainMenu {}");
            File.WriteAllText(Path.Combine(statesDir, "match.psps"), "state Match {}");

            string gameOverPath = Path.Combine(statesDir, "game_over.psps");
            const string validGameOver = """
                state GameOver {
                    update(dt: f32) {
                        goto Match
                        goto MainMenu
                    }
                }
                """;
            File.WriteAllText(gameOverPath, validGameOver);
            string uri = new Uri(gameOverPath).AbsoluteUri;

            var responses = await RunSessionAsync(
                new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
                new
                {
                    jsonrpc = "2.0",
                    method = "textDocument/didOpen",
                    @params = new { textDocument = new { uri, languageId = "pspscript", version = 1, text = validGameOver } }
                },
                new
                {
                    jsonrpc = "2.0",
                    method = "textDocument/didChange",
                    @params = new
                    {
                        textDocument = new { uri, version = 2 },
                        contentChanges = new[]
                        {
                            new
                            {
                                text = """
                                    state GameOver {
                                        update(dt: f32) {
                                            goto NonExistentState
                                        }
                                    }
                                    """
                            }
                        }
                    }
                },
                new { jsonrpc = "2.0", id = 2, method = "shutdown" },
                new { jsonrpc = "2.0", method = "exit" }
            );

            var diagNotifications = responses
                .Where(d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics")
                .ToList();

            Assert.Equal(2, diagNotifications.Count);

            // Opening game_over.psps with `goto Match` and `goto MainMenu` produces ZERO false-positive diagnostics
            var firstDiags = diagNotifications[0].RootElement.GetProperty("params").GetProperty("diagnostics");
            Assert.Equal(0, firstDiags.GetArrayLength());

            // Editing to a truly undeclared state immediately reports a diagnostic
            var secondDiags = diagNotifications[1].RootElement.GetProperty("params").GetProperty("diagnostics");
            Assert.Equal(1, secondDiags.GetArrayLength());
            Assert.Contains("NonExistentState", secondDiags[0].GetProperty("message").GetString());
            var range = secondDiags[0].GetProperty("range");
            Assert.Equal(2, range.GetProperty("start").GetProperty("line").GetInt32());
            Assert.True(
                range.GetProperty("end").GetProperty("character").GetInt32() >
                range.GetProperty("start").GetProperty("character").GetInt32() + 1);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MissingAssetFileOnDisk_EmitsRealTimeWarningWithExactLineSpan()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"psps_lsp_asset_warn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            string filePath = Path.Combine(tempRoot, "main.psps");
            const string source = """
                game "Asset Check" { entry: Main }
                asset missing_tex = texture("does_not_exist.png")
                state Main {}
                """;
            File.WriteAllText(filePath, source);
            string uri = new Uri(filePath).AbsoluteUri;

            var responses = await RunSessionAsync(
                new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
                new
                {
                    jsonrpc = "2.0",
                    method = "textDocument/didOpen",
                    @params = new { textDocument = new { uri, languageId = "pspscript", version = 1, text = source } }
                },
                new { jsonrpc = "2.0", id = 2, method = "shutdown" },
                new { jsonrpc = "2.0", method = "exit" }
            );

            var diagNotif = responses.Single(d =>
                d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics");
            var diags = diagNotif.RootElement.GetProperty("params").GetProperty("diagnostics");
            Assert.Equal(1, diags.GetArrayLength());

            var warn = diags[0];
            Assert.Equal(2, warn.GetProperty("severity").GetInt32()); // 2 = Warning
            Assert.Contains("does_not_exist.png", warn.GetProperty("message").GetString());
            Assert.Equal(1, warn.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GoToDefinition_ResolvesCrossFileStatesStdLibImportAndSelfFields()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"psps_lsp_def_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            string matchPath = Path.Combine(tempRoot, "match.psps");
            File.WriteAllText(matchPath, """
                // line 0 comment
                state Match {
                    score: int = 0
                }
                """);

            string mainPath = Path.Combine(tempRoot, "main.psps");
            const string mainSource = """
                import input
                import "match.psps"
                game "Def Test" { entry: Menu }
                state Menu {
                    speed: f32 = 120.0
                    update(dt: f32) {
                        var s: f32 = self.speed
                        goto Match
                    }
                }
                """;
            File.WriteAllText(mainPath, mainSource);
            string mainUri = new Uri(mainPath).AbsoluteUri;
            string matchUri = new Uri(matchPath).AbsoluteUri;

            var responses = await RunSessionAsync(
                new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
                new
                {
                    jsonrpc = "2.0",
                    method = "textDocument/didOpen",
                    @params = new { textDocument = new { uri = mainUri, languageId = "pspscript", version = 1, text = mainSource } }
                },
                // 1) Definition on `import input` (line 0, char 8)
                new
                {
                    jsonrpc = "2.0",
                    id = 30,
                    method = "textDocument/definition",
                    @params = new { textDocument = new { uri = mainUri }, position = new { line = 0, character = 8 } }
                },
                // 2) Definition on `self.speed` (line 6, char 27)
                new
                {
                    jsonrpc = "2.0",
                    id = 31,
                    method = "textDocument/definition",
                    @params = new { textDocument = new { uri = mainUri }, position = new { line = 6, character = 27 } }
                },
                // 3) Definition on `goto Match` (line 7, char 14)
                new
                {
                    jsonrpc = "2.0",
                    id = 32,
                    method = "textDocument/definition",
                    @params = new { textDocument = new { uri = mainUri }, position = new { line = 7, character = 14 } }
                },
                new { jsonrpc = "2.0", id = 2, method = "shutdown" },
                new { jsonrpc = "2.0", method = "exit" }
            );

            var importDef = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 30)
                .RootElement.GetProperty("result");
            Assert.EndsWith("StdLib/input.psps", importDef.GetProperty("uri").GetString(), StringComparison.OrdinalIgnoreCase);

            var fieldDef = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 31)
                .RootElement.GetProperty("result");
            Assert.Equal(mainUri, fieldDef.GetProperty("uri").GetString());
            Assert.Equal(4, fieldDef.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

            var stateDef = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 32)
                .RootElement.GetProperty("result");
            Assert.Equal(matchUri, stateDef.GetProperty("uri").GetString());
            Assert.Equal(1, stateDef.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SignatureHelp_AndDocumentSymbols_AndContextualCompletionsWithRecovery()
    {
        const string uri = "file:///tmp/lsp_advanced_test.psps";
        // Notice line 7 has an incomplete `self.` right after adding a new field `boost: f32 = 2.0`!
        const string incompleteSource = """
            message Hit { amount: int }
            actor Hero {
                pos: vec2 = { 0.0, 0.0 }
                boost: f32 = 2.0
                on Hit(msg) {
                }
                update(dt: f32) {
                    self.
                    draw_subsprite(tex, r, p, 
                }
            }
            """;

        var responses = await RunSessionAsync(
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
            new
            {
                jsonrpc = "2.0",
                method = "textDocument/didOpen",
                @params = new { textDocument = new { uri, languageId = "pspscript", version = 1, text = incompleteSource } }
            },
            // 1) Completion on `self.` at line 7, char 13 (tests single-line recovery!)
            new
            {
                jsonrpc = "2.0",
                id = 40,
                method = "textDocument/completion",
                @params = new { textDocument = new { uri }, position = new { line = 7, character = 13 } }
            },
            // 2) Signature help on `draw_subsprite(tex, r, p, ` at line 8, char 34 (activeParameter == 3)
            new
            {
                jsonrpc = "2.0",
                id = 41,
                method = "textDocument/signatureHelp",
                @params = new { textDocument = new { uri }, position = new { line = 8, character = 34 } }
            },
            // 3) Hover on `draw_subsprite` at line 8, char 12
            new
            {
                jsonrpc = "2.0",
                id = 42,
                method = "textDocument/hover",
                @params = new { textDocument = new { uri }, position = new { line = 8, character = 12 } }
            },
            // 4) Document symbols (Outline)
            new
            {
                jsonrpc = "2.0",
                id = 43,
                method = "textDocument/documentSymbol",
                @params = new { textDocument = new { uri } }
            },
            new { jsonrpc = "2.0", id = 2, method = "shutdown" },
            new { jsonrpc = "2.0", method = "exit" }
        );

        // 1) Verify `self.` recovered `boost` and `pos` even though line 7/8 had syntax errors
        var compItems = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 40)
            .RootElement.GetProperty("result").GetProperty("items")
            .EnumerateArray()
            .Select(static item => item.GetProperty("label").GetString()!)
            .ToArray();
        Assert.Contains("pos", compItems);
        Assert.Contains("boost", compItems);

        // 2) Verify signatureHelp for draw_subsprite has activeParameter == 3
        var sigHelp = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 41)
            .RootElement.GetProperty("result");
        Assert.Equal(3, sigHelp.GetProperty("activeParameter").GetInt32());
        Assert.Contains("draw_subsprite", sigHelp.GetProperty("signatures")[0].GetProperty("label").GetString());

        // 3) Verify hover on builtin `draw_subsprite`
        var hoverVal = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 42)
            .RootElement.GetProperty("result").GetProperty("contents").GetProperty("value").GetString();
        Assert.Contains("flip_x", hoverVal);

        // 4) Verify DocumentSymbol fallback outline contains `Hero` and `Hit`
        var docSymbols = responses.Single(d => d.RootElement.TryGetProperty("id", out var i) && i.GetInt32() == 43)
            .RootElement.GetProperty("result")
            .EnumerateArray()
            .Select(static s => s.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("Hit", docSymbols);
        Assert.Contains("Hero", docSymbols);
    }
}
