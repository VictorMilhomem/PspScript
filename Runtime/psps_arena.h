#ifndef PSPS_ARENA_H
#define PSPS_ARENA_H
#define _PSPS_STB_ALLOC_H

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include <string.h>
#include "psps_utils.h"

/* ============================================================================
 * PSPScript Static Bump Arena Allocator
 * ============================================================================
 * Designed for PlayStation Portable (Allegrex MIPS32, 32MB/64MB unified RAM,
 * no virtual memory) and desktop targets. All allocations come from fixed,
 * caller-supplied static memory buffers (.bss or static arrays).
 * ============================================================================ */

typedef struct psps_arena {
    uint8_t* buffer;
    size_t   capacity;
    size_t   current_offset;
    size_t   prev_offset;
    size_t   peak_offset;
} psps_arena_t;

typedef struct psps_arena_config {
    size_t size_of;
    size_t align;
} psps_arena_config_t;

typedef struct psps_tmp_arena {
    psps_arena_t* arena;
    size_t        prev_offset;
    size_t        current_offset;
} psps_tmp_arena_t;

/* Backwards-compatible type aliases */
typedef psps_arena_t        arena_t;
typedef psps_arena_config_t arena_config_t;
typedef psps_tmp_arena_t    tmp_arena_t;

/* Alignment & Power-of-Two Helpers */
size_t    round_up_multiple_of(size_t n, size_t multiple);
bool      is_pwr_two(uintptr_t x);
uintptr_t align_forward(uintptr_t ptr, size_t align);

/* Core Static Arena API */
psps_arena_t psps_arena_init(void* backing_buffer, size_t capacity);
void         psps_arena_reset(psps_arena_t* arena);
void         psps_arena_clear(psps_arena_t* arena);
size_t       psps_arena_remaining(const psps_arena_t* arena);

void* psps_arena_alloc_align_impl(psps_arena_t* arena, const psps_arena_config_t* config);

#define psps_arena_alloc_align(arena, ...) \
    psps_arena_alloc_align_impl((arena), &(psps_arena_config_t){ __VA_ARGS__ })

#define psps_arena_alloc(arena, type, count)                                           \
    (                                                                                  \
        ASSERT_CHECK(((size_t)(count) <= SIZE_MAX / sizeof(type)), "Arena count overflow"), \
        (type*)psps_arena_alloc_align((arena),                                         \
            .size_of = sizeof(type) * (size_t)(count),                                 \
            .align   = PSPS_ALIGNOF(type))                                             \
    )

/* Temporary Arena Checkpoints (Frame / ScopedScratch Rewind) */
psps_tmp_arena_t psps_tmp_arena_begin(psps_arena_t* arena);
void             psps_tmp_arena_end(psps_tmp_arena_t* tmp);

/* Backwards-Compatible Function & Macro Aliases */
#define arena_alloc_align(arena, ...)      psps_arena_alloc_align((arena), __VA_ARGS__)
#define arena_alloc(arena, type, count)    psps_arena_alloc((arena), type, (count))

static inline psps_arena_t mk_arena_from_buffer(void* buffer, size_t buffer_size) {
    return psps_arena_init(buffer, buffer_size);
}

static inline psps_tmp_arena_t mk_tmp_arena(psps_arena_t* arena) {
    return psps_tmp_arena_begin(arena);
}

static inline void tmp_arena_destroy(psps_tmp_arena_t* tmp) {
    psps_tmp_arena_end(tmp);
}

static inline void arena_reset_ptr(psps_arena_t* arena) {
    psps_arena_reset(arena);
}

static inline void arena_destroy(psps_arena_t* arena) {
    psps_arena_clear(arena);
}

#endif /* PSPS_ARENA_H */

#if defined(PSPS_ARENA_IMPLEMENTATION) || defined(PSPS_STB_ALLOC_IMPLEMENTATION)
#ifndef PSPS_ARENA_IMPL_INCLUDED
#define PSPS_ARENA_IMPL_INCLUDED

size_t round_up_multiple_of(size_t n, size_t multiple) {
    if (multiple == 0) {
        return n;
    }
    size_t remainder = n % multiple;
    if (remainder == 0) {
        return n;
    }
    ASSERT_CHECK(n <= SIZE_MAX - (multiple - remainder), "round_up_multiple_of overflow");
    return n + (multiple - remainder);
}

