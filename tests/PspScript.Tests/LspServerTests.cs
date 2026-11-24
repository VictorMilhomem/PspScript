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
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
