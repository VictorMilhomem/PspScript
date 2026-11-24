using PspScript.CodeGen;
using PspScript.Parsing;
using PspScript.Semantics;
using Xunit;

namespace PspScript.Tests;

public class TilemapAndBuiltinsTests : IDisposable
{
    private readonly string _tempDir;

    public TilemapAndBuiltinsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"psps_tilemap_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static (SemanticAnalysisResult Analysis, string? CSource) CompileSnippet(string source, string? baseDir = null)
    {
        bool parsed = PspParser.TryParse(source, out var program, out var parseError);
        Assert.True(parsed, $"Parse failed: {parseError?.Message}");
        Assert.NotNull(program);

        var analysis = SemanticAnalyzer.Analyze(program!);
        if (!analysis.Success || analysis.Model is null)
            return (analysis, null);

        string cSource = CCodeGenerator.Generate(program!, analysis.Model, baseDir);
        return (analysis, cSource);
    }

    private string WriteMinimalPng(string fileName)
    {
        // Valid 1x1 white RGBA PNG (67 bytes)
        byte[] pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");
        string fullPath = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(fullPath, pngBytes);
        return fullPath;
    }

    private void WriteExternalTsx(string fileName, string imageName, int tileCount, int? solidLocalTileId = null)
    {
        string tileBlock = solidLocalTileId.HasValue
            ? $"""
               <tile id="{solidLocalTileId.Value}">
                <properties>
                 <property name="solid" type="bool" value="true"/>
                </properties>
               </tile>
               """
            : string.Empty;

        string tsx = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <tileset version="1.10" tiledversion="1.11.0" name="{Path.GetFileNameWithoutExtension(fileName)}" tilewidth="16" tileheight="16" tilecount="{tileCount}" columns="2">
             <image source="{imageName}" width="32" height="32"/>
             {tileBlock}
            </tileset>
            """;

        File.WriteAllText(Path.Combine(_tempDir, fileName), tsx);
    }

    [Fact]
    public void SingleLayerCollisionTilemap_RendersAndPopulatesCollisionByDefault()
    {
        WriteMinimalPng("terrain.png");
        WriteExternalTsx("terrain.tsx", "terrain.png", tileCount: 4);

        string tmx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" orientation="orthogonal" renderorder="right-down" width="2" height="2" tilewidth="16" tileheight="16" infinite="0" nextlayerid="3" nextobjectid="1">
             <tileset firstgid="1" source="terrain.tsx"/>
             <layer id="1" name="Collision" width="2" height="2">
              <data encoding="csv">
            1,0,
            0,2
            </data>
             </layer>
            </map>
            """;
        File.WriteAllText(Path.Combine(_tempDir, "single.tmx"), tmx);

        string src = """
            asset lvl = tilemap("single.tmx")
            state Main {
                draw() { lvl.draw() }
            }
            """;

        var (analysis, _) = CompileSnippet(src, _tempDir);
        Assert.True(analysis.Success);

        var baked = TilemapBaker.BakeTilemap(analysis.Model!.Symbols.Assets["lvl"], _tempDir);
        Assert.Equal((ushort)1, baked.LayerCount);
        Assert.Equal(new ushort[] { 1, 0, 0, 2 }, baked.Layers);
        Assert.Equal(new byte[] { 1, 0, 0, 1 }, baked.Collision);
    }

