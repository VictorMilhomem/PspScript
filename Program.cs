using System.Diagnostics;
using PspScript.Ast;
using PspScript.CodeGen;
using PspScript.Parsing;
using PspScript.Semantics;

if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

string? inputPath = null;
string outputPath = Path.Combine("build", "out.c");
bool checkOnly = false;
bool dumpAst = false;
bool buildEboot = true;
bool buildDesktop = false;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    switch (arg)
    {
        case "-o" or "--output":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("Error: Missing output file path after '-o/--output'.");
                return 1;
            }
            outputPath = args[++i];
            break;

        case "--check":
            checkOnly = true;
            buildEboot = false;
            break;

        case "--c-only" or "--no-eboot":
            buildEboot = false;
            break;

        case "--desktop":
            buildDesktop = true;
            break;

        case "--dump-ast":
            dumpAst = true;
            break;

        default:
            if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"Error: Unknown option '{arg}'. Run with --help for usage.");
                return 1;
            }

            if (inputPath is null)
            {
                inputPath = arg;
            }
            else
            {
                Console.Error.WriteLine($"Error: Unexpected extra argument '{arg}'.");
                return 1;
            }
            break;
    }
}

if (string.IsNullOrWhiteSpace(inputPath))
{
    Console.Error.WriteLine("Error: No input '.psps' file specified.");
    PrintUsage();
    return 1;
}

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Error: Input file not found: '{inputPath}'");
    return 1;
}

if (!inputPath.EndsWith(".psps", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Warning: Input file '{inputPath}' does not use the '.psps' extension.");
}

// ============================================================================
// Parsing & Module Resolution (import "...")
// ============================================================================
Console.WriteLine($"[1/4] Parsing & resolving modules from '{inputPath}'...");

var loadResult = ModuleLoader.Load(inputPath);
if (!loadResult.Success || loadResult.Program is null)
{
    foreach (var err in loadResult.Errors)
    {
        PrintModuleLoadError(err);
    }
    return 1;
}

var program = loadResult.Program;
Console.WriteLine($"      Loaded {loadResult.LoadedFiles.Count} module(s), {program.Declarations.Count} top-level declaration(s).");

// ============================================================================
// Semantic Analysis & Type Checking
// ============================================================================
Console.WriteLine("[2/4] Running Semantic Analysis & Type Checking...");

var analysis = SemanticAnalyzer.Analyze(program);

if (!analysis.Success || analysis.Model is null)
{
    Console.Error.WriteLine($"Semantic analysis failed with {analysis.Diagnostics.Count} diagnostic(s):");
    foreach (var diag in analysis.Diagnostics)
    {
        Console.Error.WriteLine($"  {inputPath}: {diag}");
    }
    return 1;
}

var model = analysis.Model;
Console.WriteLine($"      Resolved {model.NodeTypes.Count} AST node types.");
Console.WriteLine($"      Static Memory Footprint: {model.EstimatedStaticMemoryBytes:N0} bytes" +
                  (model.ConfiguredRamBudgetBytes is long budget ? $" / {budget:N0} bytes budget" : string.Empty));

if (dumpAst)
{
    Console.WriteLine();
    PrintAstSummary(program, model);
    Console.WriteLine();
}

if (checkOnly)
{
    Console.WriteLine("Check completed successfully (--check).");
    return 0;
}

// ============================================================================
// C99 Code Generation
// ============================================================================
Console.WriteLine($"[3/4] Generating C99 code -> '{outputPath}'...");

string cSource = CCodeGenerator.Generate(program, model);
string? outDir = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrEmpty(outDir))
{
    Directory.CreateDirectory(outDir);
}
File.WriteAllText(outputPath, cSource);

Console.WriteLine($"      Emitted {cSource.Length:N0} bytes of static C99 to '{outputPath}'.");

if (buildDesktop)
{
    Console.WriteLine("[Desktop] Building native Linux preview binary (make desktop)...");
    int desktopExit = RunProcess("make", "desktop");
    if (desktopExit != 0)
    {
        Console.Error.WriteLine($"Error: Desktop build failed with exit code {desktopExit}.");
        return desktopExit;
    }
}

// ============================================================================
// EBOOT.PBP Cross-Compilation via Podman / Docker Compose
// ============================================================================
if (!buildEboot)
{
    Console.WriteLine("[4/4] Skipping EBOOT.PBP container build (--c-only).");
    return 0;
}

string gameTitle = model.Symbols.GameConfig?.Title ?? Path.GetFileNameWithoutExtension(inputPath);
Console.WriteLine($"[4/4] Building PlayStation Portable EBOOT.PBP (Title: \"{gameTitle}\") via docker-compose.yml...");

int ebootExit = RunComposeBuild(gameTitle);
if (ebootExit != 0)
{
    Console.Error.WriteLine($"Error: EBOOT.PBP container build failed with exit code {ebootExit}.");
    return ebootExit;
}

string ebootPath = Path.Combine(string.IsNullOrEmpty(outDir) ? "build" : outDir, "EBOOT.PBP");
if (File.Exists(ebootPath))
{
    var info = new FileInfo(ebootPath);
    Console.WriteLine($"      Successfully built '{ebootPath}' ({info.Length:N0} bytes).");
}

