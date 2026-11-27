# PSPScript (v0.2)

A C# (.NET 10) transpiler for **PSPScript (`.psps`)**, a domain-specific game scripting language engineered for **Sony PlayStation Portable (PSP)** homebrew development. It compiles high-level, game-oriented scripts down to clean, static, zero-heap-allocation **C99** targeting **PSPSDK (`psp-gcc` + `psp-prxgen`)** and a modular STB-style **SDL2** runtime (`EBOOT.PBP` for PSP/PPSSPP, plus native Linux desktop previews).

---

## Core Design

The PlayStation Portable runs on a 32-bit Allegrex MIPS CPU (333 MHz), a hardware single-precision FPU, a `480×272` screen, 32 MB/64 MB of unified RAM, and no virtual memory. Frequent `malloc`/`free` calls lead to memory fragmentation and crashes on real hardware.

PSPScript enforces static resource budgets at compile time:

- **Static Memory Arenas**: Memory is explicitly scoped across `global var` (`.bss`), `level var` (`LevelArena`, automatically reset on `goto`), `var` (C stack), and `temp var` (`FrameArena`, wiped every frame).
- **Static Actor Pools & Generational Handles**: Actors are declared either as singletons (`actor Player`) or fixed-capacity pools (`actor Ball[4]`) and referenced via 32-bit generational handles (`ref<T>`) to prevent dangling-pointer crashes without garbage collection.
- **Typed Message Passing**: Actor communication uses `message`, `send(...)`, and `on Msg(msg)` handlers lowered to a static tagged union and fixed-capacity frame mailbox.
- **Game Primitives & Compile-Time PNG Baking**: First-class `vec2`, `vec3`, `rect` (16-byte aligned for VFPU), compile-time ABGR8888 `color` (`#RRGGBB` / `#RRGGBBAA`), auto-ticking `timer` (`0.5s`, `750ms`), and a zero-dependency PNG baker (`AssetBaker.cs`) embedding real PNG sprites/spritesheets directly into static `RGBA4444` or `ABGR8888` C99 arrays.
- **Modular `import` System, Bare `StdLib` Resolution & Explicit Entry States**: Split games across multiple `.psps` files (`import "actors/ball.psps"`) or import bundled standard library modules by bare name (`import collision` / `import "collision"`, with project-local override support), complete with automatic diamond deduplication, cycle detection, and explicit `entry: MainMenu` configuration in `game { ... }`.

---

## Syntax Preview

```pspscript
game "PSP Pong" {
    ram_budget: 16MB,
    fps: 60,
    entry: Match
}

message PaddleBounce {
    dir_x: f32,
    offset_y: f32
}

level var player_score: int = 0

actor Ball[4] {
    pos: vec2 = { 236.0, 132.0 }
    vel: vec2 = { -180.0, 90.0 }
    serve_delay: timer = 0.75s

    on PaddleBounce(msg) {
        vel.x = clamp(abs(vel.x) * 1.06, 180.0, 340.0) * msg.dir_x
    }

    update(dt: f32) {
        if !serve_delay.elapsed() {
            return
        }
        pos += vel * dt
    }

    draw() {
        draw_rect({ pos.x, pos.y, 8.0, 8.0 }, #FFF275)
    }
}
```

See the full modular playable game in [examples/pong/main.psps](examples/pong/main.psps).

---

## Quick Start

### Prerequisites
- **.NET 10 SDK** (only needed to build/install from source; the installed `pspscript` binary bundles the `.NET 10` runtime)
- **Podman** (`podman-compose` or `podman`) or **Docker** (to cross-compile `build/EBOOT.PBP` via `pspdev/pspdev:latest`)
- Optional: **`gcc` + `SDL2`** on Linux for instant native desktop previews

### Install Globally (Self-Contained Binary)

Bundle the compiler with the `.NET 10` runtime, `StdLib/`, `Runtime/`, and `Makefile` into `~/.local/share/pspscript`, link `~/.local/bin/pspscript`, and install/link the bundled VS Code extension (`~/.vscode/extensions/pspscript-*`):

```bash
./install.sh

# Later, after pulling or editing the compiler, atomically update the installed binary & VS Code extension:
./update.sh            # Rebuild from current working tree & sync VS Code extension
./update.sh --pull     # git pull --ff-only, then rebuild & swap
./update.sh --test     # Run xUnit test suite before updating
# Pass --no-vscode to either script to skip the VS Code extension step
```

Once installed, you can run `pspscript` from **any** project directory without the `.NET` SDK:

```bash
# 1. Full pipeline: parse, type-check, emit build/out.c, and package build/EBOOT.PBP
pspscript main.psps

# 2. Fast local desktop preview on Linux (generates build/out.c and compiles build/game_desktop)
pspscript main.psps --c-only --desktop && ./build/game_desktop

# 3. Validate syntax, types, and static RAM budget without emitting C code
pspscript main.psps --check --dump-ast
```