    [Fact]
    public void SingleLayerCollisionTilemap_DrawCollisionFalse_HidesVisualTiles()
    {
        WriteMinimalPng("terrain.png");
        WriteExternalTsx("terrain.tsx", "terrain.png", tileCount: 4);

        string tmx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" orientation="orthogonal" renderorder="right-down" width="2" height="2" tilewidth="16" tileheight="16" infinite="0" nextlayerid="3" nextobjectid="1">
             <tileset firstgid="1" source="terrain.tsx"/>
             <layer id="1" name="Collision" width="2" height="2">
              <data encoding="csv">
            1,0,
            0,2
            </data>
             </layer>
            </map>
            """;
        File.WriteAllText(Path.Combine(_tempDir, "single_hide.tmx"), tmx);

        string src = """
            asset lvl = tilemap("single_hide.tmx", draw_collision: false)
            state Main {
                draw() { lvl.draw() }
            }
            """;

        var (analysis, _) = CompileSnippet(src, _tempDir);
        Assert.True(analysis.Success);

        var baked = TilemapBaker.BakeTilemap(analysis.Model!.Symbols.Assets["lvl"], _tempDir);
        Assert.Equal(new ushort[] { 0, 0, 0, 0 }, baked.Layers);
        Assert.Equal(new byte[] { 1, 0, 0, 1 }, baked.Collision);
    }

    [Fact]
    public void MultiLayerTilemap_DrawCollisionOptionControlsVisibility()
    {
        WriteMinimalPng("terrain.png");
        WriteExternalTsx("terrain.tsx", "terrain.png", tileCount: 4);

        string tmx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" orientation="orthogonal" renderorder="right-down" width="2" height="1" tilewidth="16" tileheight="16" infinite="0" nextlayerid="3" nextobjectid="1">
             <tileset firstgid="1" source="terrain.tsx"/>
             <layer id="1" name="Background" width="2" height="1">
              <data encoding="csv">1,1</data>
             </layer>
             <layer id="2" name="Terrain" width="2" height="1">
              <data encoding="csv">0,2</data>
             </layer>
            </map>
            """;
        File.WriteAllText(Path.Combine(_tempDir, "multi_layer.tmx"), tmx);

        string hiddenSrc = """
            asset lvl = tilemap("multi_layer.tmx", collision_layer: "Terrain")
            state Main { draw() { lvl.draw() } }
            """;
        var (hiddenAnalysis, _) = CompileSnippet(hiddenSrc, _tempDir);
        Assert.True(hiddenAnalysis.Success);
        var hiddenBaked = TilemapBaker.BakeTilemap(hiddenAnalysis.Model!.Symbols.Assets["lvl"], _tempDir);
        Assert.Equal((ushort)1, hiddenBaked.LayerCount);
        Assert.Equal(new byte[] { 0, 1 }, hiddenBaked.Collision);

        string visibleSrc = """
            asset lvl = tilemap("multi_layer.tmx", collision_layer: "Terrain", draw_collision: true)
            state Main { draw() { lvl.draw() } }
            """;
        var (visibleAnalysis, _) = CompileSnippet(visibleSrc, _tempDir);
        Assert.True(visibleAnalysis.Success);
        var visibleBaked = TilemapBaker.BakeTilemap(visibleAnalysis.Model!.Symbols.Assets["lvl"], _tempDir);
        Assert.Equal((ushort)2, visibleBaked.LayerCount);
        Assert.Equal(new ushort[] { 1, 1, 0, 2 }, visibleBaked.Layers);
        Assert.Equal(new byte[] { 0, 1 }, visibleBaked.Collision);
    }