return 0;

// ============================================================================
// CLI Helper Functions
// ============================================================================

static void PrintUsage()
{
    Console.WriteLine("""
    PSPScript Compiler - Transpiles PSPScript (.psps) to Static C99 & PSP EBOOT.PBP

    Usage:
      dotnet run -- <input.psps> [options]

    Arguments:
      <input.psps>              Path to the PSPScript source file (.psps)

    Options:
      -o, --output <path>       Output C99 file path (default: build/out.c)
      --c-only, --no-eboot      Emit C99 source only; skip container EBOOT.PBP build
      --desktop                 Also build native desktop preview executable (make desktop)
      --check                   Run syntax and semantic analysis only (no code generation)
      --dump-ast                Print parsed declarations and resolved semantic types
      -h, --help                Display this help message

    Examples:
      dotnet run -- examples/pong/main.psps
      dotnet run -- examples/pong/main.psps --c-only --desktop
      dotnet run -- examples/pong/main.psps --check --dump-ast
    """);
}

static void PrintModuleLoadError(ModuleLoadError error)
{
    if (error.Line > 0)
    {
        Console.Error.WriteLine($"{error.FilePath}({error.Line}:{error.Column}): error: {error.Message}");
        if (!string.IsNullOrEmpty(error.SourceText))
        {
            string[] lines = error.SourceText.Replace("\r\n", "\n").Split('\n');
            if (error.Line <= lines.Length)
            {
                string sourceLine = lines[error.Line - 1];
                Console.Error.WriteLine($"  {error.Line} | {sourceLine}");
                string gutterPad = new(' ', error.Line.ToString().Length);
                int caretOffset = Math.Max(0, error.Column - 1);
                Console.Error.WriteLine($"  {gutterPad} | {new string(' ', caretOffset)}^");
            }
        }
    }
    else
    {
        Console.Error.WriteLine($"{error.FilePath}: error: {error.Message}");
    }
}

static void PrintAstSummary(ProgramNode program, SemanticModel model)
{
    foreach (var decl in program.Declarations)
    {
        switch (decl)
        {
            case GameConfigDecl game:
                Console.WriteLine($"  [GameConfig] \"{game.Title}\" ({game.Settings.Count} setting(s))");
                break;

            case AssetDecl asset:
                Console.WriteLine($"  [Asset] {asset.Name} : {model.GetTypeOrDefault(asset)} (\"{asset.FilePath}\")");
                break;

            case MessageDecl msg:
                Console.WriteLine($"  [Message] {msg.Name} ({msg.Fields.Count} field(s))");
                break;

            case StructDecl st:
                Console.WriteLine($"  [Struct] {st.Name} ({st.Fields.Count} field(s))");
                break;

            case GlobalVarDecl gvar:
                Console.WriteLine($"  [GlobalVar] ({gvar.Scope}) {gvar.Name} : {model.GetTypeOrDefault(gvar)}");
                break;

            case ActorDecl actor:
                string poolInfo = actor.PoolCapacity.HasValue ? $"[pool: {actor.PoolCapacity.Value}]" : "[singleton]";
                Console.WriteLine($"  [Actor] {actor.Name} {poolInfo} (Fields: {actor.Fields.Count}, Hooks: {actor.Hooks.Count}, Handlers: {actor.Handlers.Count}, Methods: {actor.Methods.Count})");
                break;

            case StateDecl state:
                Console.WriteLine($"  [State] {state.Name} (Fields: {state.Fields.Count}, Hooks: {state.Hooks.Count}, Handlers: {state.Handlers.Count}, Methods: {state.Methods.Count})");
                break;

            case FuncDecl fn:
                Console.WriteLine($"  [Function] {fn.Name} : {model.GetTypeOrDefault(fn)}");
                break;
        }
    }
}

static int RunComposeBuild(string gameTitle)
{
    var env = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PSP_EBOOT_TITLE"] = gameTitle
    };

    if (IsCommandAvailable("podman-compose"))
    {
        return RunProcess("podman-compose", "run --rm psp-builder", env);
    }

    if (IsCommandAvailable("podman"))
    {
        return RunProcess("podman", "compose run --rm psp-builder", env);
    }

    if (IsCommandAvailable("docker"))
    {
        return RunProcess("docker", "compose run --rm psp-builder", env);
    }

    Console.Error.WriteLine("Error: Neither 'podman-compose', 'podman', nor 'docker' was found in PATH.");
    return 1;
}

static bool IsCommandAvailable(string binaryName)
{
    string? pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(pathEnv))
        return false;

    foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        string fullPath = Path.Combine(dir, binaryName);
        if (File.Exists(fullPath))
            return true;
    }

    return false;
}

static int RunProcess(string fileName, string arguments, IDictionary<string, string>? extraEnv = null)
{
    var psi = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        WorkingDirectory = Directory.GetCurrentDirectory(),
        UseShellExecute = false
    };

    if (extraEnv is not null)
    {
        foreach (var (key, value) in extraEnv)
        {
            psi.Environment[key] = value;
        }
    }

    using var proc = Process.Start(psi);
    if (proc is null)
        return 1;

    proc.WaitForExit();
    return proc.ExitCode;
}