### Build & Run from Source (Development)

```bash
dotnet run -- examples/pong/main.psps
dotnet run -- examples/pong/main.psps --c-only --desktop && ./build/game_desktop
dotnet run -- examples/pong/main.psps --check --dump-ast
```

### CLI Options

| Flag | Description |
| :--- | :--- |
| `<input.psps>` | Path to the PSPScript source file |
| `-o, --output <path>` | Output C99 path (default: `build/out.c`) |
| `--c-only, --no-eboot` | Emit C99 only; skip container `EBOOT.PBP` build |
| `--desktop` | Also build native Linux preview binary (`make desktop`) |
| `--check` | Run parser and semantic analyzer only |
| `--dump-ast` | Print parsed declarations and resolved semantic types |
| `--lsp` | Start the PSPScript Language Server (LSP) over stdio |

---

## VS Code Extension & Language Server (LSP)

Running `./install.sh` or `./update.sh` installs and updates the bundled `.psps` extension ([vscode-pspscript/](vscode-pspscript/README.md)) automatically. If you prefer manual setup for syntax highlighting, snippets, **real-time syntax & semantic diagnostics**, **Go to Definition**, **Signature Help**, **Document Symbols**, **context-aware autocomplete**, and **hover signatures**:

```bash
# 1. Build the PSPScript compiler (embeds the `--lsp` Language Server)
dotnet build

# 2. Install the VS Code LanguageClient dependency
cd vscode-pspscript && npm install && cd ..

# 3. Symlink the extension into VS Code
mkdir -p ~/.vscode/extensions && ln -sfn "$(pwd)/vscode-pspscript" ~/.vscode/extensions/pspscript-0.2.0
```

Reload VS Code (`Ctrl+Shift+P` → `Developer: Reload Window`) to activate highlighting and the Language Server on `.psps` files.

> **Optional (`pspscript.serverPath`):** The extension auto-detects `bin/Release/net10.0/PspScript.dll` or `bin/Debug/net10.0/PspScript.dll` from the repository. If you move or package the extension as a `.vsix`, point `pspscript.serverPath` in VS Code `settings.json` to your `PspScript.dll` (or native `PspScript` binary).

---

## Project Structure

| Path | Description |
| :--- | :--- |
| `Ast/` | Immutable C# record AST definitions (`ProgramNode`, `TopDecl`, `Stmt`, `Expr`, `TypeRef`) |
| `Parser/` | Parlot-based recursive-descent & precedence-climbing parser (`PspParser.cs`) + multi-file `ModuleLoader.cs` |
| `Semantics/` | Two-pass semantic analyzer, symbol table, type checker, entry state & static RAM budget validator |
| `CodeGen/` | Modular C99 code generator (`CSourceWriter`, `CTypeMapper`, `AssetBaker`, `TilemapBaker`, `AudioBaker`, `PspXmbStager`, `Actors`, `States`, `Statements`, `Expressions`) |
| `Lsp/` | Zero-dependency JSON-RPC 2.0 Language Server (`PspScript --lsp`) for real-time diagnostics, autocomplete & hover |
| `Runtime/` | Zero-heap STB-style C99 headers (`psps_runtime.h`, `psps_types.h`, `psps_arena.h`, `psps_video.h`, `psps_input.h`, `psps_save.h`, `psps_audio.h`) |
| `StdLib/` | Bundled zero-allocation PSPScript standard library modules imported by bare name ([input.psps](StdLib/input.psps), [collision.psps](StdLib/collision.psps)) |
| `tests/` | Automated `.NET 10` xUnit test suite (`tests/PspScript.Tests`) |
| `examples/` | Modular `.psps` projects: [Pong](examples/pong/main.psps), [Sprite Demo](examples/sprite/main.psps), and [Animation Demo](examples/anim/main.psps) |
| `docs/` | [Language Tutorial](docs/tutorial.md) & [EBNF Grammar](docs/grammar.md) |
| `vscode-pspscript/` | VS Code extension providing syntax highlighting, snippets, and the PSPScript LSP client |
| `install.sh` & `update.sh` | Self-contained single-file `linux-x64` installer & atomic updater bundling the `.NET 10` runtime, `StdLib/`, `Runtime/`, and `Makefile` |
| `Makefile` & `docker-compose.yml` | Podman/PSPSDK cross-compilation (`psp-gcc` → `psp-prxgen` → `EBOOT.PBP`) and desktop preview targets |

---

## Examples