bool is_pwr_two(uintptr_t x) {
    return (x != 0) && ((x & (x - 1)) == 0);
}

uintptr_t align_forward(uintptr_t ptr, size_t align) {
    ASSERT_CHECK(is_pwr_two((uintptr_t)align), "Alignment must be a non-zero power of 2");

    uintptr_t p = ptr;
    uintptr_t a = (uintptr_t)align;
    uintptr_t mod = p & (a - 1);

    if (mod != 0) {
        uintptr_t padding = a - mod;
        ASSERT_CHECK(p <= UINTPTR_MAX - padding, "Pointer alignment overflow");
        p += padding;
    }
    return p;
}

psps_arena_t psps_arena_init(void* backing_buffer, size_t capacity) {
    ASSERT_CHECK(backing_buffer != NULL || capacity == 0, "Arena backing buffer cannot be NULL");
    return (psps_arena_t){
        .buffer         = (uint8_t*)backing_buffer,
        .capacity       = capacity,
        .current_offset = 0,
        .prev_offset    = 0,
        .peak_offset    = 0
    };
}

void* psps_arena_alloc_align_impl(psps_arena_t* arena, const psps_arena_config_t* config) {
    ASSERT_CHECK(arena != NULL && config != NULL, "Arena or config is NULL");
    ASSERT_CHECK(arena->buffer != NULL, "Arena backing buffer is NULL");

    if (config->size_of == 0) {
        return NULL;
    }

    size_t align = config->align != 0 ? config->align : sizeof(void*);
    uintptr_t base_addr = (uintptr_t)arena->buffer;
    ASSERT_CHECK(arena->current_offset <= UINTPTR_MAX - base_addr, "Arena pointer offset overflow");

    uintptr_t curr_ptr = base_addr + (uintptr_t)arena->current_offset;
    uintptr_t aligned  = align_forward(curr_ptr, align);
    size_t    offset   = (size_t)(aligned - base_addr);

    /* Overflow-safe bounds check against fixed static capacity */
    if (offset > arena->capacity || config->size_of > (arena->capacity - offset)) {
        ASSERT_CHECK(false, "Static arena out of memory (exceeded fixed budget)");
        return NULL;
    }

    void* ptr = arena->buffer + offset;
    arena->prev_offset    = offset;
    arena->current_offset = offset + config->size_of;

    if (arena->current_offset > arena->peak_offset) {
        arena->peak_offset = arena->current_offset;
    }

    memset(ptr, 0, config->size_of);
    return ptr;
}

psps_tmp_arena_t psps_tmp_arena_begin(psps_arena_t* arena) {
    ASSERT_CHECK(arena != NULL, "Cannot create checkpoint from NULL arena");
    return (psps_tmp_arena_t){
        .arena          = arena,
        .prev_offset    = arena->prev_offset,
        .current_offset = arena->current_offset
    };
}

void psps_tmp_arena_end(psps_tmp_arena_t* tmp) {
    ASSERT_CHECK(tmp != NULL && tmp->arena != NULL, "Invalid temporary arena checkpoint");
    ASSERT_CHECK(tmp->current_offset <= tmp->arena->capacity, "Corrupted temporary arena offset");

    tmp->arena->prev_offset    = tmp->prev_offset;
    tmp->arena->current_offset = tmp->current_offset;
}

void psps_arena_reset(psps_arena_t* arena) {
    ASSERT_CHECK(arena != NULL, "Arena is NULL");
    arena->current_offset = 0;
    arena->prev_offset    = 0;
}

void psps_arena_clear(psps_arena_t* arena) {
    ASSERT_CHECK(arena != NULL, "Arena is NULL");
    if (arena->buffer != NULL && arena->capacity > 0) {
        memset(arena->buffer, 0, arena->capacity);
    }
    arena->current_offset = 0;
    arena->prev_offset    = 0;
}

size_t psps_arena_remaining(const psps_arena_t* arena) {
    ASSERT_CHECK(arena != NULL, "Arena is NULL");
    return (arena->current_offset <= arena->capacity)
        ? (arena->capacity - arena->current_offset)
        : 0;
}

#endif /* PSPS_ARENA_IMPL_INCLUDED */
#endif /* PSPS_ARENA_IMPLEMENTATION || PSPS_STB_ALLOC_IMPLEMENTATION */
