# PSPScript Language Tutorial

Welcome to **PSPScript** — a domain-specific game scripting language designed specifically for **Sony PlayStation Portable (PSP)** homebrew development. PSPScript compiles high-level, game-oriented `.psps` scripts into clean, static, zero-heap-allocation **C99** targeting `psp-gcc` (PSPSDK + SDL2) and desktop Linux/Windows for fast local testing.

---

## Table of Contents

- [What's New in v0.2 (Migrating from v0.1)](#whats-new-in-v02-migrating-from-v01)
1. [Why PSPScript? (The PSP Hardware Model)](#1-why-pspscript-the-psp-hardware-model)
2. [Quick Start & CLI Workflow](#2-quick-start--cli-workflow)
3. [Game Configuration, Imports & Static Memory Budget](#3-game-configuration-imports--static-memory-budget)
4. [Type System & Game Primitives](#4-type-system--game-primitives)
5. [Variables & Static Memory Scopes](#5-variables--static-memory-scopes)
6. [Structs & Compile-Time Assets](#6-structs--compile-time-assets)
7. [Actors: Singletons & Static Pools](#7-actors-singletons--static-pools)
8. [Typed Message Passing (`message`, `on`, `send`)](#8-typed-message-passing-message-on-send)
9. [Game States & Transitions (`state`, `goto`)](#9-game-states--transitions-state-goto)
10. [Control Flow & Functions](#10-control-flow--functions)
11. [Built-In PSP Runtime Functions, Input & Standard Library](#11-built-in-psp-runtime-functions-input--standard-library)
12. [2D Camera, Tilemaps, Bitmap Fonts & Save Data](#12-2d-camera-tilemaps-bitmap-fonts--save-data)
13. [The Deterministic 4-Phase Frame Loop](#13-the-deterministic-4-phase-frame-loop)
14. [Complete Playable Examples](#14-complete-playable-examples)

---

## What's New in v0.2 (Migrating from v0.1)

PSPScript **v0.2** streamlines everyday PSP game code with unified `for` loops, built-in `rect` geometry helpers, bare module imports, and a bundled Standard Library (`StdLib/`).

| Area | v0.1 | v0.2 |
| :--- | :--- | :--- |
| **Loops** *(breaking)* | `while cond { ... }` | `for cond { ... }` (Go-style), `for i in start..end { ... }`, and `break` (`while` removed) |
| **Imports** | Relative paths only (`import "shared/math.psps"`) | Adds bare module imports (`import input`, `import collision`, `import "collision"`) with local override & `StdLib/` fallback |
| **Standard Library** | Manual boilerplate for button bitmasks & 2D shapes | Bundled zero-allocation `StdLib/input.psps` (`BTN_*`, `dpad_vector()`) and `StdLib/collision.psps` (`AABB`, `Circle`) |
| **`rect` Geometry** | Manual AABB math (`check_aabb(...)`) | Built-in `r.overlaps(other)`, `r.contains(point)`, and read-only `r.center` |
| **Enums & `match`** | Integer constants + `if`/`else if` chains | First-class nominal `enum` (`Season.Spring`) and exhaustive `match` statements |
| **Imported Assets** | Resolved relative to entry directory only | Resolves relative to the declaring file first, then falls back to entry directory |

### Quick Migration Examples

```pspscript
// v0.1: manual loop, manual button bitmasks, manual AABB helper
global var BTN_LEFT: int = 64
var y: f32 = 20.0
while y < 250.0 {
    draw_rect({ 239.0, y, 2.0, 10.0 }, #263859)
    y += 20.0
}
if check_aabb(ax, ay, aw, ah, bx, by, bw, bh) { ... }

// v0.2: StdLib bare import, range `for`, and built-in `rect` helpers
import input

var dir: vec2 = dpad_vector()
for i in 0..12 {
    draw_rect({ 239.0, 20.0 + (i * 20), 2.0, 10.0 }, #263859)
}
var a: rect = { ax, ay, aw, ah }
var b: rect = { bx, by, bw, bh }
if a.overlaps(b) {
    var mid: vec2 = a.center
}
```

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

Create a file with the `.psps` extension (for example, `examples/pong/main.psps`), then run the PSPScript compiler:

```bash
# 1. Full build: parse, type-check, emit build/out.c, and package build/EBOOT.PBP via Podman/Docker
dotnet run -- examples/pong/main.psps

# 2. Fast desktop preview on Linux (generates build/out.c and compiles build/game_desktop with SDL2)
dotnet run -- examples/pong/main.psps --c-only --desktop && ./build/game_desktop

# 3. Validate syntax, types, and static RAM budget without emitting C code
dotnet run -- examples/pong/main.psps --check --dump-ast
```

> **Tip (VS Code Syntax Highlighting):** Install the bundled extension from `vscode-pspscript/` for `.psps` syntax highlighting, file icons, and code snippets:
> ```bash
> mkdir -p ~/.vscode/extensions && ln -sfn "$(pwd)/vscode-pspscript" ~/.vscode/extensions/pspscript-0.1.0
> ```

---

## 3. Game Configuration, Imports & Static Memory Budget

### Splitting Projects & Importing Standard Library Modules (`import`)

Large games can be split across multiple `.psps` files—and import bundled Standard Library modules—using top-level `import` declarations:

```pspscript
import collision             // Bare StdLib module (unquoted identifier)
import "collision"           // Bare StdLib module (quoted string)
import "shared/math.psps"    // Relative path
import "actors/paddles"      // '.psps' extension is optional
```

- **Relative paths** (containing `/` or `\`, or starting with `.`) are resolved relative to the file containing the `import`.
- **Bare module names** (no path separators and no leading `.`) resolve in order:
  1. `<importingFileDir>/<name>.psps` — allows project-local overrides.
  2. Bundled `StdLib/<name>.psps` — resolved from the compiler's `StdLib/` directory.
- **Deduplication & cycle checks**: Diamond imports (multiple files importing the same local or `StdLib` module) are automatically deduplicated (`#pragma once` semantics), and circular imports are reported at compile time.
- **Assets in imported files**: `asset` paths declared inside an imported module resolve relative to the file that declared them first, falling back to the entry file's directory.

### Game Configuration (`game`)

Every `.psps` program can declare a `game` block to configure the PSP `EBOOT.PBP` title, target frame rate, and static RAM budget:

```pspscript
game "StarDust PSP" {
    ram_budget: 16MB,
    fps: 60,
    entry: Gameplay
}
```

- **Title**: Stamped automatically into `PARAM.SFO` when building `EBOOT.PBP` and used as the desktop window title.
- **`ram_budget`**: Accepts size literals (`KB` or `MB`, e.g., `512KB`, `16MB`). During semantic analysis, the compiler sums the static byte size of all globals, state fields, fixed arrays, and actor pools and fails compilation if the budget is exceeded.
- **`entry`**: Explicitly specifies the starting `state` entered at boot (accepts either an identifier like `entry: Gameplay` or a quoted string `entry: "Gameplay"`). Validated at compile time so typos fail before code generation; if omitted, the first declared `state` is used.

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

// Built-in rect methods & read-only center property
var other_box: rect = { 24.0, 44.0, 16.0, 16.0 }
var hit: bool = box.overlaps(other_box)
var inside: bool = box.contains(pos)
var mid: vec2 = box.center               // Read-only computed property ({ x + w * 0.5, y + h * 0.5 })
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

### Enumerated Types (`enum`)

Enums define nominal, type-safe sets of named variants (4 bytes / `int32_t` in C99). Different enum types and `int` cannot be mixed implicitly, and uninitialized enum variables default to their first variant (`0`):

```pspscript
enum Season {
    Spring,
    Summer,
    Fall,
    Winter
}

global var current_season: Season = Season.Spring
```

### Compile-Time Assets (`asset`)

Assets declare PNG textures or spritesheets that are decoded at **compile time** by `AssetBaker.cs` and embedded directly into static C99 pixel arrays—requiring zero runtime file I/O or heap allocation on PSP:

```pspscript
// Full 32-bit color (ABGR8888) — ideal for crisp pixel art & spritesheets
asset hero_sheet = texture("../assets/Characters/Basic Charakter Spritesheet.png", format: ABGR8888)

// 16-bit color (RGBA4444) — half the VRAM footprint on PSP
asset house_tex = texture("../assets/Objects/Free_Chicken_House.png", format: RGBA4444)
```

Paths are resolved relative to the entry `.psps` file's directory, and uploaded to VRAM automatically at startup with their exact image `(width, height)`.

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
fn draw_dashed_net() {
    for i in 0..12 {
        draw_rect({ 239.0, 20.0 + (i * 20), 2.0, 10.0 }, #263859)
    }
}

fn sum_scores(scores: [8]int) -> int {
    var total: int = 0
    for i in 0..scores.len {
        total += scores[i]
    }
    return total
}

fn wait_until_edge(x: f32) -> f32 {
    for x < 250.0 {
        x += 20.0
    }
    return x
}
```

- `if condition { ... } else if other { ... } else { ... }`
- `match value { Pattern => stmt_or_block, _ => ... }` — exhaustive pattern matching over `enum` or integer types (lowers to C99 `switch`). Every variant of an `enum` must be handled unless a `_` wildcard arm is present; integer `match` statements always require `_`.
- `for condition { ... }` — Go-style conditional loop (`condition` must be `bool`)
- `for i in start..end { ... }` — exclusive integer range loop (`i` is an immutable, block-scoped `int`)
- `each item in PoolName { ... }` — safe active-slot iteration over actor pools
- `break` — exits the innermost `for` or `each` loop immediately
- `return` or `return expr` (non-void functions must return a value on all code paths)
- Functions without `-> Type` default to `void`.

```pspscript
fn season_speed(s: Season) -> f32 {
    match s {
        Season.Spring => return 1.0,
        Season.Summer => {
            return 1.2
        },
        Season.Fall => return 0.9,
        Season.Winter => return 0.6
    }
}
```

---

## 11. Built-In PSP Runtime Functions, Input & Standard Library

### Rendering, Camera & Persistence (480×272 Logical Coordinates)

| Function | Signature | Description |
| :--- | :--- | :--- |
| `clear_screen` | `(color) -> void` | Clears the screen with an ABGR8888 color |
| `draw_rect` | `(rect, color) -> void` | Draws a filled rectangle |
| `sprite` | `(asset, vec2[, color]) -> void` | Draws a baked texture at `pos` with optional tint |
| `draw_subsprite` | `(asset, rect, vec2[, color]) -> void` | Draws a sub-rectangle (`src_rect`) from a spritesheet at `pos` |
| `draw_tilemap` | `(asset, [N]u8, int, int, int[, vec2]) -> void` | Draws a culled tilemap grid (`cols`, `rows`, `tile_size`, optional `origin`) |
| `set_camera` | `(vec2) -> void` | Sets the 2D world camera offset (`camera_pos`) |
| `get_camera` | `() -> vec2` | Returns the current 2D camera offset |
| `set_camera_enabled` | `(bool) -> void` | Enables (`true`) or disables (`false`, screen-space HUD) camera offsets |
| `draw_text` | `(string, vec2[, color[, int]]) -> void` | Draws 8×8 bitmap text (supports `\n` and integer `scale`) |
| `draw_number` | `(int, vec2[, color[, int]]) -> void` | Draws an integer using the 8×8 bitmap font |
| `save_game` | `() -> bool` | Saves all `global var` fields to Memory Stick (`sceUtilitySavedata`) / desktop file |
| `load_game` | `() -> bool` | Loads and verifies checksummed `global var` save data |

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

### Standard Library Modules (`StdLib/`)

PSPScript ships with zero-allocation standard library modules written in pure `f32` PSPScript, imported by bare name (`import input`, `import collision`, or quoted `import "collision"`):

#### `input` (`StdLib/input.psps`)

Provides all PSP button bitmask constants (`BTN_CROSS`, `BTN_CIRCLE`, `BTN_SQUARE`, `BTN_TRIANGLE`, `BTN_UP`, `BTN_DOWN`, `BTN_LEFT`, `BTN_RIGHT`, `BTN_L`, `BTN_R`, `BTN_START`, `BTN_SELECT`) and a D-Pad vector helper:

| Symbol | Type / Signature | Description |
| :--- | :--- | :--- |
| `BTN_*` | `global var ...: int` | PSP controller button bitmasks matching `Runtime/psps_input.h` |
| `dpad_vector` | `() -> vec2` | Returns unnormalized directional vector (`-1.0..1.0` per axis) from held D-Pad buttons |

```pspscript
import input

actor Player {
    pos: vec2 = { 216.0, 112.0 }
    speed: f32 = 140.0

    update(dt: f32) {
        temp var dir: vec2 = dpad_vector()
        if dir.length() > 0.0 {
            pos += dir.normalized() * speed * dt
        }
    }
}
```

#### `collision` (`StdLib/collision.psps`)

Value structs:
- `AABB { pos: vec2 = { 0.0, 0.0 }, size: vec2 = { 0.0, 0.0 } }`
- `Circle { center: vec2 = { 0.0, 0.0 }, radius: f32 = 0.0 }`

Pure collision queries (for built-in `rect` overlap/containment, use `r.overlaps(other)` and `r.contains(point)` directly):

| Function | Signature | Description |
| :--- | :--- | :--- |
| `aabb_overlaps` | `(a: AABB, b: AABB) -> bool` | Axis-aligned bounding box overlap test |
| `aabb_contains_point` | `(box: AABB, point: vec2) -> bool` | Inclusive point-in-AABB test |
| `circle_overlaps` | `(a: Circle, b: Circle) -> bool` | Squared-distance circle-circle overlap test |
| `circle_aabb_overlaps` | `(c: Circle, box: AABB) -> bool` | Closest-point circle-AABB overlap test |

```pspscript
import collision

state Arena {
    player_box: AABB
    hazard: Circle
    hit: bool = false

    init() {
        player_box.pos = { 100.0, 100.0 }
        player_box.size = { 24.0, 24.0 }
        hazard.center = { 120.0, 112.0 }
        hazard.radius = 10.0
    }

    update(dt: f32) {
        hit = circle_aabb_overlaps(hazard, player_box)
    }
}
```

---

## 12. 2D Camera, Tilemaps, Bitmap Fonts & Save Data

PSPScript includes built-in runtime support for scrolling worlds, spritesheets, HUD text, and Memory Stick save data without any dynamic heap allocation:

```pspscript
asset world_sheet = texture("tiles.png", format: RGBA4444)

// All 'global var' fields are packed into a contiguous PspsGlobalState_t struct
// that save_game() and load_game() serialize atomically with an FNV-1a checksum.
global var high_score: int = 0
global var world_tiles: [64]u8

state Overworld {
    init() {
        if !load_game() {
            high_score = 0
            save_game()
        }
        world_tiles[0] = 1
        set_camera({ 32.0, 16.0 })
    }

    draw() {
        clear_screen(#0B1021)

        // 1. World-space rendering (follows camera_pos with 480x272 frustum culling)
        set_camera_enabled(true)
        draw_tilemap(world_sheet, world_tiles, 8, 8, 16)
        draw_subsprite(world_sheet, { 0.0, 0.0, 16.0, 16.0 }, { 64.0, 48.0 })

        // 2. Screen-space HUD rendering (ignores camera_pos)
        set_camera_enabled(false)
        draw_text("HIGH SCORE:", { 8.0, 8.0 }, #FFFFFF, 2)
        draw_number(high_score, { 192.0, 8.0 }, #FFF275, 2)
    }
}
```

- **PSP Hardware (`__PSP__`)**: `save_game()` and `load_game()` invoke `sceUtilitySavedata` (`PSP_UTILITY_SAVEDATA_AUTOSAVE` / `AUTOLOAD`) to persist `DATA.BIN` under `ms0:/PSP/SAVEDATA/`.
- **Desktop Preview**: The same checksummed binary format is saved to `savedata/<GAME_ID>.bin` for local testing.

### Spritesheet Animation (`draw_subsprite` + `timer`)

Combine `draw_subsprite` with an auto-ticking `timer` field for smooth frame-rate-independent animations:

```pspscript
actor Hero {
    pos: vec2 = { 216.0, 112.0 }
    dir_row: int = 0             // 0=Down, 1=Up, 2=Left, 3=Right
    frame: int = 0               // 0..3
    anim_timer: timer = 140ms

    update(dt: f32) {
        if anim_timer.elapsed() {
            frame += 1
            if frame >= 4 { frame = 0 }
            anim_timer = 140ms
        }
    }

    draw() {
        var src_x: f32 = frame * 48
        var src_y: f32 = dir_row * 48
        draw_subsprite(hero_sheet, { src_x, src_y, 48.0, 48.0 }, pos)
    }
}
```

---

## 13. The Deterministic 4-Phase Frame Loop

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

## 14. Complete Playable Examples

Explore all three modular PSPScript projects in `examples/`:

1. **[examples/pong/main.psps](../examples/pong/main.psps)** — Full Pong game with `import input`, built-in `rect.overlaps(...)` & `rect.center`, range `for i in 0..12`, message-driven `MainMenu` (`Button[4]`, `ButtonFocus`, `ButtonPress`), selectable CPU difficulty, `Ball[4]` pool, `PaddleBounce` messages, bitmap font HUD, and `save_game()` / `load_game()`.
2. **[examples/sprite/main.psps](../examples/sprite/main.psps)** — Static PNG sprite baking (`Free_Chicken_House.png`, `Egg_item.png`), `import input` + `dpad_vector()` with `temp var`, tint cycling via `CycleTint` message, and pooled bobbing `Egg[8]` actors.
3. **[examples/anim/main.psps](../examples/anim/main.psps)** — 4-directional spritesheet animation (`Basic Charakter Spritesheet.png`, `Free Chicken Sprites.png`) using `import input` + `dpad_vector()`, `draw_subsprite`, auto-ticking `timer` frame clocks, pooled `Chicken[6]` AI + `Startle` message, and 2D camera tracking.

```bash
# Build PSP EBOOT.PBP for any example
dotnet run -- examples/pong/main.psps
dotnet run -- examples/sprite/main.psps
dotnet run -- examples/anim/main.psps

# Or compile & play on desktop Linux
dotnet run -- examples/anim/main.psps --c-only --desktop && ./build/game_desktop
```
