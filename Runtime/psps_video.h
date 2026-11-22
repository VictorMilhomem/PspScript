#ifndef PSPS_VIDEO_H
#define PSPS_VIDEO_H

#include <stdint.h>
#include <stdbool.h>
#include <SDL2/SDL.h>
#include "psps_utils.h"
#include "psps_types.h"

/* ============================================================================
 * PSPScript SDL2 Video & 2D Sprite Renderer (480x272 Native PSP Resolution)
 * ============================================================================ */

typedef struct psps_texture {
    SDL_Texture* handle;
    uint16_t     width;
    uint16_t     height;
    bool         valid;
} psps_texture_t;

/* Lifecycle & Frame Timing */
bool  psps_video_init(const char* title);
bool  psps_video_init_scaled(const char* title, int window_scale);
void  psps_video_shutdown(void);

void  psps_video_begin_frame(psps_color_t clear_abgr);
void  psps_video_end_frame(void);
float psps_video_delta_time(void);

SDL_Renderer* psps_video_get_renderer(void);
SDL_Window*   psps_video_get_window(void);

/* Compile-Time Baked Texture Upload (Zero Heap Allocation) */
psps_texture_t psps_load_texture_rgba4444(const uint16_t* pixels, uint16_t width, uint16_t height);
psps_texture_t psps_load_texture_abgr8888(const uint32_t* pixels, uint16_t width, uint16_t height);
void           psps_destroy_texture(psps_texture_t* texture);

/* 2D Drawing Primitives */
void psps_clear_screen(psps_color_t clear_abgr);
void psps_draw_sprite(psps_texture_t texture, vec2_t pos, psps_color_t tint_abgr);
void psps_draw_sprite_ex(
    psps_texture_t texture,
    const rect_t*  src_rect,
    rect_t         dst_rect,
    float          angle_deg,
    vec2_t         origin,
    psps_color_t   tint_abgr
);
void psps_draw_rect(rect_t bounds, psps_color_t color_abgr);
void psps_draw_rect_outline(rect_t bounds, psps_color_t color_abgr);

#endif /* PSPS_VIDEO_H */

#ifdef PSPS_VIDEO_IMPLEMENTATION
#ifndef PSPS_VIDEO_IMPL_INCLUDED
#define PSPS_VIDEO_IMPL_INCLUDED

typedef struct psps_video_state {
    SDL_Window*   window;
    SDL_Renderer* renderer;
    uint64_t      last_counter;
    uint64_t      perf_freq;
    float         delta_time;
    bool          initialized;
} psps_video_state_t;

static psps_video_state_t g_psps_video = {
    .window       = NULL,
    .renderer     = NULL,
    .last_counter = 0,
    .perf_freq    = 0,
    .delta_time   = 1.0f / 60.0f,
    .initialized  = false
};

bool psps_video_init(const char* title) {
    #if defined(__PSP__)
        return psps_video_init_scaled(title, 1);
    #else
        return psps_video_init_scaled(title, 2);
    #endif
}

bool psps_video_init_scaled(const char* title, int window_scale) {
    if (g_psps_video.initialized) {
        return true;
    }

    if (window_scale < 1) {
        window_scale = 1;
    }

    if ((SDL_WasInit(SDL_INIT_VIDEO) & SDL_INIT_VIDEO) == 0) {
        if (SDL_InitSubSystem(SDL_INIT_VIDEO) < 0) {
            fprintf(stderr, "psps_video_init: SDL_InitSubSystem(SDL_INIT_VIDEO) failed: %s\n", SDL_GetError());
            return false;
        }
    }

    const char* safe_title = (title != NULL) ? title : "PSPScript Homebrew";
    int win_w = PSP_SCREEN_WIDTH * window_scale;
    int win_h = PSP_SCREEN_HEIGHT * window_scale;

    g_psps_video.window = SDL_CreateWindow(
        safe_title,
        SDL_WINDOWPOS_CENTERED,
        SDL_WINDOWPOS_CENTERED,
        win_w,
        win_h,
        SDL_WINDOW_SHOWN
    );

    if (g_psps_video.window == NULL) {
        fprintf(stderr, "psps_video_init: SDL_CreateWindow failed: %s\n", SDL_GetError());
        return false;
    }

    g_psps_video.renderer = SDL_CreateRenderer(
        g_psps_video.window,
        -1,
        SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC
    );

    if (g_psps_video.renderer == NULL) {
        /* Fallback to software renderer if hardware acceleration is unavailable */
        g_psps_video.renderer = SDL_CreateRenderer(g_psps_video.window, -1, SDL_RENDERER_SOFTWARE);
    }

    if (g_psps_video.renderer == NULL) {
        fprintf(stderr, "psps_video_init: SDL_CreateRenderer failed: %s\n", SDL_GetError());
        SDL_DestroyWindow(g_psps_video.window);
        g_psps_video.window = NULL;
        return false;
    }

    SDL_RenderSetLogicalSize(g_psps_video.renderer, PSP_SCREEN_WIDTH, PSP_SCREEN_HEIGHT);
    SDL_SetRenderDrawBlendMode(g_psps_video.renderer, SDL_BLENDMODE_BLEND);

    g_psps_video.perf_freq    = SDL_GetPerformanceFrequency();
    g_psps_video.last_counter = SDL_GetPerformanceCounter();
    g_psps_video.delta_time   = 1.0f / 60.0f;
    g_psps_video.initialized  = true;
    return true;
}

