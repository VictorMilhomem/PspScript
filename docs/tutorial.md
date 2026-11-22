# PSPScript Language Tutorial

Welcome to **PSPScript** — a domain-specific game scripting language designed specifically for **Sony PlayStation Portable (PSP)** homebrew development. PSPScript compiles high-level, game-oriented `.psps` scripts into clean, static, zero-heap-allocation **C99** targeting `psp-gcc` (PSPSDK + SDL2) and desktop Linux/Windows for fast local testing.

---

## Table of Contents

1. [Why PSPScript? (The PSP Hardware Model)](#1-why-pspscript-the-psp-hardware-model)
2. [Quick Start & CLI Workflow](#2-quick-start--cli-workflow)
3. [Game Configuration & Static Memory Budget](#3-game-configuration--static-memory-budget)
4. [Type System & Game Primitives](#4-type-system--game-primitives)
5. [Variables & Static Memory Scopes](#5-variables--static-memory-scopes)
6. [Structs & Compile-Time Assets](#6-structs--compile-time-assets)
7. [Actors: Singletons & Static Pools](#7-actors-singletons--static-pools)
8. [Typed Message Passing (`message`, `on`, `send`)](#8-typed-message-passing-message-on-send)
9. [Game States & Transitions (`state`, `goto`)](#9-game-states--transitions-state-goto)
10. [Control Flow & Functions](#10-control-flow--functions)
11. [Built-In PSP Runtime Functions & Input](#11-built-in-psp-runtime-functions--input)
12. [The Deterministic 4-Phase Frame Loop](#12-the-deterministic-4-phase-frame-loop)
13. [Complete Playable Example](#13-complete-playable-example)

---

## 1. Why PSPScript? (The PSP Hardware Model)

The PlayStation Portable runs on a 32-bit **Allegrex MIPS32** CPU (up to 333 MHz) with a **480×272** screen, a hardware **single-precision FPU** (`float`), **32 MB / 64 MB of unified RAM**, and **no virtual memory**.

On hardware without virtual memory, frequent `malloc`/`free` calls fragment RAM and cause crashes. PSPScript prevents this by design:

- **Zero dynamic heap allocations**: Every actor pool, mailbox, and arena lives in fixed-size static `.bss` memory.
- **Safe generational handles (`ref<T>`)**: Actors are referenced through 32-bit `(index, generation)` handles instead of raw pointers, preventing dangling-pointer crashes when an actor is destroyed.
- **First-class game primitives**: `vec2`, `vec3`, `rect`, `color`, and `timer` are built directly into the language and compiler.
- **Hardware single-precision math**: Floating-point literals automatically emit C `f` suffixes (`0.5f`) so the Allegrex FPU executes them in hardware rather than slow software `double` emulation.

---

## 2. Quick Start & CLI Workflow

Create a file with the `.psps` extension (for example, `examples/pong.psps`), then run the PSPScript compiler:

```bash
# 1. Full build: parse, type-check, emit build/out.c, and package build/EBOOT.PBP via Podman/Docker
dotnet run -- examples/pong.psps

# 2. Fast desktop preview on Linux (generates build/out.c and compiles build/game_desktop with SDL2)
dotnet run -- examples/pong.psps --c-only --desktop && ./build/game_desktop

# 3. Validate syntax, types, and static RAM budget without emitting C code
dotnet run -- examples/pong.psps --check --dump-ast
```

> **Tip (VS Code Syntax Highlighting):** Install the bundled extension from `vscode-pspscript/` for `.psps` syntax highlighting, file icons, and code snippets:
> ```bash
> mkdir -p ~/.vscode/extensions && ln -sfn "$(pwd)/vscode-pspscript" ~/.vscode/extensions/pspscript-0.1.0
> ```

---

## 3. Game Configuration & Static Memory Budget

Every `.psps` program can declare a `game` block to configure the PSP `EBOOT.PBP` title, target frame rate, and static RAM budget:

```pspscript
game "StarDust PSP" {
    ram_budget: 16MB,
    fps: 60
}
```

- **Title**: Stamped automatically into `PARAM.SFO` when building `EBOOT.PBP` and used as the desktop window title.
- **`ram_budget`**: Accepts size literals (`KB` or `MB`, e.g., `512KB`, `16MB`). During semantic analysis, the compiler sums the static byte size of all globals, state fields, fixed arrays, and actor pools and fails compilation if the budget is exceeded.

---

## 4. Type System & Game Primitives

### Scalar Primitives

| Type | C99 Mapping | Size | Description |
| :--- | :--- | :--- | :--- |
| `int` | `int32_t` | 4 B | Signed 32-bit integer |
| `u8` | `uint8_t` | 1 B | Unsigned 8-bit integer |
| `u16` | `uint16_t` | 2 B | Unsigned 16-bit integer |
| `u32` | `uint32_t` | 4 B | Unsigned 32-bit integer |
| `f32` | `float` | 4 B | 32-bit single-precision float (PSP hardware FPU) |
| `bool` | `bool` | 1 B | `true` or `false` |
| `void` | `void` | 0 B | No return value |

### Vectors & Rectangles (`vec2`, `vec3`, `rect`)

Brace literals `{ ... }` infer their type from the number of components and are 16-byte aligned in C99 for PSP VFPU compatibility:

```pspscript
var pos: vec2 = { 240.0, 136.0 }         // 2 components -> vec2 (.x, .y)
var dir: vec3 = { 0.0, 1.0, 0.0 }        // 3 components -> vec3 (.x, .y, .z)
var box: rect = { 20.0, 40.0, 10.0, 56.0 } // 4 components -> rect (.x, .y, .w, .h)

// Vector arithmetic & scalar scaling
var vel: vec2 = { 120.0, -40.0 }
pos += vel * dt
var offset: vec2 = pos - { 240.0, 136.0 }

// Built-in vector methods
var dist: f32 = offset.length()
var unit: vec2 = offset.normalized()
var alignment: f32 = unit.dot({ 1.0, 0.0 })
```

### Native PSP Colors (`color`)

Hex color literals (`#RRGGBB` or `#RRGGBBAA`) are packed at compile time into native PSP **ABGR8888** (`0xAABBGGRR`) `uint32_t` values—zero runtime byte-swapping required:

```pspscript
var cyan: color = #00E5FF        // Alpha defaults to 0xFF -> 0xFFFFE500u
var ghost: color = #FFFFFF80     // 50% translucent white  -> 0x80FFFFFFu

var red_channel: u8 = cyan.r     // Unpack channels with .r, .g, .b, .a
```

### First-Class Timers (`timer`)

Timers replace repetitive manual countdown boilerplate. Any `timer` field declared on an `actor` or `state` is **automatically ticked every frame** before `update(dt)` runs:

```pspscript
actor Turret {
    cooldown: timer = 0.5s       // Accepts 's' (seconds) or 'ms' (milliseconds)

    update(dt: f32) {
        if cooldown.elapsed() {
            // Fire projectile...
            cooldown = 750ms     // Restart timer to 0.75 seconds
        }
    }
}
```

Timers also expose `.remaining`, `.duration`, `.elapsed()`, `.ready()`, and `.reset()`.

### Fixed-Capacity Static Arrays (`[N]T`)

Dynamic arrays do not exist in PSPScript. All arrays have a compile-time constant length:

```pspscript
var scores: [8]int
var targets: [4]ref<Enemy>
var count: int = targets.len     // Evaluates to constant 4
```

### Generational Actor Handles (`ref<ActorName>`)

References to actors use `ref<ActorName>` (and can be set to `null`). Even if the referenced actor is destroyed and its pool slot is reused by a new actor, the generation counter mismatches and stale accesses fail safely instead of corrupting memory.

```pspscript
var locked_target: ref<Enemy> = null
if locked_target != null {
    send(locked_target, Hit { amount: 10 })
}
```

---

## 5. Variables & Static Memory Scopes

PSPScript makes memory lifetime explicit through four variable scopes:

| Syntax | Scope | Backing Storage | Lifetime |
| :--- | :--- | :--- | :--- |
| `global var x: int = 0` | `Global` | Static `.bss` | Entire application lifetime |
| `level var score: int = 0` | `LevelArena` | Static `.bss` / Level reset | Automatically reset to initial value on `goto` state transition |
| `var speed: f32 = 10.0` | `Stack` | C function stack | Current block/function |
| `temp var v: vec2 = { 1.0, 2.0 }` | `FrameArena` | `g_frame_arena` | Wiped automatically in $O(1)$ at the end of every frame |

```pspscript
global var high_score: int = 0       // Survives across 'goto GameOver' -> 'goto Match'
level var current_score: int = 0     // Resets to 0 whenever 'goto' changes state

fn compute_impulse(dt: f32) -> vec2 {
    temp var scratch: vec2 = { 50.0, -25.0 }  // Allocated from g_frame_arena
    var scaled: vec2 = scratch * dt           // Stack local
    return scaled
}
```

Type annotations are optional when an initializer is provided (`var speed = 120.0` infers `f32`; `var count = 5` infers `int`).

---

## 6. Structs & Compile-Time Assets

### Passive Value Structs (`struct`)

Structs group plain data values (with optional default field values). Recursive value cycles (a struct containing itself) are rejected at compile time.

```pspscript
struct Stats {
    score: int = 0,
    lives: u8 = 3,
    shield: f32 = 100.0
}
```

### Compile-Time Assets (`asset`)

Assets declare textures or media baked into the binary and uploaded to VRAM at startup:

```pspscript
asset ship_tex = texture("ship.png", format: RGBA4444)
```

---

## 7. Actors: Singletons & Static Pools

Actors are the core game entities in PSPScript. An actor can be either a **singleton** (exactly 1 instance) or a **static pool** (up to `N` concurrent instances):

```pspscript
// Singleton actor (no [N] capacity)
actor Player {
    pos: vec2 = { 240.0, 136.0 }
    hp: int = 100

    init() {
        pos = { 240.0, 136.0 }
    }

    update(dt: f32) {
        if button_held(16) { pos.y -= 180.0 * dt }
        if button_held(32) { pos.y += 180.0 * dt }
    }

    draw() {
        draw_rect({ pos.x, pos.y, 16.0, 16.0 }, #00E5FF)
    }
}

// Pooled actor (static pool of up to 32 active instances)
actor Bullet[32] {
    pos: vec2
    vel: vec2 = { 300.0, 0.0 }
    life: timer = 1.5s

    update(dt: f32) {
        pos += vel * dt
        if life.elapsed() {
            destroy self
        }
    }

    draw() {
        draw_rect({ pos.x, pos.y, 4.0, 4.0 }, #FFF275)
    }
}
```

### Lifecycle Hooks

Both `actor` and `state` blocks support four optional lifecycle hooks:
- `init()` — Runs when the actor is spawned (or when entering the state).
- `update(dt: f32)` — Runs once per frame during Phase 1 (`dt` is frame delta time in seconds).
- `draw()` — Runs once per frame during Phase 3 (between `psps_video_begin_frame` and `psps_video_end_frame`).
- `exit()` — Runs right before the actor is destroyed (or before leaving a state).

### Spawning, Destroying & Iterating Over Pools

```pspscript
// Spawn with field overrides (returns ref<Bullet>)
var b = spawn Bullet { pos: { 40.0, 120.0 }, vel: { 320.0, 0.0 } }

// Or spawn without storing the handle
spawn Bullet { pos: { 40.0, 140.0 } }

// Safe iteration over all active instances in a pooled actor
each bullet in Bullet {
    if bullet.pos.x > 480.0 {
        destroy bullet
    }
}
```

> **Compile-time safety:** Attempting `each p in Player` on a **singleton** actor (declared without `[N]`) or using `self` outside an `actor` is caught by `SemanticAnalyzer` at compile time.

---

## 8. Typed Message Passing (`message`, `on`, `send`)

Actors communicate asynchronously via typed messages queued in a static frame mailbox and delivered in Phase 2 of every frame:

```pspscript
message Hit {
    amount: int,
    knockback: vec2
}

actor Enemy[32] {
    pos: vec2
    hp: int = 100

    on Hit(msg) {
        hp -= msg.amount
        pos += msg.knockback
        if hp <= 0 {
            destroy self
        }
    }
}
```

Sending a message to an actor handle:

```pspscript
send(target, Hit { amount: 25, knockback: { 12.0, 0.0 } })
```

At compile time, PSPScript verifies that:
1. `target` resolves to `ref<ActorName>`.
2. `ActorName` actually declares an `on Hit(...)` handler.
3. All required fields of `Hit` are provided with matching types.

At runtime, if `target` was destroyed before Phase 2 delivery, the generational handle check discards the message cleanly with zero crash risk.

---

## 9. Game States & Transitions (`state`, `goto`)

Game scenes (title screen, gameplay match, game over) are modeled as `state` blocks. The **first** `state` declared in the file is entered automatically at startup.

```pspscript
state Gameplay {
    init() {
        spawn Enemy { pos: { 360.0, 100.0 } }
    }

    update(dt: f32) {
        if current_score >= 500 {
            goto Victory
        }
    }

    draw() {
        clear_screen(#0B1021)
    }

    exit() {
        each e in Enemy {
            destroy e
        }
    }
}

state Victory {
    restart_delay: timer = 2.0s

    update(dt: f32) {
        if restart_delay.elapsed() {
            goto Gameplay
        }
    }

    draw() {
        clear_screen(#001122)
    }
}
```

When `goto NextState` executes:
1. The transition is queued for Phase 4 (end of the current frame).
2. The outgoing state's `exit()` hook runs.
3. `g_level_arena` and all `level var` globals are automatically reset.
4. The incoming state's fields are re-initialized and its `init()` hook runs.

---

## 10. Control Flow & Functions

PSPScript supports familiar, semicolon-free control flow and typed functions:

```pspscript
fn check_aabb(ax: f32, ay: f32, aw: f32, ah: f32, bx: f32, by: f32, bw: f32, bh: f32) -> bool {
    return (ax < bx + bw) && (ax + aw > bx) && (ay < by + bh) && (ay + ah > by)
}

fn draw_dashed_net() {
    var y: f32 = 20.0
    while y < 250.0 {
        draw_rect({ 239.0, y, 2.0, 10.0 }, #263859)
        y += 20.0
    }
}
```

- `if condition { ... } else if other { ... } else { ... }`
- `while condition { ... }`
- `each item in PoolName { ... }`
- `return` or `return expr` (non-void functions must return a value on all code paths)
- Functions without `-> Type` default to `void`.

---

## 11. Built-In PSP Runtime Functions & Input

### Rendering (480×272 Logical Coordinates)

| Function | Signature | Description |
| :--- | :--- | :--- |
| `clear_screen` | `(color) -> void` | Clears the screen with an ABGR8888 color |
| `draw_rect` | `(rect, color) -> void` | Draws a filled rectangle |
| `sprite` | `(asset, vec2[, color]) -> void` | Draws a baked texture at `pos` with optional tint |

### Input

| Function | Signature | Description |
| :--- | :--- | :--- |
| `button_held` | `(int) -> bool` | `true` every frame the button bitmask is held down |
| `button_pressed` | `(int) -> bool` | `true` only on the frame the button transitions from up to down |

PSP button bitmasks (matching `Runtime/psps_input.h`):

| Button | Bitmask | Desktop Keyboard Fallback |
| :--- | :--- | :--- |
| `CROSS` | `1` | `Z`, `J`, `Space` |
| `CIRCLE` | `2` | `X`, `K` |
| `SQUARE` | `4` | `A`, `U` |
| `TRIANGLE` | `8` | `S`, `I` |
| `UP` / `DOWN` / `LEFT` / `RIGHT` | `16` / `32` / `64` / `128` | Arrow keys |
| `L` / `R` | `256` / `512` | `Q` / `W`, `E` |
| `START` / `SELECT` | `1024` / `2048` | `Enter` / `Backspace` |

### Single-Precision Math Intrinsics

`sin(x)`, `cos(x)`, `sqrt(x)`, `abs(x)`, `min(a, b)`, `max(a, b)`, `clamp(val, low, high)`, `lerp(a, b, t)`.

---

## 12. The Deterministic 4-Phase Frame Loop

Every compiled PSPScript program executes the same predictable loop per frame:

```text
┌──────────────────────────────────────────────────────────┐
│ Phase 1: Input & Update                                  │
│   • Poll PSP controller / desktop keyboard               │
│   • Tick all timer fields on active state & actors       │
│   • Run update(dt) on active state & active actors       │
├──────────────────────────────────────────────────────────┤
│ Phase 2: Message Delivery                                │
│   • Drain static frame mailbox                           │
│   • Resolve generational handles & dispatch on Msg(...)  │
├──────────────────────────────────────────────────────────┤
│ Phase 3: Render (480×272)                                │
│   • Begin frame                                          │
│   • Run draw() on active state & active actors           │
│   • Present frame (VSync)                                │
├──────────────────────────────────────────────────────────┤
│ Phase 4: Frame Cleanup & State Transitions               │
│   • Reset g_frame_arena (wipes all 'temp var' data)      │
│   • Apply pending 'goto' transition & reset LevelArena   │
└──────────────────────────────────────────────────────────┘
```

---

## 13. Complete Playable Example

For a complete, playable PSP homebrew game showcasing singletons, pooled actors, timers, typed messages, AABB collision, state transitions, and 2D rendering, see **[examples/pong.psps](../examples/pong.psps)** and run:

```bash
# Build PSP EBOOT.PBP
dotnet run -- examples/pong.psps

# Or play immediately on desktop Linux
dotnet run -- examples/pong.psps --c-only --desktop && ./build/game_desktop
```