    [Fact]
    public void MultiTilesetTmx_BakesAllTilesetsAndResolvesSolidByGlobalGid()
    {
        WriteMinimalPng("ts1.png");
        WriteMinimalPng("ts2.png");
        WriteExternalTsx("ts1.tsx", "ts1.png", tileCount: 4, solidLocalTileId: null);
        WriteExternalTsx("ts2.tsx", "ts2.png", tileCount: 4, solidLocalTileId: 1); // global GID = 5 + 1 = 6

        string tmx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" orientation="orthogonal" renderorder="right-down" width="2" height="2" tilewidth="16" tileheight="16" infinite="0" nextlayerid="3" nextobjectid="1">
             <tileset firstgid="1" source="ts1.tsx"/>
             <tileset firstgid="5" source="ts2.tsx"/>
             <layer id="1" name="Ground" width="2" height="2">
              <data encoding="csv">
            2,6,
            5,0
            </data>
             </layer>
            </map>
            """;
        File.WriteAllText(Path.Combine(_tempDir, "multi_ts.tmx"), tmx);

        string src = """
            asset stage = tilemap("multi_ts.tmx")
            state Main {
                draw() { stage.draw() }
            }
            """;

        var (analysis, cSource) = CompileSnippet(src, _tempDir);
        Assert.True(analysis.Success);
        Assert.NotNull(cSource);

        var baked = TilemapBaker.BakeTilemap(analysis.Model!.Symbols.Assets["stage"], _tempDir);
        Assert.Equal(2, baked.Tilesets.Count);
        Assert.Equal((ushort)1, baked.Tilesets[0].FirstGid);
        Assert.Equal((ushort)5, baked.Tilesets[1].FirstGid);
        // GID 2 (ts1 local 1) is NOT solid; GID 6 (ts2 local 1) IS solid; GID 5 (ts2 local 0) is NOT solid
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, baked.Collision);
        Assert.Equal(new ushort[] { 2, 6, 5, 0 }, baked.Layers);

        Assert.Contains("static const psps_tilemap_tileset_t g_asset_stage_tilesets[2]", cSource);
        Assert.Contains(".first_gid = 1u", cSource);
        Assert.Contains(".first_gid = 5u", cSource);
        Assert.Contains(".tileset_count = 2u", cSource);
    }

    [Fact]
    public void DrawCollisionOption_NonBoolLiteral_ReportsSemanticError()
    {
        string src = """
            asset lvl = tilemap("map.tmx", draw_collision: 1)
            state Main { draw() {} }
            """;

        var (analysis, _) = CompileSnippet(src);
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("draw_collision", StringComparison.Ordinal));
    }

    [Fact]
    public void DrawSubsprite_FlipOverloads_TypeCheckAndEmitExCalls()
    {
        string src = """
            asset sheet = texture("sheet.png")
            state Main {
                facing_left: bool = true
                draw() {
                    var r: rect = { 0.0, 0.0, 16.0, 16.0 }
                    var p: vec2 = { 10.0, 20.0 }
                    draw_subsprite(sheet, r, p)
                    draw_subsprite(sheet, r, p, #FF0000)
                    draw_subsprite(sheet, r, p, facing_left)
                    draw_subsprite(sheet, r, p, facing_left, true)
                    draw_subsprite(sheet, r, p, #FF0000, facing_left)
                    draw_subsprite(sheet, r, p, #FF0000, facing_left, true)
                }
            }
            """;

        var (analysis, cSource) = CompileSnippet(src, _tempDir);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(cSource);

        Assert.Contains("psps_draw_subsprite(g_asset_sheet, r, p, PSPS_COLOR_WHITE)", cSource);
        Assert.Contains("psps_draw_subsprite(g_asset_sheet, r, p, ((psps_color_t)0xFF0000FFu))", cSource);
        Assert.Contains("psps_draw_subsprite_ex(g_asset_sheet, r, p, PSPS_COLOR_WHITE, self->facing_left, false)", cSource);
        Assert.Contains("psps_draw_subsprite_ex(g_asset_sheet, r, p, PSPS_COLOR_WHITE, self->facing_left, true)", cSource);
        Assert.Contains("psps_draw_subsprite_ex(g_asset_sheet, r, p, ((psps_color_t)0xFF0000FFu), self->facing_left, false)", cSource);
        Assert.Contains("psps_draw_subsprite_ex(g_asset_sheet, r, p, ((psps_color_t)0xFF0000FFu), self->facing_left, true)", cSource);
    }

    [Fact]
    public void DrawSubsprite_InvalidFourthArg_ReportsSemanticError()
    {
        string src = """
            asset sheet = texture("sheet.png")
            state Main {
                draw() {
                    draw_subsprite(sheet, { 0.0, 0.0, 16.0, 16.0 }, { 0.0, 0.0 }, 123)
                }
            }
            """;

        var (analysis, _) = CompileSnippet(src);
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("draw_subsprite", StringComparison.Ordinal));
    }

    [Fact]
    public void FloorBuiltin_ReturnsIntAndWidensToF32()
    {
        string src = """
            asset lvl = tilemap("missing.tmx")
            state Main {
                x: f32 = 35.9
                update(dt: f32) {
                    var col: int = floor(x / 16.0)
                    var snapped: f32 = floor(x / 16.0) * 16.0
                    if lvl.is_solid(col, 0) {
                        x = snapped
                    }
                }
            }
            """;

        var (analysis, cSource) = CompileSnippet(src, _tempDir);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(cSource);
        Assert.Contains("((int32_t)floorf((float)(", cSource);
    }
}
