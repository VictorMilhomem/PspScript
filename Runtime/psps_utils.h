#ifndef PSPS_UTILS_H
#define PSPS_UTILS_H

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>

#define PSP_SCREEN_WIDTH  480
#define PSP_SCREEN_HEIGHT 272
#define ANALOG_DEADZONE   0.20f

#define KB(x) ((size_t)(x) * (size_t)1024u)
#define MB(x) (KB(x) * (size_t)1024u)
#define GB(x) (MB(x) * (size_t)1024u)

/* Portable C99 alignment query and attribute macros (GCC / Clang / psp-gcc) */
#if defined(__GNUC__) || defined(__clang__)
    #define PSPS_ALIGNOF(type) __alignof__(type)
    #define PSPS_ALIGNED(n)    __attribute__((aligned(n)))
#elif defined(__STDC_VERSION__) && (__STDC_VERSION__ >= 201112L)
    #define PSPS_ALIGNOF(type) _Alignof(type)
    #define PSPS_ALIGNED(n)    _Alignas(n)
#else
    #define PSPS_ALIGNOF(type) offsetof(struct { char c; type member; }, member)
    #define PSPS_ALIGNED(n)
#endif

/* Expression-safe assertion macro usable in both statements and comma expressions */
#ifdef NDEBUG
    #define ASSERT_CHECK(cond, msg) \
        ((void)((cond) ? 0 : (fprintf(stderr, "Assertion failed: %s (%s:%d)\n", (msg), __FILE__, __LINE__), abort(), 0)))
#else
    #include <assert.h>
    #define ASSERT_CHECK(cond, msg) assert((cond) && (msg))
#endif

#endif /* PSPS_UTILS_H */