void psps_video_shutdown(void) {
    if (!g_psps_video.initialized) {
        return;
    }

    if (g_psps_video.renderer != NULL) {
        SDL_DestroyRenderer(g_psps_video.renderer);
        g_psps_video.renderer = NULL;
    }

    if (g_psps_video.window != NULL) {
        SDL_DestroyWindow(g_psps_video.window);
        g_psps_video.window = NULL;
    }

    SDL_QuitSubSystem(SDL_INIT_VIDEO);
    g_psps_video.initialized = false;
}

void psps_clear_screen(psps_color_t clear_abgr) {
    if (g_psps_video.renderer == NULL) {
        return;
    }

    SDL_SetRenderDrawColor(
        g_psps_video.renderer,
        psps_color_r(clear_abgr),
        psps_color_g(clear_abgr),
        psps_color_b(clear_abgr),
        psps_color_a(clear_abgr)
    );
    SDL_RenderClear(g_psps_video.renderer);
}

void psps_video_begin_frame(psps_color_t clear_abgr) {
    uint64_t now = SDL_GetPerformanceCounter();
    if (g_psps_video.perf_freq > 0 && g_psps_video.last_counter > 0) {
        uint64_t elapsed_ticks = now - g_psps_video.last_counter;
        float dt = (float)((double)elapsed_ticks / (double)g_psps_video.perf_freq);

        /* Clamp delta_time to [0.0001s, 0.1s] to prevent physics explosions on frame hitches */
        if (dt < 0.0001f) dt = 0.0001f;
        if (dt > 0.1000f) dt = 0.1000f;
        g_psps_video.delta_time = dt;
    }
    g_psps_video.last_counter = now;

    psps_clear_screen(clear_abgr);
}

void psps_video_end_frame(void) {
    if (g_psps_video.renderer != NULL) {
        SDL_RenderPresent(g_psps_video.renderer);
    }
}

float psps_video_delta_time(void) {
    return g_psps_video.delta_time;
}

SDL_Renderer* psps_video_get_renderer(void) {
    return g_psps_video.renderer;
}

SDL_Window* psps_video_get_window(void) {
    return g_psps_video.window;
}

psps_texture_t psps_load_texture_rgba4444(const uint16_t* pixels, uint16_t width, uint16_t height) {
    psps_texture_t result = { .handle = NULL, .width = width, .height = height, .valid = false };
    if (g_psps_video.renderer == NULL || pixels == NULL || width == 0 || height == 0) {
        return result;
    }

    SDL_Texture* tex = SDL_CreateTexture(
        g_psps_video.renderer,
        SDL_PIXELFORMAT_RGBA4444,
        SDL_TEXTUREACCESS_STATIC,
        (int)width,
        (int)height
    );

    if (tex == NULL) {
        fprintf(stderr, "psps_load_texture_rgba4444: SDL_CreateTexture failed: %s\n", SDL_GetError());
        return result;
    }

    int pitch = (int)width * (int)sizeof(uint16_t);
    if (SDL_UpdateTexture(tex, NULL, pixels, pitch) != 0) {
        fprintf(stderr, "psps_load_texture_rgba4444: SDL_UpdateTexture failed: %s\n", SDL_GetError());
        SDL_DestroyTexture(tex);
        return result;
    }

    SDL_SetTextureBlendMode(tex, SDL_BLENDMODE_BLEND);
    result.handle = tex;
    result.valid  = true;
    return result;
}

