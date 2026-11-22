# PSPScript (v0.1)

A C# (.NET 10) transpiler for **PSPScript (`.psps`)**, a domain-specific game scripting language engineered for **Sony PlayStation Portable (PSP)** homebrew development. It compiles high-level, game-oriented scripts down to clean, static, zero-heap-allocation **C99** targeting **PSPSDK (`psp-gcc` + `psp-prxgen`)** and a modular STB-style **SDL2** runtime (`EBOOT.PBP` for PSP/PPSSPP, plus native Linux desktop previews).

---

## Core Design

The PlayStation Portable runs on a 32-bit Allegrex MIPS CPU (333 MHz), a hardware single-precision FPU, a `480×272` screen, 32 MB/64 MB of unified RAM, and no virtual memory. Frequent `malloc`/`free` calls lead to memory fragmentation and crashes on real hardware.

PSPScript enforces static resource budgets at compile time:

- **Static Memory Arenas**: Memory is explicitly scoped across `global var` (`.bss`), `level var` (`LevelArena`, automatically reset on `goto`), `var` (C stack), and `temp var` (`FrameArena`, wiped every frame).
- **Static Actor Pools & Generational Handles**: Actors are declared either as singletons (`actor Player`) or fixed-capacity pools (`actor Ball[4]`) and referenced via 32-bit generational handles (`ref<T>`) to prevent dangling-pointer crashes without garbage collection.
- **Typed Message Passing**: Actor communication uses `message`, `send(...)`, and `on Msg(msg)` handlers lowered to a static tagged union and fixed-capacity frame mailbox.
- **Game Primitives & Hardware Math**: First-class `vec2`, `vec3`, `rect` (16-byte aligned for VFPU), compile-time ABGR8888 `color` (`#RRGGBB` / `#RRGGBBAA`), and auto-ticking `timer` (`0.5s`, `750ms`) with single-precision `f` suffixes on all floats.

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
- **.NET 10 SDK** (to run the PSPScript compiler)
- **Podman** (`podman-compose` or `podman compose`) or **Docker** (to cross-compile `build/EBOOT.PBP` via `pspdev/pspdev:latest`)
- Optional: **`gcc` + `SDL2`** on Linux for instant native desktop previews

### Build & Run

```bash
# 1. Full pipeline: parse, type-check, emit build/out.c, and package build/EBOOT.PBP
dotnet run -- examples/pong/main.psps

# 2. Fast local desktop preview on Linux (generates build/out.c and compiles build/game_desktop)
dotnet run -- examples/pong/main.psps --c-only --desktop && ./build/game_desktop

# 3. Validate syntax, types, and static RAM budget without emitting C code
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

---

## VS Code Extension

Install the bundled `.psps` syntax highlighter, file icon, and snippets extension from [vscode-pspscript/](vscode-pspscript/README.md):

```bash
mkdir -p ~/.vscode/extensions && ln -sfn "$(pwd)/vscode-pspscript" ~/.vscode/extensions/pspscript-0.1.0
```

Reload VS Code (`Ctrl+Shift+P` → `Developer: Reload Window`) to activate highlighting for `.psps` files.

---

## Project Structure

| Path | Description |
| :--- | :--- |
| `Ast/` | Immutable C# record AST definitions (`ProgramNode`, `TopDecl`, `Stmt`, `Expr`, `TypeRef`) |
| `Parser/` | Parlot-based recursive-descent & precedence-climbing parser (`PspParser.cs`) |
| `Semantics/` | Two-pass semantic analyzer, symbol table, type checker, and static RAM budget validator |
| `CodeGen/` | Modular C99 code generator (`CSourceWriter`, `CTypeMapper`, `Actors`, `States`, `Statements`, `Expressions`) |
| `Runtime/` | Zero-heap STB-style C99 headers (`psps_runtime.h`, `psps_types.h`, `psps_arena.h`, `psps_video.h`, `psps_input.h`, `psps_save.h`) |
| `examples/` | Modular sample `.psps` game ([examples/pong/main.psps](examples/pong/main.psps)) |
| `docs/` | [Language Tutorial](docs/tutorial.md) & [EBNF Grammar](docs/grammar.md) |
| `vscode-pspscript/` | VS Code syntax highlighting extension & snippets |
| `Makefile` & `docker-compose.yml` | Podman/PSPSDK cross-compilation (`psp-gcc` → `psp-prxgen` → `EBOOT.PBP`) and desktop preview targets |

---

## Documentation

- **[Language Tutorial](docs/tutorial.md)** — Complete guide to PSPScript syntax, memory scopes, actors, messages, states, timers, and runtime functions.
- **[Grammar Reference](docs/grammar.md)** — Formal EBNF grammar specification.

---

## Roadmap

### v0.1 (Current Release)
* [x] AST (Abstract Syntax Tree) Design
* [x] Parser (using Parlot 2.3)
* [x] Two-Pass Semantic Analysis, Type Checking & Static RAM Budget Validation
* [x] Modular C99 Code Generator (Static Arenas, Actor Pools, Generational Handles & Frame Mailbox)
* [x] Modular STB-Style PSP C99 Runtime (`psps_arena`, `psps_types`, `psps_video`, `psps_input`, `psps_save`)
* [x] Tilemaps, 2D Camera (`set_camera`, `draw_subsprite`, `draw_tilemap`), Embedded 8×8 Bitmap Font (`draw_text`, `draw_number`), and Save Data Persistence (`save_game` / `load_game` via `sceUtilitySavedata`)
* [x] CLI Compiler & Podman/PSPSDK `EBOOT.PBP` Build Integration (`psp-prxgen` + `pack-pbp`)
* [x] VS Code Syntax Highlighting Extension & Snippets
* [x] Language Tutorial & Playable Pong Example (`examples/pong.psps`)

### Planned for v0.2+
* [ ] Compile-Time Image & Audio Asset Baker (PNG → swizzled `RGBA4444`/`T8`, WAV → PCM)
* [ ] PSP Audio Runtime Module (`psps_audio.h`)
* [ ] Language Server (LSP) for Real-Time VS Code Diagnostics & Autocomplete
* [ ] Automated Unit & Integration Test Suite

---

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.
