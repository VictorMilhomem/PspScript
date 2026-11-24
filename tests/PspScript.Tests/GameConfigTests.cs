using System.Buffers.Binary;
using System.IO.Compression;
using PspScript.Ast;
using PspScript.CodeGen;
using PspScript.Parsing;
using PspScript.Semantics;
using Xunit;

namespace PspScript.Tests;

public sealed class GameConfigTests
{
    private static ProgramNode ParseSource(string source)
    {
        bool ok = PspParser.TryParse(source, out var program, out var error);
        Assert.True(ok, $"Expected parse success, got error: {error?.Message}");
        Assert.NotNull(program);
        return program!;
    }

    // ========================================================================
    // 1. Parser Tests
    // ========================================================================

    [Fact]
    public void Parser_ParsesAllGameConfigFieldsAndValueTypes()
    {
        const string source = """
            game "StarDust PSP" {
                id: "STDS00001",
                version: "01.25",
                ram_budget: 24MB,
                extra_ram: false,
                fps: 60,
                entry: Gameplay,
                window_scale: 3,
                icon: "assets/icon.png",
                background: "assets/bg.png",
                overlay: "assets/overlay.png",
            }
            state Gameplay {}
            """;

        var program = ParseSource(source);
        var cfg = Assert.IsType<GameConfigDecl>(program.Declarations[0]);

        Assert.Equal("StarDust PSP", cfg.Title);
        Assert.Equal(new StringConfigValue("STDS00001"), cfg.Settings["id"]);
        Assert.Equal(new StringConfigValue("01.25"), cfg.Settings["version"]);
        Assert.Equal(new SizeConfigValue(24L * 1024L * 1024L), cfg.Settings["ram_budget"]);
        Assert.Equal(new BoolConfigValue(false), cfg.Settings["extra_ram"]);
        Assert.Equal(new IntConfigValue(60), cfg.Settings["fps"]);
        Assert.Equal(new IdentConfigValue("Gameplay"), cfg.Settings["entry"]);
        Assert.Equal(new IntConfigValue(3), cfg.Settings["window_scale"]);
        Assert.Equal(new StringConfigValue("assets/icon.png"), cfg.Settings["icon"]);
        Assert.Equal(new StringConfigValue("assets/bg.png"), cfg.Settings["background"]);
        Assert.Equal(new StringConfigValue("assets/overlay.png"), cfg.Settings["overlay"]);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Parser_ParsesBooleanConfigValue(string literal, bool expected)
    {
        string source = $$"""
            game "Bool Test" {
                extra_ram: {{literal}}
            }
            state Boot {}
            """;

        var program = ParseSource(source);
        var cfg = Assert.IsType<GameConfigDecl>(program.Declarations[0]);
        var boolVal = Assert.IsType<BoolConfigValue>(cfg.Settings["extra_ram"]);
        Assert.Equal(expected, boolVal.Value);
    }

    // ========================================================================
    // 2. Semantic Analysis: Defaults & Valid Configurations
    // ========================================================================

    [Fact]
    public void Semantics_AppliesDefaultsWhenOptionalKeysAreOmitted()
    {
        const string source = """
            game "Minimal Game" {
                ram_budget: 16MB
            }
            state Boot {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(analysis.Model);

        var symbols = analysis.Model!.Symbols;
        Assert.Equal("PSPS00001", symbols.GameId);
        Assert.Equal("01.00", symbols.GameVersion);
        Assert.True(symbols.ExtraRam);
        Assert.Equal(2, symbols.WindowScale);
        Assert.Null(symbols.IconPath);
        Assert.Null(symbols.BackgroundPath);
        Assert.Null(symbols.OverlayPath);
        Assert.Equal("Boot", symbols.EntryStateName);
    }

    [Fact]
    public void Semantics_PopulatesAllValidGameConfigSymbols()
    {
        const string source = """
            game "Full Metadata" {
                id: "PONG00002",
                version: "02.50",
                ram_budget: 16MB,
                extra_ram: false,
                fps: 60,
                entry: Match,
                window_scale: 4,
                icon: "icon.png",
                background: "pic1.png",
                overlay: "pic0.png"
            }
            state Match {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));

        var symbols = analysis.Model!.Symbols;
        Assert.Equal("PONG00002", symbols.GameId);
        Assert.Equal("02.50", symbols.GameVersion);
        Assert.False(symbols.ExtraRam);
        Assert.Equal(4, symbols.WindowScale);
        Assert.Equal("icon.png", symbols.IconPath);
        Assert.Equal("pic1.png", symbols.BackgroundPath);
        Assert.Equal("pic0.png", symbols.OverlayPath);
        Assert.Equal("Match", symbols.EntryStateName);
    }

