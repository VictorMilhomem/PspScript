#ifndef PSPS_TYPES_H
#define PSPS_TYPES_H

#include <stdint.h>
#include <stdbool.h>
#include <math.h>
#include "psps_utils.h"

/* ============================================================================
 * 1. Vector & Geometry Primitives (16-byte aligned for PSP Allegrex VFPU)
 * ============================================================================ */

typedef struct PSPS_ALIGNED(16) vec2 {
    float x;
    float y;
} vec2_t;

typedef struct PSPS_ALIGNED(16) vec3 {
    float x;
    float y;
    float z;
    float _pad; /* Maintains 16-byte VFPU register alignment */
} vec3_t;

typedef struct PSPS_ALIGNED(16) rect {
    float x;
    float y;
    float w;
    float h;
} rect_t;

static inline vec2_t vec2_make(float x, float y) {
    return (vec2_t){ .x = x, .y = y };
}

static inline vec2_t vec2_add(vec2_t a, vec2_t b) {
    return (vec2_t){ .x = a.x + b.x, .y = a.y + b.y };
}

static inline vec2_t vec2_sub(vec2_t a, vec2_t b) {
    return (vec2_t){ .x = a.x - b.x, .y = a.y - b.y };
}

static inline vec2_t vec2_mul(vec2_t a, vec2_t b) {
    return (vec2_t){ .x = a.x * b.x, .y = a.y * b.y };
}

static inline vec2_t vec2_div(vec2_t a, vec2_t b) {
    return (vec2_t){
        .x = (b.x != 0.0f) ? (a.x / b.x) : 0.0f,
        .y = (b.y != 0.0f) ? (a.y / b.y) : 0.0f
    };
}

static inline vec2_t vec2_scale(vec2_t v, float s) {
    return (vec2_t){ .x = v.x * s, .y = v.y * s };
}

static inline vec2_t vec2_neg(vec2_t v) {
    return (vec2_t){ .x = -v.x, .y = -v.y };
}

static inline float vec2_dot(vec2_t a, vec2_t b) {
    return (a.x * b.x) + (a.y * b.y);
}

static inline float vec2_len_sq(vec2_t v) {
    return (v.x * v.x) + (v.y * v.y);
}

static inline float vec2_len(vec2_t v) {
    return sqrtf(vec2_len_sq(v));
}

static inline vec2_t vec2_normalize(vec2_t v) {
    float len = vec2_len(v);
    if (len <= 1e-6f) {
        return (vec2_t){ .x = 0.0f, .y = 0.0f };
    }
    float inv = 1.0f / len;
    return (vec2_t){ .x = v.x * inv, .y = v.y * inv };
}

static inline vec3_t vec3_make(float x, float y, float z) {
    return (vec3_t){ .x = x, .y = y, .z = z, ._pad = 0.0f };
}

static inline vec3_t vec3_add(vec3_t a, vec3_t b) {
    return (vec3_t){ .x = a.x + b.x, .y = a.y + b.y, .z = a.z + b.z, ._pad = 0.0f };
}

static inline vec3_t vec3_sub(vec3_t a, vec3_t b) {
    return (vec3_t){ .x = a.x - b.x, .y = a.y - b.y, .z = a.z - b.z, ._pad = 0.0f };
}

static inline vec3_t vec3_mul(vec3_t a, vec3_t b) {
    return (vec3_t){ .x = a.x * b.x, .y = a.y * b.y, .z = a.z * b.z, ._pad = 0.0f };
}

static inline vec3_t vec3_scale(vec3_t v, float s) {
    return (vec3_t){ .x = v.x * s, .y = v.y * s, .z = v.z * s, ._pad = 0.0f };
}

static inline vec3_t vec3_neg(vec3_t v) {
    return (vec3_t){ .x = -v.x, .y = -v.y, .z = -v.z, ._pad = 0.0f };
}

static inline float vec3_dot(vec3_t a, vec3_t b) {
    return (a.x * b.x) + (a.y * b.y) + (a.z * b.z);
}

static inline float vec3_len(vec3_t v) {
    return sqrtf(vec3_dot(v, v));
}

static inline vec3_t vec3_normalize(vec3_t v) {
    float len = vec3_len(v);
    if (len <= 1e-6f) {
        return (vec3_t){ .x = 0.0f, .y = 0.0f, .z = 0.0f, ._pad = 0.0f };
    }
    float inv = 1.0f / len;
    return (vec3_t){ .x = v.x * inv, .y = v.y * inv, .z = v.z * inv, ._pad = 0.0f };
}

