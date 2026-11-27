using PspScript.CodeGen;
using PspScript.Parsing;
using PspScript.Semantics;
using Xunit;

namespace PspScript.Tests;

public class LambdaAndFunctionTypeTests
{
    private static (SemanticAnalysisResult Result, string? CCode) Compile(string source)
    {
        bool parsed = PspParser.TryParse(source, out var program, out var parseError);
        Assert.True(parsed, $"Parse failed: {parseError?.Message}");
        Assert.NotNull(program);

        var result = SemanticAnalyzer.Analyze(program!);
        string? c = result.Success && result.Model is not null
            ? CCodeGenerator.Generate(program!, result.Model)
            : null;
        return (result, c);
    }

    [Fact]
    public void FunctionTypes_And_VoidNullSafeCallbacks_CompileToC99()
    {
        const string source = """
            state Match {
                update(dt: f32) {}
            }

            actor Button[4] {
                on_click: fn() -> void = null
                on_hover: fn() = null

                update(dt: f32) {
                    if on_click != null {
                        on_click()
                    }
                    on_hover()
                }
            }

            state Menu {
                init() {
                    spawn Button {
                        on_click: () => goto Match,
                        on_hover: () => {
                            var x = 1
                        }
                    }
                }
            }
            """;

        var (result, c) = Compile(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.NotNull(c);
        Assert.Contains("void (*on_click)(void);", c);
        Assert.Contains("void (*on_hover)(void);", c);
        Assert.Contains("self->on_click != NULL", c);
        Assert.Contains("((self->on_click) != NULL ? (self->on_click)() : (void)0)", c);
        Assert.Contains("static void psps_lambda_0(void)", c);
        Assert.Contains("psps_request_state_transition(STATE_Match);", c);
        Assert.Contains("static void psps_lambda_1(void)", c);
    }

    [Fact]
    public void HigherOrderFunction_InfersLambdaParamTypes_And_AcceptsNamedFunction()
    {
        const string source = """
            fn apply_curve(t: f32, ease: fn(f32) -> f32) -> f32 {
                return ease(t)
            }

            fn linear(x: f32) -> f32 {
                return x
            }

            state Demo {
                val: f32 = 0.0

                init() {
                    var a = apply_curve(0.5, (t) => t * t)
                    var b = apply_curve(0.5, linear)
                    var cb: fn(f32, f32) -> f32 = (x, y) => x + y
                    val = cb(a, b)
                }
            }
            """;

        var (result, c) = Compile(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.NotNull(c);
        Assert.Contains("static float psps_fn_apply_curve(float t, float (*ease)(float))", c);
        Assert.Contains("psps_fn_apply_curve(0.5f, psps_lambda_0)", c);
        Assert.Contains("psps_fn_apply_curve(0.5f, psps_fn_linear)", c);
        Assert.Contains("static float psps_lambda_0(float t)", c);
        Assert.Contains("static float psps_lambda_1(float x, float y)", c);
    }

    [Fact]
    public void FunctionReturningFunctionPointer_And_IndirectCall_CompileCleanly()
    {
        const string source = """
            fn double_it(x: int) -> int {
                return x * 2
            }

            fn choose_op(use_double: bool) -> fn(int) -> int {
                if use_double {
                    return double_it
                }
                return (x) => x + 1
            }

            state Demo {
                res: int = 0

                init() {
                    var f = (x: int) => {
                        return x + 10
                    }
                    res = choose_op(true)(5) + f(3)
                }
            }
            """;

        var (result, c) = Compile(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.NotNull(c);
        Assert.Contains("static int32_t (*psps_fn_choose_op(bool use_double))(int32_t)", c);
        Assert.Contains("(psps_fn_choose_op(true))(5)", c);
        Assert.Contains("(f)(3)", c);
    }

    [Fact]
    public void StructAndActorRef_FunctionTypedFields_CanBeCalledViaMemberAccess()
    {
        const string source = """
            struct Config {
                transform: fn(f32) -> f32 = null
            }

            actor Runner {
                on_step: fn() -> void = null
            }

            state Demo {
                out_val: f32 = 0.0

                init() {
                    var cfg: Config
                    cfg.transform = (x) => x * 3.0
                    out_val = cfg.transform(2.0)
                    Runner.on_step = () => {}
                    Runner.on_step()
                }
            }
            """;

        var (result, c) = Compile(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.NotNull(c);
        Assert.Contains("(cfg.transform)(2.0f)", c);
        Assert.Contains("Runner_resolve_mut(Runner_singleton_ref())->on_step", c);
    }

    [Fact]
    public void CapturingLocalVariable_Or_Self_ReportsClearNonCapturingError()
    {
        const string source = """
            actor Enemy {
                hp: int = 10
                cb: fn() -> void = null

                init() {
                    var bonus: int = 5
                    cb = () => {
                        var x = bonus
                        var me = self
                        var h = hp
                    }
                }
            }
            """;

        var (result, _) = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Cannot capture local variable 'bonus'"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Cannot capture 'self' inside a lambda"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Cannot capture field of actor 'Enemy' 'hp'"));
    }

    [Fact]
    public void UntypedLambdaParam_WithoutExpectedFunctionType_ReportsError()
    {
        const string source = """
            state Demo {
                init() {
                    var f = (x) => x + 1
                }
            }
            """;

        var (result, _) = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Cannot infer type for lambda parameter 'x'"));
    }

    [Fact]
    public void MismatchedFunctionSignature_ReportsTypeError()
    {
        const string source = """
            fn takes_int(x: int) -> int {
                return x
            }

            state Demo {
                init() {
                    var cb: fn(f32) -> f32 = takes_int
                }
            }
            """;

        var (result, _) = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Cannot initialize local variable 'cb'"));
    }
}