    // ========================================================================
    // 3. Semantic Analysis: Strict Validation & Negative Diagnostics
    // ========================================================================

    [Fact]
    public void Semantics_RejectsUnknownConfigKeyWithHelpfulError()
    {
        const string source = """
            game "Typo Test" {
                icno: "icon.png"
            }
            state Boot {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("Unknown game config key 'icno'") && d.Message.Contains("Valid keys:"));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"TOO_LONG_123\"")]
    [InlineData("\"lowercase\"")]
    [InlineData("\"BAD-ID!\"")]
    [InlineData("12345")]
    public void Semantics_RejectsInvalidGameId(string rawId)
    {
        string source = $$"""
            game "Bad ID" {
                id: {{rawId}}
            }
            state Boot {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("'id' in game config"));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("100")]
    public void Semantics_RejectsInvalidVersion(string rawVersion)
    {
        string source = $$"""
            game "Bad Ver" {
                version: {{rawVersion}}
            }
            state Boot {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("'version' in game config"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"true\"")]
    [InlineData("yes")]
    public void Semantics_RejectsNonBooleanExtraRam(string rawVal)
    {
        string source = $$"""
            game "Bad ExtraRam" {
                extra_ram: {{rawVal}}
            }
            state Boot {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("'extra_ram' in game config must be a boolean"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("\"2\"")]
    public void Semantics_RejectsOutOfRangeWindowScale(string rawVal)
    {
        string source = $$"""
            game "Bad Scale" {
                window_scale: {{rawVal}}
            }
            state Boot {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("'window_scale' in game config must be an integer between 1 and 4"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"60\"")]
    public void Semantics_RejectsInvalidFps(string rawVal)
    {
        string source = $$"""
            game "Bad FPS" {
                fps: {{rawVal}}
            }
            state Boot {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("'fps' in game config must be a positive integer"));
    }

    [Theory]
    [InlineData("icon", "\"\"")]
    [InlineData("background", "123")]
    [InlineData("overlay", "\"   \"")]
    public void Semantics_RejectsInvalidXmbImagePath(string key, string rawVal)
    {
        string source = $$"""
            game "Bad Image" {
                {{key}}: {{rawVal}}
            }
            state Boot {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains($"'{key}' in game config must be a non-empty PNG file path string"));
    }

    [Fact]
    public void Semantics_RejectsMultipleGameConfigBlocks()
    {
        const string source = """
            game "First" { fps: 60 }
            game "Second" { fps: 30 }
            state Boot {}
            """;

        var analysis = SemanticAnalyzer.Analyze(ParseSource(source));
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("Multiple 'game' configuration blocks are not allowed"));
    }

    // ========================================================================
    // 4. C99 Code Generation & ModuleLoader SourceDirectory
    // ========================================================================

    [Fact]
    public void CodeGen_EmitsCustomSaveGameIdAndWindowScale()
    {
        const string source = """
            game "StarDust PSP" {
                id: "STDS00001",
                window_scale: 3,
                entry: Boot
            }
            state Boot {}
            """;

        var program = ParseSource(source);
        var analysis = SemanticAnalyzer.Analyze(program);
        Assert.True(analysis.Success);

        string cSource = CCodeGenerator.Generate(program, analysis.Model!);
        Assert.Contains("#define PSPS_SAVE_GAME_ID        \"STDS00001\"", cSource);
        Assert.Contains("#define PSPS_SAVE_TITLE          \"StarDust PSP\"", cSource);
        Assert.Contains("psps_video_init_scaled(\"StarDust PSP\", 3)", cSource);
    }

    [Fact]
    public void ModuleLoader_RecordsGameConfigSourceDirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"psps_cfg_loader_{Guid.NewGuid():N}");
        string subDir = Path.Combine(tempRoot, "config");
        Directory.CreateDirectory(subDir);

        try
        {
            string cfgFile = Path.Combine(subDir, "meta.psps");
            File.WriteAllText(cfgFile, """
                game "Imported Config" {
                    id: "IMPT00001",
                    icon: "icon.png"
                }
                """);

            string mainFile = Path.Combine(tempRoot, "main.psps");
            File.WriteAllText(mainFile, """
                import "config/meta.psps"
                state Boot {}
                """);

            var loadResult = ModuleLoader.Load(mainFile);
            Assert.True(loadResult.Success);

            var gameDecl = Assert.Single(loadResult.Program!.Declarations.OfType<GameConfigDecl>());
            Assert.Equal(Path.GetFullPath(subDir), gameDecl.SourceDirectory);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ========================================================================
    // 5. XMB PNG Stager Tests (PspXmbStager)
    // ========================================================================

    [Fact]
    public void XmbStager_ReturnsNullWhenUnconfigured()
    {
        using var err = new StringWriter();
        bool ok = PspXmbStager.TryStageXmbPng(null, "icon", "ICON0.PNG", 144, 80, null, null, "build", out string makeValue, err);

        Assert.True(ok);
        Assert.Equal("NULL", makeValue);
        Assert.Equal(string.Empty, err.ToString());
    }

    [Fact]
    public void XmbStager_FailsWhenFileDoesNotExist()
    {
        using var err = new StringWriter();
        bool ok = PspXmbStager.TryStageXmbPng("does_not_exist_12345.png", "icon", "ICON0.PNG", 144, 80, null, null, "build", out _, err);

        Assert.False(ok);
        Assert.Contains("not found", err.ToString());
    }

    [Fact]
    public void XmbStager_FailsWhenFileIsNotValidPng()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"psps_xmb_bad_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string fakePath = Path.Combine(tempDir, "not_a_png.png");
            File.WriteAllText(fakePath, "definitely not a PNG binary header");

            using var err = new StringWriter();
            bool ok = PspXmbStager.TryStageXmbPng("not_a_png.png", "icon", "ICON0.PNG", 144, 80, tempDir, tempDir, Path.Combine(tempDir, "build"), out _, err);

            Assert.False(ok);
            Assert.Contains("not a valid PNG image", err.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void XmbStager_CopiesExactSpecPngWithoutWarnings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"psps_xmb_ok_{Guid.NewGuid():N}");
        string buildDir = Path.Combine(tempDir, "build");
        Directory.CreateDirectory(tempDir);

        try
        {
            string iconPath = Path.Combine(tempDir, "icon.png");
            WriteMinimalPng(iconPath, 144, 80);

            using var err = new StringWriter();
            bool ok = PspXmbStager.TryStageXmbPng("icon.png", "icon", "ICON0.PNG", 144, 80, tempDir, tempDir, buildDir, out string makeValue, err);

            Assert.True(ok);
            Assert.Equal(string.Empty, err.ToString());
            Assert.True(File.Exists(Path.Combine(buildDir, "ICON0.PNG")));
            Assert.EndsWith("ICON0.PNG", makeValue);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void XmbStager_WarnsWhenPngDimensionsDifferFromXmbSpec()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"psps_xmb_warn_{Guid.NewGuid():N}");
        string buildDir = Path.Combine(tempDir, "build");
        Directory.CreateDirectory(tempDir);

        try
        {
            string iconPath = Path.Combine(tempDir, "small_icon.png");
            WriteMinimalPng(iconPath, 64, 64);

            using var err = new StringWriter();
            bool ok = PspXmbStager.TryStageXmbPng("small_icon.png", "icon", "ICON0.PNG", 144, 80, tempDir, tempDir, buildDir, out _, err);

            Assert.True(ok);
            Assert.Contains("64x64px; PSP XMB recommends 144x80px", err.ToString());
            Assert.True(File.Exists(Path.Combine(buildDir, "ICON0.PNG")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteMinimalPng(string path, int width, int height)
    {
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB

        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WriteChunk(ms, "IHDR"u8, ihdr);

        byte[] raw = new byte[height * (1 + width * 3)];
        using var idatMs = new MemoryStream();
        using (var zlib = new ZLibStream(idatMs, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        WriteChunk(ms, "IDAT"u8, idatMs.ToArray());
        WriteChunk(ms, "IEND"u8, []);

        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, data.Length);
        output.Write(lenBuf);
        output.Write(type);
        output.Write(data);
        // CRC placeholder (sufficient for header signature + IHDR dimension validation)
        output.Write([0, 0, 0, 0]);
    }
}
