# PSPScript

A C# transpiler for PSPScript, a domain-specific scripting language engineered for PlayStation Portable (PSP) homebrew game development. It compiles high-level, game-oriented scripts down to clean, static C99 targeting the PSPSDK (`psp-gcc`) and SDL2/pspgu.

## Core Idea

The PlayStation Portable operates under strict hardware constraints: an Allegrex MIPS CPU clocked up to 333 MHz, 32 MB of unified RAM (64 MB on PSP-2000+), and no virtual memory. On such hardware, frequent dynamic heap allocations (`malloc`/`free`) inevitably lead to memory fragmentation and crashes.

PSPScript resolves this by enforcing static resource budgets at compile time:

- **Static Memory Arenas**: Memory is managed via global, level, and frame arenas, eliminating runtime heap allocations and fragmentation.
- **Static Actor Model**: Actors reside in fixed-capacity pools and are referenced through 32-bit generational handles (`ref<T>`) to guarantee memory safety without garbage collection overhead.
- **Message Passing**: Actor communication uses typed messages that compile into static mailboxes and flat tagged unions.
- **Game Primitives & Asset Baking**: First-class primitives (`vec2`, `color`, `timer`) and compile-time asset conversion directly into PSP hardware-friendly formats (e.g., swizzled textures).

### Syntax Preview

```rust
message Hit {
    amount: int
}

actor Player {
    pos: vec2
    target: ref<Enemy>
    shoot_cooldown: timer

    update(dt: f32) {
        if shoot_cooldown.elapsed() {
            send(target, Hit { amount: 25 })
            shoot_cooldown = 0.5s
        }
    }
}

actor Enemy[32] {
    pos: vec2
    hp: int = 100

    on Hit(msg) {
        hp -= msg.amount
        if hp <= 0 {
            destroy self
        }
    }
}

```

## Roadmap

* [x] AST (Abstract Syntax Tree) Design
* [ ] Parser (using Parlot)
* [ ] Semantic Analysis & Type Checking
* [ ] C99 Code Generator (Arenas, Actor Pools & Message Mailboxes)
* [ ] Minimal PSP C Runtime
* [ ] CLI / PSPSDK Build Integration (`EBOOT.PBP`)