| Example | Entry Point | Showcased Features |
| :--- | :--- | :--- |
| **PSP Pong** | [examples/pong/main.psps](examples/pong/main.psps) | Multi-file `import` + `import input`, compile-time `const` pool capacities & `match` expressions, ternary `?:`, field type inference & `vec2`/`rect` constructors, baked WAV SFX (`sfx_chip`, `sfx_click`, `sfx_hit`, `sfx_score`), `rect.overlaps` / `rect.center`, range `for i in 0..12`, `MainMenu` with message-driven `Button[MAX_BUTTONS]`, `Ball[MAX_BALLS]` pool, `PaddleBounce` messages, bitmap font HUD, `save_game` / `load_game` |
| **Sprite Demo** | [examples/sprite/main.psps](examples/sprite/main.psps) | Compile-time baked PNGs (`Free_Chicken_House.png`, `Egg_item.png`), `import input` + `dpad_vector()`, field type inference & `vec2(...)`, `match` expression tint cycling via `CycleTint` message, bobbing pooled `Egg[8]`, `temp var` normalized movement |
| **Animation Demo** | [examples/anim/main.psps](examples/anim/main.psps) | Baked spritesheets (`Basic Charakter Spritesheet.png`, `Free Chicken Sprites.png`), `import input` + `dpad_vector()`, field type inference, ternary `?:` & `rect(...)`, 4-directional `draw_subsprite` animation with `timer` frame clocks, pooled `Chicken[6]` AI + `Startle` message, 2D camera tracking |

---

## Documentation

- **[Language Tutorial](docs/tutorial.md)** — Complete guide to PSPScript syntax, memory scopes, actors, messages, states, timers, and runtime functions.
- **[Grammar Reference](docs/grammar.md)** — Formal EBNF grammar specification.

---

## Roadmap

### v0.1
* [x] AST (Abstract Syntax Tree) Design
* [x] Parser (using Parlot 2.3), Multi-File `import` Loader & Bare `StdLib` Module Resolution (`StdLib/collision.psps`)
* [x] Two-Pass Semantic Analysis, Type Checking, Explicit `entry:` State & Static RAM Budget Validation
* [x] Modular C99 Code Generator (Static Arenas, Actor Pools, Generational Handles & Frame Mailbox)
* [x] Compile-Time PNG Asset Baker (pure C# `.png` → static `RGBA4444` / `ABGR8888` C99 arrays)
* [x] Modular STB-Style PSP C99 Runtime (`psps_arena`, `psps_types`, `psps_video`, `psps_input`, `psps_save`)
* [x] Tilemaps, 2D Camera (`set_camera`, `draw_subsprite`, `draw_tilemap`), Embedded 8×8 Bitmap Font (`draw_text`, `draw_number`), and Save Data Persistence (`save_game` / `load_game` via `sceUtilitySavedata`)
* [x] CLI Compiler & Podman/PSPSDK `EBOOT.PBP` Build Integration (`psp-prxgen` + `pack-pbp`)
* [x] VS Code Syntax Highlighting Extension & Snippets
* [x] Language Tutorial & Modular Examples (`examples/pong`, `examples/sprite`, `examples/anim`)

### v0.2 (Current Release)
* [x] Bare `StdLib` Module Resolution (`import input`, `import collision`) & Project-Local Override Support
* [x] Unified `for` Loops (`for cond { ... }` & `for i in start..end { ... }`, removing `while`) & `break` Statement
* [x] Full Bitwise Operator Suite (`&`, `|`, `^`, `~`, `<<`, `>>` and compound assignments)
* [x] Built-In `rect` Geometry Helpers (`overlaps`, `contains`, read-only `center`)
* [x] Initial Standard Library Modules (`StdLib/input.psps`, `StdLib/collision.psps`) & v0.2 Examples
* [x] First-Class `enum` Declarations & Exhaustive `match` Statements
* [x] Compile-Time Baked Tiled Tilemaps (`DotTiled` `.tmx`/`.tmj`, multi-layer, collision & opt-in `mutable: true`)
* [x] Compile-Time Audio Asset Baker (WAV → PCM) & PSP Audio Runtime Module (`psps_audio.h`)
* [x] Extended `game { ... }` EBOOT/XMB Configuration (`id`, `version`, `extra_ram`, `icon`, `background`, `overlay`)
* [x] Language Server (LSP) for Real-Time VS Code Diagnostics, Autocomplete & Hover (`PspScript --lsp`)
* [x] Self-Contained Single-File Global Installer (`install.sh` bundling the `.NET 10` runtime)
* [x] Automated Unit Test Suite (`tests/PspScript.Tests`)

### v0.3 (In Progress)
* [x] Compile-Time Constants (`const NAME[: type] = expr`, usable in expressions, array/pool sizes, and `match` patterns)
* [x] Ternary Conditional Expression (`cond ? a : b`)
* [x] Loop Ergonomics (`continue` statement & inclusive ranges `0..=10`)
* [x] Field Type Inference & Typed Game Constructors (`vec2(x, y)`, `vec3(x, y, z)`, `rect(x, y, w, h)`)
* [x] Enhanced `match` (multi-pattern `|` arms, `const` patterns, and `match` expressions)
* [x] First-Class Function Types (`fn(T1, T2) -> R`) & Zero-Heap Non-Capturing Lambdas (`(x) => x * 2.0`, null-safe `void` callbacks)

---

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.
