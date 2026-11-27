using PspScript.CodeGen;
using PspScript.Parsing;
using PspScript.Semantics;
using Xunit;

namespace PspScript.Tests;

public sealed class FieldInferenceAndMatchTests
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
    public void FieldTypeInference_AndTypedGameConstructors_WorkAcrossActorStateStructAndConst()
    {
        const string source = """
            const ORIGIN = vec2(240.0, 136.0)
            const BOUNDS = rect(0.0, 0.0, 480.0, 272.0)

            struct Config {
                speed = 180.0,
                retries = 3
            }

            actor Hero {
                pos = vec2(240.0, 136.0)
                dir3 = vec3(1.0, 0.0, 0.0)
                hitbox = rect(0.0, 0.0, 16.0, 16.0)
                frame = 0
                walking = false
                anim_timer = 180ms
                tint = #FFF275

                update(dt: f32) {
                    var next = vec2(pos.x + dt, pos.y)
                    var box = rect(next.x, next.y, 16.0, 16.0)
                    if box.overlaps(BOUNDS) {
                        pos = next
                    }
                }
            }

            state Main {
                score = 0
                draw() {
                    draw_rect(rect(ORIGIN.x, ORIGIN.y, 8.0, 8.0), #00E5FF)
                }
            }
            """;

        var (analysis, cSource) = Compile(source);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(cSource);

        var hero = analysis.Model!.Symbols.Actors["Hero"];
        Assert.Equal(ResolvedType.Vec2, hero.Fields["pos"].Type);
        Assert.Equal(ResolvedType.Vec3, hero.Fields["dir3"].Type);
        Assert.Equal(ResolvedType.Rect, hero.Fields["hitbox"].Type);
        Assert.Equal(ResolvedType.Int, hero.Fields["frame"].Type);
        Assert.Equal(ResolvedType.Bool, hero.Fields["walking"].Type);
        Assert.Equal(ResolvedType.Timer, hero.Fields["anim_timer"].Type);
        Assert.Equal(ResolvedType.Color, hero.Fields["tint"].Type);

        Assert.Contains("vec2_make((float)(240.0f), (float)(136.0f))", cSource);
        Assert.Contains("rect_make((float)(next.x), (float)(next.y), (float)(16.0f), (float)(16.0f))", cSource);
    }

    [Fact]
    public void FieldTypeInference_FromNull_ReportsSemanticError()
    {
        const string source = """
            state Main {
                bad = null
            }
            """;

        var (analysis, _) = Compile(source);
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("Cannot infer type of field 'bad'"));
    }

    [Fact]
    public void MultiPatternMatchStmt_SupportsEnumsIntsAndConstants()
    {
        const string source = """
            enum Season { Spring, Summer, Fall, Winter }
            const CODE_A = 1
            const CODE_B = 2

            state Main {
                season = Season.Spring
                code = 1
                bonus = 0

                update(dt: f32) {
                    match season {
                        Season.Spring | Season.Summer => bonus = 10,
                        Season.Fall | Season.Winter => bonus = 5
                    }
                    match code {
                        CODE_A | CODE_B | 3 => bonus += 1,
                        _ => bonus = 0
                    }
                }
            }
            """;

        var (analysis, cSource) = Compile(source);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(cSource);

        Assert.Contains("case Season_Spring:", cSource);
        Assert.Contains("case Season_Summer:", cSource);
        Assert.Contains("case 1:", cSource);
        Assert.Contains("case 2:", cSource);
        Assert.Contains("case 3:", cSource);
    }

    [Fact]
    public void MatchExpression_SupportsMultiPatternConstWideningAndConstFolding()
    {
        const string source = """
            enum Diff { Easy, Normal, Hard }
            const DEFAULT_DIFF = Diff.Hard
            const BASE_SPEED: f32 = match DEFAULT_DIFF {
                Diff.Easy => 120.0,
                Diff.Normal | Diff.Hard => 220.0
            }

            state Main {
                diff = Diff.Normal
                tier = 2

                update(dt: f32) {
                    var speed = match diff {
                        Diff.Easy => 100,
                        Diff.Normal | Diff.Hard => BASE_SPEED
                    }
                    var label = match tier {
                        0 | 1 => "LOW",
                        2 => "MID",
                        _ => "HIGH"
                    }
                    draw_text(label, vec2(speed * dt, 8.0))
                }
            }
            """;

        var (analysis, cSource) = Compile(source);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(cSource);

        Assert.Equal(new FloatConstValue(220.0f), analysis.Model!.Symbols.Constants["BASE_SPEED"].Value);
        Assert.Contains("((((self->diff) == Diff_Easy)) ? (100) : ((g_const_BASE_SPEED)))", cSource);
        Assert.Contains("((self->tier) == 0) || ((self->tier) == 1)", cSource);
    }

    [Fact]
    public void MatchExpression_NonExhaustiveOrIncompatibleArmTypes_ReportsErrors()
    {
        const string nonExhaustive = """
            enum ColorMode { A, B, C }
            state Main {
                m = ColorMode.A
                init() {
                    var x = match m {
                        ColorMode.A | ColorMode.B => 1
                    }
                }
            }
            """;
        var (neRes, _) = Compile(nonExhaustive);
        Assert.False(neRes.Success);
        Assert.Contains(neRes.Diagnostics, d => d.Message.Contains("Non-exhaustive match on 'ColorMode'"));

        const string incompatible = """
            state Main {
                x = 0
                init() {
                    var v = match x {
                        0 => 10,
                        _ => false
                    }
                }
            }
            """;
        var (incRes, _) = Compile(incompatible);
        Assert.False(incRes.Success);
        Assert.Contains(incRes.Diagnostics, d => d.Message.Contains("Match expression arm produces incompatible type"));
    }
}