static inline rect_t rect_make(float x, float y, float w, float h) {
    return (rect_t){ .x = x, .y = y, .w = w, .h = h };
}

static inline bool rect_contains(rect_t r, vec2_t p) {
    return (p.x >= r.x) && (p.x <= r.x + r.w) &&
           (p.y >= r.y) && (p.y <= r.y + r.h);
}

static inline bool rect_intersects(rect_t a, rect_t b) {
    return (a.x < b.x + b.w) && (a.x + a.w > b.x) &&
           (a.y < b.y + b.h) && (a.y + a.h > b.y);
}

/* ============================================================================
 * 2. Native PSP Color (Packed ABGR8888: 0xAABBGGRR)
 * ============================================================================ */

typedef uint32_t psps_color_t;

#define PSPS_COLOR_WHITE       ((psps_color_t)0xFFFFFFFFu)
#define PSPS_COLOR_BLACK       ((psps_color_t)0xFF000000u)
#define PSPS_COLOR_RED         ((psps_color_t)0xFF0000FFu)
#define PSPS_COLOR_GREEN       ((psps_color_t)0xFF00FF00u)
#define PSPS_COLOR_BLUE        ((psps_color_t)0xFFFF0000u)
#define PSPS_COLOR_TRANSPARENT ((psps_color_t)0x00000000u)

static inline psps_color_t psps_color_rgba(uint8_t r, uint8_t g, uint8_t b, uint8_t a) {
    return ((uint32_t)a << 24) | ((uint32_t)b << 16) | ((uint32_t)g << 8) | (uint32_t)r;
}

static inline uint8_t psps_color_r(psps_color_t abgr) {
    return (uint8_t)(abgr & 0xFFu);
}

static inline uint8_t psps_color_g(psps_color_t abgr) {
    return (uint8_t)((abgr >> 8) & 0xFFu);
}

static inline uint8_t psps_color_b(psps_color_t abgr) {
    return (uint8_t)((abgr >> 16) & 0xFFu);
}

static inline uint8_t psps_color_a(psps_color_t abgr) {
    return (uint8_t)((abgr >> 24) & 0xFFu);
}

/* ============================================================================
 * 3. First-Class Countdown Timer Primitive
 * ============================================================================ */

typedef struct psps_timer {
    float remaining;
    float duration;
} psps_timer_t;

static inline psps_timer_t psps_timer_make(float seconds) {
    float clamped = (seconds > 0.0f) ? seconds : 0.0f;
    return (psps_timer_t){ .remaining = clamped, .duration = clamped };
}

static inline void psps_timer_start(psps_timer_t* timer, float seconds) {
    if (timer == NULL) return;
    float clamped = (seconds > 0.0f) ? seconds : 0.0f;
    timer->remaining = clamped;
    timer->duration  = clamped;
}

static inline void psps_timer_tick(psps_timer_t* timer, float dt) {
    if (timer == NULL || dt <= 0.0f) return;
    if (timer->remaining > dt) {
        timer->remaining -= dt;
    } else {
        timer->remaining = 0.0f;
    }
}

static inline bool psps_timer_elapsed(const psps_timer_t* timer) {
    return (timer != NULL) && (timer->remaining <= 0.0f);
}

static inline void psps_timer_reset(psps_timer_t* timer) {
    if (timer == NULL) return;
    timer->remaining = timer->duration;
}

/* ============================================================================
 * 4. 32-Bit Generational Actor Reference Handle (ref<T>)
 * ============================================================================ */

#define PSPS_INVALID_INDEX ((uint16_t)0xFFFFu)

typedef struct psps_ref {
    uint16_t index;
    uint16_t generation;
} psps_ref_t;

#define PSPS_NULL_REF ((psps_ref_t){ .index = PSPS_INVALID_INDEX, .generation = 0u })

static inline bool psps_ref_is_null(psps_ref_t handle) {
    return handle.index == PSPS_INVALID_INDEX || handle.generation == 0u;
}

static inline bool psps_ref_equals(psps_ref_t a, psps_ref_t b) {
    return (a.index == b.index) && (a.generation == b.generation);
}

#endif /* PSPS_TYPES_H */
