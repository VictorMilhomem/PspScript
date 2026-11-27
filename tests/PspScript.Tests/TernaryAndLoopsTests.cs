using PspScript.CodeGen;
using PspScript.Parsing;
using PspScript.Semantics;
using Xunit;

namespace PspScript.Tests;

public sealed class TernaryAndLoopsTests
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
    public void TernaryExpr_RuntimeChainingWideningAndConstFolding()
    {
        const string source = """
            const HARD_MODE = true
            const MAX_SPEED: f32 = HARD_MODE ? 240.0 : 160.0

            state Main {
                walking: bool = true
                tier: int = 1
                update(dt: f32) {
                    var row: int = walking ? 1 : 0
                    var speed: f32 = walking ? MAX_SPEED : 0
                    var rank: int = tier == 0 ? 10 : tier == 1 ? 20 : 30
                    var tint: color = walking ? #00E5FF : #888888
                    draw_rect({ speed, row, rank, 4.0 }, tint)
                }
            }
            """;

        var (analysis, cSource) = Compile(source);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(cSource);

        Assert.Equal(new FloatConstValue(240.0f), analysis.Model!.Symbols.Constants["MAX_SPEED"].Value);
        Assert.Contains("((self->walking) ? (1) : (0))", cSource);
        Assert.Contains("(((self->tier == 0)) ? (10) : ((((self->tier == 1)) ? (20) : (30))))", cSource);
    }

    [Fact]
    public void TernaryExpr_NonBoolConditionOrIncompatibleBranches_ReportsErrors()
    {
        const string badCond = """
            state Main {
                init() {
                    var x: int = 1 ? 2 : 3
                }
            }
            """;
        var (badCondRes, _) = Compile(badCond);
        Assert.False(badCondRes.Success);
        Assert.Contains(badCondRes.Diagnostics, d => d.Message.Contains("Ternary condition must evaluate to 'bool'"));

        const string badBranches = """
            state Main {
                init() {
                    var x = true ? 1 : false
                }
            }
            """;
        var (badBranchRes, _) = Compile(badBranches);
        Assert.False(badBranchRes.Success);
        Assert.Contains(badBranchRes.Diagnostics, d => d.Message.Contains("Ternary conditional branches have incompatible types"));
    }

    [Fact]
    public void ContinueStmt_ValidInsideForRangeAndEachLoops()
    {
        const string source = """
            actor Enemy[4] {
                active: bool = true
            }

            state Main {
                init() {
                    var sum: int = 0
                    for i in 0..=10 {
                        if i % 2 == 0 {
                            continue
                        }
                        sum += i
                    }

                    var n: int = 0
                    for n < 5 {
                        n += 1
                        if n == 3 {
                            continue
                        }
                    }

                    each e in Enemy {
                        if !e.active {
                            continue
                        }
                        destroy e
                    }
                }
            }
            """;

        var (analysis, cSource) = Compile(source);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(cSource);

        Assert.Contains("i <= (int32_t)(10)", cSource);
        Assert.Contains("continue;", cSource);
    }

    [Fact]
    public void ContinueStmt_OutsideLoop_ReportsSemanticError()
    {
        const string source = """
            state Main {
                init() {
                    continue
                }
            }
            """;

        var (analysis, _) = Compile(source);
        Assert.False(analysis.Success);
        Assert.Contains(analysis.Diagnostics, d => d.Message.Contains("'continue' statement can only be used inside a 'for' or 'each' loop"));
    }

    [Fact]
    public void InclusiveAndExclusiveRangeLoops_EmitExpectedComparisons()
    {
        const string source = """
            state Main {
                init() {
                    var limit: int = 5
                    for a in 0..5 {
                        draw_number(a, { 0.0, 0.0 })
                    }
                    for b in 0..=limit + 1 {
                        draw_number(b, { 0.0, 8.0 })
                    }
                }
            }
            """;

        var (analysis, cSource) = Compile(source);
        Assert.True(analysis.Success, string.Join("\n", analysis.Diagnostics));
        Assert.NotNull(cSource);

        Assert.Contains("a < (int32_t)(5)", cSource);
        Assert.Contains("b <= _range_end_", cSource);
    }
}