psps_texture_t psps_load_texture_abgr8888(const uint32_t* pixels, uint16_t width, uint16_t height) {
    psps_texture_t result = { .handle = NULL, .width = width, .height = height, .valid = false };
    if (g_psps_video.renderer == NULL || pixels == NULL || width == 0 || height == 0) {
        return result;
    }

    SDL_Texture* tex = SDL_CreateTexture(
        g_psps_video.renderer,
        SDL_PIXELFORMAT_ABGR8888,
        SDL_TEXTUREACCESS_STATIC,
        (int)width,
        (int)height
    );

    if (tex == NULL) {
        fprintf(stderr, "psps_load_texture_abgr8888: SDL_CreateTexture failed: %s\n", SDL_GetError());
        return result;
    }

    int pitch = (int)width * (int)sizeof(uint32_t);
    if (SDL_UpdateTexture(tex, NULL, pixels, pitch) != 0) {
        fprintf(stderr, "psps_load_texture_abgr8888: SDL_UpdateTexture failed: %s\n", SDL_GetError());
        SDL_DestroyTexture(tex);
        return result;
    }

    SDL_SetTextureBlendMode(tex, SDL_BLENDMODE_BLEND);
    result.handle = tex;
    result.valid  = true;
    return result;
}

void psps_destroy_texture(psps_texture_t* texture) {
    if (texture == NULL) {
        return;
    }
    if (texture->handle != NULL) {
        SDL_DestroyTexture(texture->handle);
        texture->handle = NULL;
    }
    texture->width  = 0;
    texture->height = 0;
    texture->valid  = false;
}

void psps_draw_sprite(psps_texture_t texture, vec2_t pos, psps_color_t tint_abgr) {
    if (g_psps_video.renderer == NULL || !texture.valid || texture.handle == NULL) {
        return;
    }

    SDL_SetTextureColorMod(
        texture.handle,
        psps_color_r(tint_abgr),
        psps_color_g(tint_abgr),
        psps_color_b(tint_abgr)
    );
    SDL_SetTextureAlphaMod(texture.handle, psps_color_a(tint_abgr));

    SDL_Rect dst = {
        .x = (int)pos.x,
        .y = (int)pos.y,
        .w = (int)texture.width,
        .h = (int)texture.height
    };

    SDL_RenderCopy(g_psps_video.renderer, texture.handle, NULL, &dst);
}

void psps_draw_sprite_ex(
    psps_texture_t texture,
    const rect_t*  src_rect,
    rect_t         dst_rect,
    float          angle_deg,
    vec2_t         origin,
    psps_color_t   tint_abgr)
{
    if (g_psps_video.renderer == NULL || !texture.valid || texture.handle == NULL) {
        return;
    }

    SDL_SetTextureColorMod(
        texture.handle,
        psps_color_r(tint_abgr),
        psps_color_g(tint_abgr),
        psps_color_b(tint_abgr)
    );
    SDL_SetTextureAlphaMod(texture.handle, psps_color_a(tint_abgr));

    SDL_Rect sdl_src;
    const SDL_Rect* src_ptr = NULL;
    if (src_rect != NULL) {
        sdl_src.x = (int)src_rect->x;
        sdl_src.y = (int)src_rect->y;
        sdl_src.w = (int)src_rect->w;
        sdl_src.h = (int)src_rect->h;
        src_ptr   = &sdl_src;
    }

    SDL_Rect sdl_dst = {
        .x = (int)dst_rect.x,
        .y = (int)dst_rect.y,
        .w = (int)dst_rect.w,
        .h = (int)dst_rect.h
    };

    SDL_Point center = {
        .x = (int)origin.x,
        .y = (int)origin.y
    };

    SDL_RenderCopyEx(
        g_psps_video.renderer,
        texture.handle,
        src_ptr,
        &sdl_dst,
        (double)angle_deg,
        &center,
        SDL_FLIP_NONE
    );
}

void psps_draw_rect(rect_t bounds, psps_color_t color_abgr) {
    if (g_psps_video.renderer == NULL) {
        return;
    }

    SDL_SetRenderDrawColor(
        g_psps_video.renderer,
        psps_color_r(color_abgr),
        psps_color_g(color_abgr),
        psps_color_b(color_abgr),
        psps_color_a(color_abgr)
    );

    SDL_Rect rect = {
        .x = (int)bounds.x,
        .y = (int)bounds.y,
        .w = (int)bounds.w,
        .h = (int)bounds.h
    };

    SDL_RenderFillRect(g_psps_video.renderer, &rect);
}

void psps_draw_rect_outline(rect_t bounds, psps_color_t color_abgr) {
    if (g_psps_video.renderer == NULL) {
        return;
    }

    SDL_SetRenderDrawColor(
        g_psps_video.renderer,
        psps_color_r(color_abgr),
        psps_color_g(color_abgr),
        psps_color_b(color_abgr),
        psps_color_a(color_abgr)
    );

    SDL_Rect rect = {
        .x = (int)bounds.x,
        .y = (int)bounds.y,
        .w = (int)bounds.w,
        .h = (int)bounds.h
    };

    SDL_RenderDrawRect(g_psps_video.renderer, &rect);
}

#endif /* PSPS_VIDEO_IMPL_INCLUDED */
#endif /* PSPS_VIDEO_IMPLEMENTATION */
