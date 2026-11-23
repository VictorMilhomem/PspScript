#ifndef PSPS_RUNTIME_H
#define PSPS_RUNTIME_H

/* ============================================================================
 * PSPScript Umbrella Runtime Header
 * ============================================================================
 * Define PSPS_RUNTIME_IMPLEMENTATION in exactly one C translation unit (e.g.,
 * the generated out.c) before including this header to instantiate all runtime
 * implementations:
 *
 *     #define PSPS_RUNTIME_IMPLEMENTATION
 *     #include "Runtime/psps_runtime.h"
 * ============================================================================ */

#ifdef PSPS_RUNTIME_IMPLEMENTATION
    #ifndef PSPS_ARENA_IMPLEMENTATION
        #define PSPS_ARENA_IMPLEMENTATION
    #endif
    #ifndef PSPS_VIDEO_IMPLEMENTATION
        #define PSPS_VIDEO_IMPLEMENTATION
    #endif
    #ifndef PSPS_INPUT_IMPLEMENTATION
        #define PSPS_INPUT_IMPLEMENTATION
    #endif
    #ifndef PSPS_SAVE_IMPLEMENTATION
        #define PSPS_SAVE_IMPLEMENTATION
    #endif
    #ifndef PSPS_AUDIO_IMPLEMENTATION
        #define PSPS_AUDIO_IMPLEMENTATION
    #endif
#endif

#include "psps_utils.h"
#include "psps_types.h"
#include "psps_arena.h"
#include "psps_video.h"
#include "psps_input.h"
#include "psps_save.h"
#include "psps_audio.h"

#endif /* PSPS_RUNTIME_H */
