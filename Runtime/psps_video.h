#ifndef PSPS_VIDEO_H
#define PSPS_VIDEO_H

#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <SDL2/SDL.h>
#include "psps_utils.h"
#include "psps_types.h"

/* ============================================================================
 * PSPScript SDL2 Video, 2D Camera, Tilemap & Bitmap Font Renderer (480x272)
 * ============================================================================ */

typedef struct psps_texture {
    SDL_Texture* handle;
    uint16_t     width;
    uint16_t     height;
    bool         valid;
} psps_texture_t;

typedef struct psps_tilemap {
    const uint8_t* tiles;     /* Flat row-major grid of tile indices (0 = empty) */
    uint16_t       cols;      /* Map width in tiles                              */
    uint16_t       rows;      /* Map height in tiles                             */
    uint16_t       tile_size; /* Square tile size in pixels (e.g. 8, 16, 32)     */
} psps_tilemap_t;

#define PSPS_TILE_ID_MASK 0x3FFFu
#define PSPS_TILE_FLIP_H  0x8000u
#define PSPS_TILE_FLIP_V  0x4000u

typedef struct psps_tilemap_tileset {
    const psps_texture_t* texture;       /* Uploaded VRAM tileset texture                  */
    uint16_t              first_gid;     /* 1-based first GID in this tilemap              */
    uint16_t              tile_size;     /* Tile size in pixels (0 = use map->tile_size)   */
} psps_tilemap_tileset_t;

typedef struct psps_baked_tilemap {
    const psps_texture_t*         tileset;       /* Primary tileset texture (compat / fallback)    */
    const psps_tilemap_tileset_t* tilesets;      /* Sorted ascending by first_gid (or NULL)        */
    uint16_t                      tileset_count; /* Number of entries in tilesets                  */
    uint16_t                      cols;          /* Map width in tiles                             */
    uint16_t                      rows;          /* Map height in tiles                            */
    uint16_t                      tile_size;     /* Square tile size in pixels (e.g. 8, 16, 32)    */
    uint16_t                      layer_count;   /* Number of renderable tile layers               */
    uint16_t*                     layers_mut;    /* Writable .bss [layer_count * cols * rows] or 0 */
    const uint16_t*               layers_ro;     /* Pristine .rodata [layer_count * cols * rows]   */
    uint8_t*                      collision_mut; /* Writable .bss [cols * rows] or NULL            */
    const uint8_t*                collision_ro;  /* Pristine .rodata [cols * rows]                 */
} psps_baked_tilemap_t;

/* Lifecycle & Frame Timing */
bool  psps_video_init(const char* title);
bool  psps_video_init_scaled(const char* title, int window_scale);
void  psps_video_shutdown(void);

void  psps_video_begin_frame(psps_color_t clear_abgr);
void  psps_video_end_frame(void);
float psps_video_delta_time(void);

SDL_Renderer* psps_video_get_renderer(void);
SDL_Window*   psps_video_get_window(void);

/* 2D Camera (World-Space Offset) */
void   psps_video_set_camera(vec2_t pos);
vec2_t psps_video_get_camera(void);
void   psps_video_set_camera_enabled(bool enabled);

/* Compile-Time Baked Texture Upload (Zero Heap Allocation) */
psps_texture_t psps_load_texture_rgba4444(const uint16_t* pixels, uint16_t width, uint16_t height);
psps_texture_t psps_load_texture_abgr8888(const uint32_t* pixels, uint16_t width, uint16_t height);
void           psps_destroy_texture(psps_texture_t* texture);

/* 2D Drawing Primitives, Spritesheets & Tilemaps */
void psps_clear_screen(psps_color_t clear_abgr);
void psps_draw_sprite(psps_texture_t texture, vec2_t pos, psps_color_t tint_abgr);
void psps_draw_subsprite(psps_texture_t texture, rect_t src_rect, vec2_t pos, psps_color_t tint_abgr);
void psps_draw_subsprite_ex(
    psps_texture_t texture,
    rect_t         src_rect,
    vec2_t         pos,
    psps_color_t   tint_abgr,
    bool           flip_x,
    bool           flip_y
);
void psps_draw_sprite_ex(
    psps_texture_t texture,
    const rect_t*  src_rect,
    rect_t         dst_rect,
    float          angle_deg,
    vec2_t         origin,
    psps_color_t   tint_abgr
);
void psps_draw_tilemap(
    psps_texture_t        tileset,
    const psps_tilemap_t* map,
    vec2_t                origin,
    psps_color_t          tint_abgr
);
void    psps_baked_tilemap_draw_layer(const psps_baked_tilemap_t* map, int32_t layer, vec2_t origin);
void    psps_baked_tilemap_draw(const psps_baked_tilemap_t* map, vec2_t origin);
int32_t psps_baked_tilemap_get_tile(const psps_baked_tilemap_t* map, int32_t layer, int32_t col, int32_t row);
void    psps_baked_tilemap_set_tile(psps_baked_tilemap_t* map, int32_t layer, int32_t col, int32_t row, int32_t tile_id);
bool    psps_baked_tilemap_is_solid(const psps_baked_tilemap_t* map, int32_t col, int32_t row);
void    psps_baked_tilemap_set_solid(psps_baked_tilemap_t* map, int32_t col, int32_t row, bool solid);
bool    psps_baked_tilemap_collides_rect(const psps_baked_tilemap_t* map, rect_t hitbox);
void    psps_baked_tilemap_reset(psps_baked_tilemap_t* map);
void psps_draw_rect(rect_t bounds, psps_color_t color_abgr);
void psps_draw_rect_outline(rect_t bounds, psps_color_t color_abgr);

/* Lightweight Embedded 8x8 ASCII Bitmap Font Rendering */
void psps_draw_text(const char* text, vec2_t pos, psps_color_t color_abgr);
void psps_draw_text_scaled(const char* text, vec2_t pos, int scale, psps_color_t color_abgr);
void psps_draw_number(int32_t value, vec2_t pos, psps_color_t color_abgr, int scale);

#endif /* PSPS_VIDEO_H */

#ifdef PSPS_VIDEO_IMPLEMENTATION
#ifndef PSPS_VIDEO_IMPL_INCLUDED
#define PSPS_VIDEO_IMPL_INCLUDED

/* ============================================================================
 * Embedded 8x8 Printable ASCII Bitmap Font (0x20..0x7E, 95 glyphs, 1bpp)
 * Stored in static .rodata (760 bytes) — zero external files, zero heap.
 * ============================================================================ */
static const uint8_t g_psps_font8x8[95][8] = {
    /* 0x20 ' ' */ {0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00},
    /* 0x21 '!' */ {0x18,0x3C,0x3C,0x18,0x18,0x00,0x18,0x00},
    /* 0x22 '"' */ {0x36,0x36,0x24,0x00,0x00,0x00,0x00,0x00},
    /* 0x23 '#' */ {0x36,0x36,0x7F,0x36,0x7F,0x36,0x36,0x00},
    /* 0x24 '$' */ {0x0C,0x3E,0x03,0x1E,0x30,0x1F,0x0C,0x00},
    /* 0x25 '%' */ {0x00,0x63,0x33,0x18,0x0C,0x66,0x63,0x00},
    /* 0x26 '&' */ {0x1C,0x36,0x1C,0x6E,0x3B,0x33,0x6E,0x00},
    /* 0x27 ''' */ {0x06,0x06,0x03,0x00,0x00,0x00,0x00,0x00},
    /* 0x28 '(' */ {0x18,0x0C,0x06,0x06,0x06,0x0C,0x18,0x00},
    /* 0x29 ')' */ {0x06,0x0C,0x18,0x18,0x18,0x0C,0x06,0x00},
    /* 0x2A '*' */ {0x00,0x66,0x3C,0xFF,0x3C,0x66,0x00,0x00},
    /* 0x2B '+' */ {0x00,0x0C,0x0C,0x3F,0x0C,0x0C,0x00,0x00},
    /* 0x2C ',' */ {0x00,0x00,0x00,0x00,0x00,0x0C,0x0C,0x06},
    /* 0x2D '-' */ {0x00,0x00,0x00,0x3F,0x00,0x00,0x00,0x00},
    /* 0x2E '.' */ {0x00,0x00,0x00,0x00,0x00,0x0C,0x0C,0x00},
    /* 0x2F '/' */ {0x60,0x30,0x18,0x0C,0x06,0x03,0x01,0x00},
    /* 0x30 '0' */ {0x3E,0x63,0x73,0x7B,0x6F,0x67,0x3E,0x00},
    /* 0x31 '1' */ {0x0C,0x0E,0x0C,0x0C,0x0C,0x0C,0x3F,0x00},
    /* 0x32 '2' */ {0x1E,0x33,0x30,0x1C,0x06,0x33,0x3F,0x00},
    /* 0x33 '3' */ {0x1E,0x33,0x30,0x1C,0x30,0x33,0x1E,0x00},
    /* 0x34 '4' */ {0x38,0x3C,0x36,0x33,0x7F,0x30,0x78,0x00},
    /* 0x35 '5' */ {0x3F,0x03,0x1F,0x30,0x30,0x33,0x1E,0x00},
    /* 0x36 '6' */ {0x1C,0x06,0x03,0x1F,0x33,0x33,0x1E,0x00},
    /* 0x37 '7' */ {0x3F,0x33,0x30,0x18,0x0C,0x0C,0x0C,0x00},
    /* 0x38 '8' */ {0x1E,0x33,0x33,0x1E,0x33,0x33,0x1E,0x00},
    /* 0x39 '9' */ {0x1E,0x33,0x33,0x3E,0x30,0x18,0x0E,0x00},
    /* 0x3A ':' */ {0x00,0x0C,0x0C,0x00,0x00,0x0C,0x0C,0x00},
    /* 0x3B ';' */ {0x00,0x0C,0x0C,0x00,0x00,0x0C,0x0C,0x06},
    /* 0x3C '<' */ {0x18,0x0C,0x06,0x03,0x06,0x0C,0x18,0x00},
    /* 0x3D '=' */ {0x00,0x00,0x3F,0x00,0x00,0x3F,0x00,0x00},
    /* 0x3E '>' */ {0x06,0x0C,0x18,0x30,0x18,0x0C,0x06,0x00},
    /* 0x3F '?' */ {0x1E,0x33,0x30,0x18,0x0C,0x00,0x0C,0x00},
    /* 0x40 '@' */ {0x3E,0x63,0x7B,0x7B,0x7B,0x03,0x1E,0x00},
    /* 0x41 'A' */ {0x0C,0x1E,0x33,0x33,0x3F,0x33,0x33,0x00},
    /* 0x42 'B' */ {0x3F,0x66,0x66,0x3E,0x66,0x66,0x3F,0x00},
    /* 0x43 'C' */ {0x3C,0x66,0x03,0x03,0x03,0x66,0x3C,0x00},
    /* 0x44 'D' */ {0x1F,0x36,0x66,0x66,0x66,0x36,0x1F,0x00},
    /* 0x45 'E' */ {0x7F,0x46,0x16,0x1E,0x16,0x46,0x7F,0x00},
    /* 0x46 'F' */ {0x7F,0x46,0x16,0x1E,0x16,0x06,0x0F,0x00},
    /* 0x47 'G' */ {0x3C,0x66,0x03,0x03,0x73,0x66,0x7C,0x00},
    /* 0x48 'H' */ {0x33,0x33,0x33,0x3F,0x33,0x33,0x33,0x00},
    /* 0x49 'I' */ {0x1E,0x0C,0x0C,0x0C,0x0C,0x0C,0x1E,0x00},
    /* 0x4A 'J' */ {0x78,0x30,0x30,0x30,0x33,0x33,0x1E,0x00},
    /* 0x4B 'K' */ {0x67,0x66,0x36,0x1E,0x36,0x66,0x67,0x00},
    /* 0x4C 'L' */ {0x0F,0x06,0x06,0x06,0x46,0x66,0x7F,0x00},
    /* 0x4D 'M' */ {0x63,0x77,0x7F,0x7F,0x6B,0x63,0x63,0x00},
    /* 0x4E 'N' */ {0x63,0x67,0x6F,0x7B,0x73,0x63,0x63,0x00},
    /* 0x4F 'O' */ {0x1C,0x36,0x63,0x63,0x63,0x36,0x1C,0x00},
    /* 0x50 'P' */ {0x3F,0x66,0x66,0x3E,0x06,0x06,0x0F,0x00},
    /* 0x51 'Q' */ {0x1E,0x33,0x33,0x33,0x3B,0x1E,0x38,0x00},
    /* 0x52 'R' */ {0x3F,0x66,0x66,0x3E,0x36,0x66,0x67,0x00},
    /* 0x53 'S' */ {0x1E,0x33,0x07,0x0E,0x38,0x33,0x1E,0x00},
    /* 0x54 'T' */ {0x3F,0x2D,0x0C,0x0C,0x0C,0x0C,0x1E,0x00},
    /* 0x55 'U' */ {0x33,0x33,0x33,0x33,0x33,0x33,0x3F,0x00},
    /* 0x56 'V' */ {0x33,0x33,0x33,0x33,0x33,0x1E,0x0C,0x00},
    /* 0x57 'W' */ {0x63,0x63,0x63,0x6B,0x7F,0x77,0x63,0x00},
    /* 0x58 'X' */ {0x63,0x63,0x36,0x1C,0x1C,0x36,0x63,0x00},
    /* 0x59 'Y' */ {0x33,0x33,0x33,0x1E,0x0C,0x0C,0x1E,0x00},
    /* 0x5A 'Z' */ {0x7F,0x63,0x31,0x18,0x4C,0x66,0x7F,0x00},
    /* 0x5B '[' */ {0x1E,0x06,0x06,0x06,0x06,0x06,0x1E,0x00},
    /* 0x5C '\' */ {0x03,0x06,0x0C,0x18,0x30,0x60,0x40,0x00},
    /* 0x5D ']' */ {0x1E,0x18,0x18,0x18,0x18,0x18,0x1E,0x00},
    /* 0x5E '^' */ {0x08,0x1C,0x36,0x63,0x00,0x00,0x00,0x00},
    /* 0x5F '_' */ {0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xFF},
    /* 0x60 '`' */ {0x0C,0x0C,0x18,0x00,0x00,0x00,0x00,0x00},
    /* 0x61 'a' */ {0x00,0x00,0x1E,0x30,0x3E,0x33,0x6E,0x00},
    /* 0x62 'b' */ {0x07,0x06,0x06,0x3E,0x66,0x66,0x3B,0x00},
    /* 0x63 'c' */ {0x00,0x00,0x1E,0x33,0x03,0x33,0x1E,0x00},
    /* 0x64 'd' */ {0x38,0x30,0x30,0x3E,0x33,0x33,0x6E,0x00},
    /* 0x65 'e' */ {0x00,0x00,0x1E,0x33,0x3F,0x03,0x1E,0x00},
    /* 0x66 'f' */ {0x1C,0x36,0x06,0x0F,0x06,0x06,0x0F,0x00},
    /* 0x67 'g' */ {0x00,0x00,0x6E,0x33,0x33,0x3E,0x30,0x1F},
    /* 0x68 'h' */ {0x07,0x06,0x36,0x6E,0x66,0x66,0x67,0x00},
    /* 0x69 'i' */ {0x0C,0x00,0x0E,0x0C,0x0C,0x0C,0x1E,0x00},
    /* 0x6A 'j' */ {0x30,0x00,0x30,0x30,0x30,0x33,0x33,0x1E},
    /* 0x6B 'k' */ {0x07,0x06,0x66,0x36,0x1E,0x36,0x67,0x00},
    /* 0x6C 'l' */ {0x0E,0x0C,0x0C,0x0C,0x0C,0x0C,0x1E,0x00},
    /* 0x6D 'm' */ {0x00,0x00,0x33,0x7F,0x7F,0x6B,0x63,0x00},
    /* 0x6E 'n' */ {0x00,0x00,0x1F,0x33,0x33,0x33,0x33,0x00},
    /* 0x6F 'o' */ {0x00,0x00,0x1E,0x33,0x33,0x33,0x1E,0x00},
    /* 0x70 'p' */ {0x00,0x00,0x3B,0x66,0x66,0x3E,0x06,0x0F},
    /* 0x71 'q' */ {0x00,0x00,0x6E,0x33,0x33,0x3E,0x30,0x78},
    /* 0x72 'r' */ {0x00,0x00,0x3B,0x6E,0x66,0x06,0x0F,0x00},
    /* 0x73 's' */ {0x00,0x00,0x3E,0x03,0x1E,0x30,0x1F,0x00},
    /* 0x74 't' */ {0x08,0x0C,0x3E,0x0C,0x0C,0x2C,0x18,0x00},
    /* 0x75 'u' */ {0x00,0x00,0x33,0x33,0x33,0x33,0x6E,0x00},
    /* 0x76 'v' */ {0x00,0x00,0x33,0x33,0x33,0x1E,0x0C,0x00},
    /* 0x77 'w' */ {0x00,0x00,0x63,0x6B,0x7F,0x7F,0x36,0x00},
    /* 0x78 'x' */ {0x00,0x00,0x63,0x36,0x1C,0x36,0x63,0x00},
    /* 0x79 'y' */ {0x00,0x00,0x33,0x33,0x33,0x3E,0x30,0x1F},
    /* 0x7A 'z' */ {0x00,0x00,0x3F,0x19,0x0C,0x26,0x3F,0x00},
    /* 0x7B '{' */ {0x38,0x0C,0x0C,0x07,0x0C,0x0C,0x38,0x00},
    /* 0x7C '|' */ {0x18,0x18,0x18,0x00,0x18,0x18,0x18,0x00},
    /* 0x7D '}' */ {0x07,0x0C,0x0C,0x38,0x0C,0x0C,0x07,0x00},
    /* 0x7E '~' */ {0x6E,0x3B,0x00,0x00,0x00,0x00,0x00,0x00}
};

/* 16x6 grid of 8x8 glyphs = 128x48 pixels in ABGR8888 (24 KB static scratch) */
static uint32_t g_psps_font_atlas_pixels[128u * 48u];

typedef struct psps_video_state {
    SDL_Window*   window;
    SDL_Renderer* renderer;
    SDL_Texture*  font_atlas;
    vec2_t        camera_pos;
    bool          camera_enabled;
    uint64_t      last_counter;
    uint64_t      perf_freq;
    float         delta_time;
    bool          initialized;
} psps_video_state_t;

static psps_video_state_t g_psps_video = {
    .window         = NULL,
    .renderer       = NULL,
    .font_atlas     = NULL,
    .camera_pos     = { .x = 0.0f, .y = 0.0f },
    .camera_enabled = true,
    .last_counter   = 0,
    .perf_freq      = 0,
    .delta_time     = 1.0f / 60.0f,
    .initialized    = false
};

static void psps_video_build_font_atlas(void) {
    if (g_psps_video.renderer == NULL) {
        return;
    }

    memset(g_psps_font_atlas_pixels, 0, sizeof(g_psps_font_atlas_pixels));

    for (int glyph = 0; glyph < 95; ++glyph) {
        int gx = (glyph % 16) * 8;
        int gy = (glyph / 16) * 8;
        for (int row = 0; row < 8; ++row) {
            uint8_t bits = g_psps_font8x8[glyph][row];
            for (int col = 0; col < 8; ++col) {
                if ((bits & (1u << col)) != 0u) {
                    g_psps_font_atlas_pixels[(gy + row) * 128 + (gx + col)] = 0xFFFFFFFFu;
                }
            }
        }
    }

    SDL_Texture* atlas = SDL_CreateTexture(
        g_psps_video.renderer,
        SDL_PIXELFORMAT_ABGR8888,
        SDL_TEXTUREACCESS_STATIC,
        128,
        48
    );

    if (atlas != NULL) {
        SDL_UpdateTexture(atlas, NULL, g_psps_font_atlas_pixels, 128 * (int)sizeof(uint32_t));
        SDL_SetTextureBlendMode(atlas, SDL_BLENDMODE_BLEND);
        g_psps_video.font_atlas = atlas;
    }
}

static inline vec2_t psps_video_apply_camera(vec2_t world_pos) {
    if (!g_psps_video.camera_enabled) {
        return world_pos;
    }
    return vec2_make(
        world_pos.x - g_psps_video.camera_pos.x,
        world_pos.y - g_psps_video.camera_pos.y
    );
}

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

#if defined(__PSP__)
    /* PSP hardware framebuffer is strictly 480x272; window_scale applies to desktop previews only */
    window_scale = 1;
#else
    if (window_scale < 1) {
        window_scale = 1;
    }
#endif

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

    psps_video_build_font_atlas();

    g_psps_video.camera_pos     = vec2_make(0.0f, 0.0f);
    g_psps_video.camera_enabled = true;
    g_psps_video.perf_freq      = SDL_GetPerformanceFrequency();
    g_psps_video.last_counter   = SDL_GetPerformanceCounter();
    g_psps_video.delta_time     = 1.0f / 60.0f;
    g_psps_video.initialized    = true;
    return true;
}

void psps_video_shutdown(void) {
    if (!g_psps_video.initialized) {
        return;
    }

    if (g_psps_video.font_atlas != NULL) {
        SDL_DestroyTexture(g_psps_video.font_atlas);
        g_psps_video.font_atlas = NULL;
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

void psps_video_set_camera(vec2_t pos) {
    g_psps_video.camera_pos = pos;
}

vec2_t psps_video_get_camera(void) {
    return g_psps_video.camera_pos;
}

void psps_video_set_camera_enabled(bool enabled) {
    g_psps_video.camera_enabled = enabled;
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

    vec2_t screen = psps_video_apply_camera(pos);

    SDL_SetTextureColorMod(
        texture.handle,
        psps_color_r(tint_abgr),
        psps_color_g(tint_abgr),
        psps_color_b(tint_abgr)
    );
    SDL_SetTextureAlphaMod(texture.handle, psps_color_a(tint_abgr));

    SDL_Rect dst = {
        .x = (int)screen.x,
        .y = (int)screen.y,
        .w = (int)texture.width,
        .h = (int)texture.height
    };

    SDL_RenderCopy(g_psps_video.renderer, texture.handle, NULL, &dst);
}

void psps_draw_subsprite_ex(
    psps_texture_t texture,
    rect_t         src_rect,
    vec2_t         pos,
    psps_color_t   tint_abgr,
    bool           flip_x,
    bool           flip_y)
{
    if (g_psps_video.renderer == NULL || !texture.valid || texture.handle == NULL) {
        return;
    }

    vec2_t screen = psps_video_apply_camera(pos);

    SDL_SetTextureColorMod(
        texture.handle,
        psps_color_r(tint_abgr),
        psps_color_g(tint_abgr),
        psps_color_b(tint_abgr)
    );
    SDL_SetTextureAlphaMod(texture.handle, psps_color_a(tint_abgr));

    SDL_Rect sdl_src = {
        .x = (int)src_rect.x,
        .y = (int)src_rect.y,
        .w = (int)src_rect.w,
        .h = (int)src_rect.h
    };

    SDL_Rect sdl_dst = {
        .x = (int)screen.x,
        .y = (int)screen.y,
        .w = (int)src_rect.w,
        .h = (int)src_rect.h
    };

    SDL_RendererFlip flip = SDL_FLIP_NONE;
    if (flip_x) flip = (SDL_RendererFlip)(flip | SDL_FLIP_HORIZONTAL);
    if (flip_y) flip = (SDL_RendererFlip)(flip | SDL_FLIP_VERTICAL);

    if (flip == SDL_FLIP_NONE) {
        SDL_RenderCopy(g_psps_video.renderer, texture.handle, &sdl_src, &sdl_dst);
    } else {
        SDL_RenderCopyEx(g_psps_video.renderer, texture.handle, &sdl_src, &sdl_dst, 0.0, NULL, flip);
    }
}

void psps_draw_subsprite(psps_texture_t texture, rect_t src_rect, vec2_t pos, psps_color_t tint_abgr) {
    psps_draw_subsprite_ex(texture, src_rect, pos, tint_abgr, false, false);
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

    vec2_t screen = psps_video_apply_camera(vec2_make(dst_rect.x, dst_rect.y));

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
        .x = (int)screen.x,
        .y = (int)screen.y,
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

void psps_draw_tilemap(
    psps_texture_t        tileset,
    const psps_tilemap_t* map,
    vec2_t                origin,
    psps_color_t          tint_abgr)
{
    if (g_psps_video.renderer == NULL || !tileset.valid || tileset.handle == NULL ||
        map == NULL || map->tiles == NULL || map->tile_size == 0) {
        return;
    }

    int ts = (int)map->tile_size;
    int sheet_cols = (int)tileset.width / ts;
    if (sheet_cols <= 0) {
        return;
    }

    vec2_t cam = g_psps_video.camera_enabled ? g_psps_video.camera_pos : vec2_make(0.0f, 0.0f);

    /* Viewport frustum culling against 480x272 screen bounds */
    int min_col = (int)((cam.x - origin.x) / (float)ts);
    int max_col = (int)((cam.x + (float)PSP_SCREEN_WIDTH - origin.x) / (float)ts) + 1;
    int min_row = (int)((cam.y - origin.y) / (float)ts);
    int max_row = (int)((cam.y + (float)PSP_SCREEN_HEIGHT - origin.y) / (float)ts) + 1;

    if (min_col < 0) min_col = 0;
    if (min_row < 0) min_row = 0;
    if (max_col > (int)map->cols) max_col = (int)map->cols;
    if (max_row > (int)map->rows) max_row = (int)map->rows;

    SDL_SetTextureColorMod(
        tileset.handle,
        psps_color_r(tint_abgr),
        psps_color_g(tint_abgr),
        psps_color_b(tint_abgr)
    );
    SDL_SetTextureAlphaMod(tileset.handle, psps_color_a(tint_abgr));

    for (int r = min_row; r < max_row; ++r) {
        for (int c = min_col; c < max_col; ++c) {
            uint8_t tile_id = map->tiles[r * (int)map->cols + c];
            if (tile_id == 0u) {
                continue; /* 0 = empty / transparent tile */
            }

            int idx = (int)tile_id - 1;
            SDL_Rect src = {
                .x = (idx % sheet_cols) * ts,
                .y = (idx / sheet_cols) * ts,
                .w = ts,
                .h = ts
            };

            SDL_Rect dst = {
                .x = (int)(origin.x + (float)(c * ts) - cam.x),
                .y = (int)(origin.y + (float)(r * ts) - cam.y),
                .w = ts,
                .h = ts
            };

            SDL_RenderCopy(g_psps_video.renderer, tileset.handle, &src, &dst);
        }
    }
}

void psps_baked_tilemap_draw_layer(const psps_baked_tilemap_t* map, int32_t layer, vec2_t origin) {
    if (g_psps_video.renderer == NULL || map == NULL || map->tile_size == 0) {
        return;
    }

    bool has_multi = (map->tilesets != NULL && map->tileset_count > 0u);
    if (!has_multi && (map->tileset == NULL || !map->tileset->valid || map->tileset->handle == NULL)) {
        return;
    }
    if (layer < 0 || layer >= (int32_t)map->layer_count) {
        return;
    }

    const uint16_t* data = (map->layers_mut != NULL) ? map->layers_mut : map->layers_ro;
    if (data == NULL) {
        return;
    }

    int ts = (int)map->tile_size;
    int single_sheet_cols = 0;
    if (has_multi) {
        for (uint16_t t = 0u; t < map->tileset_count; ++t) {
            const psps_texture_t* tex = map->tilesets[t].texture;
            if (tex != NULL && tex->valid && tex->handle != NULL) {
                SDL_SetTextureColorMod(tex->handle, 255u, 255u, 255u);
                SDL_SetTextureAlphaMod(tex->handle, 255u);
            }
        }
    } else {
        single_sheet_cols = (int)map->tileset->width / ts;
        if (single_sheet_cols <= 0) {
            return;
        }
        SDL_SetTextureColorMod(map->tileset->handle, 255u, 255u, 255u);
        SDL_SetTextureAlphaMod(map->tileset->handle, 255u);
    }

    vec2_t cam = g_psps_video.camera_enabled ? g_psps_video.camera_pos : vec2_make(0.0f, 0.0f);

    int min_col = (int)floorf((cam.x - origin.x) / (float)ts);
    int max_col = (int)floorf((cam.x + (float)PSP_SCREEN_WIDTH - origin.x) / (float)ts) + 1;
    int min_row = (int)floorf((cam.y - origin.y) / (float)ts);
    int max_row = (int)floorf((cam.y + (float)PSP_SCREEN_HEIGHT - origin.y) / (float)ts) + 1;

    if (min_col < 0) min_col = 0;
    if (min_row < 0) min_row = 0;
    if (max_col > (int)map->cols) max_col = (int)map->cols;
    if (max_row > (int)map->rows) max_row = (int)map->rows;

    size_t layer_offset = (size_t)layer * (size_t)map->cols * (size_t)map->rows;

    for (int r = min_row; r < max_row; ++r) {
        for (int c = min_col; c < max_col; ++c) {
            uint16_t raw = data[layer_offset + (size_t)r * (size_t)map->cols + (size_t)c];
            uint16_t tile_id = (uint16_t)(raw & PSPS_TILE_ID_MASK);
            if (tile_id == 0u) {
                continue;
            }

            SDL_Texture* tex_handle = NULL;
            int idx = 0;
            int sheet_cols = 0;
            int src_ts = ts;

            if (has_multi) {
                const psps_tilemap_tileset_t* chosen = NULL;
                for (int t = (int)map->tileset_count - 1; t >= 0; --t) {
                    if (map->tilesets[t].first_gid <= tile_id) {
                        chosen = &map->tilesets[t];
                        break;
                    }
                }
                if (chosen == NULL || chosen->texture == NULL ||
                    !chosen->texture->valid || chosen->texture->handle == NULL) {
                    continue;
                }
                src_ts = (chosen->tile_size > 0u) ? (int)chosen->tile_size : ts;
                sheet_cols = (int)chosen->texture->width / src_ts;
                if (sheet_cols <= 0) {
                    continue;
                }
                idx = (int)tile_id - (int)chosen->first_gid;
                tex_handle = chosen->texture->handle;
            } else {
                tex_handle = map->tileset->handle;
                sheet_cols = single_sheet_cols;
                idx = (int)tile_id - 1;
            }

            SDL_Rect src = {
                .x = (idx % sheet_cols) * src_ts,
                .y = (idx / sheet_cols) * src_ts,
                .w = src_ts,
                .h = src_ts
            };

            SDL_Rect dst = {
                .x = (int)(origin.x + (float)(c * ts) - cam.x),
                .y = (int)(origin.y + (float)(r * ts) - cam.y),
                .w = ts,
                .h = ts
            };

            SDL_RendererFlip flip = SDL_FLIP_NONE;
            if ((raw & PSPS_TILE_FLIP_H) != 0u) flip = (SDL_RendererFlip)(flip | SDL_FLIP_HORIZONTAL);
            if ((raw & PSPS_TILE_FLIP_V) != 0u) flip = (SDL_RendererFlip)(flip | SDL_FLIP_VERTICAL);

            if (flip == SDL_FLIP_NONE) {
                SDL_RenderCopy(g_psps_video.renderer, tex_handle, &src, &dst);
            } else {
                SDL_RenderCopyEx(g_psps_video.renderer, tex_handle, &src, &dst, 0.0, NULL, flip);
            }
        }
    }
}

void psps_baked_tilemap_draw(const psps_baked_tilemap_t* map, vec2_t origin) {
    if (map == NULL) {
        return;
    }
    for (int32_t layer = 0; layer < (int32_t)map->layer_count; ++layer) {
        psps_baked_tilemap_draw_layer(map, layer, origin);
    }
}

int32_t psps_baked_tilemap_get_tile(const psps_baked_tilemap_t* map, int32_t layer, int32_t col, int32_t row) {
    if (map == NULL || layer < 0 || layer >= (int32_t)map->layer_count ||
        col < 0 || col >= (int32_t)map->cols || row < 0 || row >= (int32_t)map->rows) {
        return 0;
    }
    const uint16_t* data = (map->layers_mut != NULL) ? map->layers_mut : map->layers_ro;
    if (data == NULL) {
        return 0;
    }
    size_t idx = (size_t)layer * (size_t)map->cols * (size_t)map->rows +
                 (size_t)row * (size_t)map->cols + (size_t)col;
    return (int32_t)(data[idx] & PSPS_TILE_ID_MASK);
}

void psps_baked_tilemap_set_tile(psps_baked_tilemap_t* map, int32_t layer, int32_t col, int32_t row, int32_t tile_id) {
    if (map == NULL || map->layers_mut == NULL ||
        layer < 0 || layer >= (int32_t)map->layer_count ||
        col < 0 || col >= (int32_t)map->cols || row < 0 || row >= (int32_t)map->rows) {
        return;
    }
    size_t idx = (size_t)layer * (size_t)map->cols * (size_t)map->rows +
                 (size_t)row * (size_t)map->cols + (size_t)col;
    uint16_t clamped = (tile_id < 0) ? 0u : (uint16_t)((uint32_t)tile_id & PSPS_TILE_ID_MASK);
    map->layers_mut[idx] = clamped;
}

bool psps_baked_tilemap_is_solid(const psps_baked_tilemap_t* map, int32_t col, int32_t row) {
    if (map == NULL || col < 0 || col >= (int32_t)map->cols || row < 0 || row >= (int32_t)map->rows) {
        return false;
    }
    const uint8_t* col_data = (map->collision_mut != NULL) ? map->collision_mut : map->collision_ro;
    if (col_data == NULL) {
        return false;
    }
    return col_data[(size_t)row * (size_t)map->cols + (size_t)col] != 0u;
}

void psps_baked_tilemap_set_solid(psps_baked_tilemap_t* map, int32_t col, int32_t row, bool solid) {
    if (map == NULL || map->collision_mut == NULL ||
        col < 0 || col >= (int32_t)map->cols || row < 0 || row >= (int32_t)map->rows) {
        return;
    }
    map->collision_mut[(size_t)row * (size_t)map->cols + (size_t)col] = solid ? 1u : 0u;
}

bool psps_baked_tilemap_collides_rect(const psps_baked_tilemap_t* map, rect_t hitbox) {
    if (map == NULL || map->tile_size == 0 || hitbox.w <= 0.0f || hitbox.h <= 0.0f) {
        return false;
    }
    float ts = (float)map->tile_size;
    int32_t min_col = (int32_t)floorf(hitbox.x / ts);
    int32_t max_col = (int32_t)floorf((hitbox.x + hitbox.w - 0.001f) / ts);
    int32_t min_row = (int32_t)floorf(hitbox.y / ts);
    int32_t max_row = (int32_t)floorf((hitbox.y + hitbox.h - 0.001f) / ts);

    for (int32_t r = min_row; r <= max_row; ++r) {
        for (int32_t c = min_col; c <= max_col; ++c) {
            if (psps_baked_tilemap_is_solid(map, c, r)) {
                return true;
            }
        }
    }
    return false;
}

void psps_baked_tilemap_reset(psps_baked_tilemap_t* map) {
    if (map == NULL) {
        return;
    }
    size_t cell_count = (size_t)map->cols * (size_t)map->rows;
    if (map->layers_mut != NULL && map->layers_ro != NULL && map->layer_count > 0u) {
        memcpy(map->layers_mut, map->layers_ro, (size_t)map->layer_count * cell_count * sizeof(uint16_t));
    }
    if (map->collision_mut != NULL && map->collision_ro != NULL) {
        memcpy(map->collision_mut, map->collision_ro, cell_count * sizeof(uint8_t));
    }
}

void psps_draw_rect(rect_t bounds, psps_color_t color_abgr) {
    if (g_psps_video.renderer == NULL) {
        return;
    }

    vec2_t screen = psps_video_apply_camera(vec2_make(bounds.x, bounds.y));

    SDL_SetRenderDrawColor(
        g_psps_video.renderer,
        psps_color_r(color_abgr),
        psps_color_g(color_abgr),
        psps_color_b(color_abgr),
        psps_color_a(color_abgr)
    );

    SDL_Rect rect = {
        .x = (int)screen.x,
        .y = (int)screen.y,
        .w = (int)bounds.w,
        .h = (int)bounds.h
    };

    SDL_RenderFillRect(g_psps_video.renderer, &rect);
}

void psps_draw_rect_outline(rect_t bounds, psps_color_t color_abgr) {
    if (g_psps_video.renderer == NULL) {
        return;
    }

    vec2_t screen = psps_video_apply_camera(vec2_make(bounds.x, bounds.y));

    SDL_SetRenderDrawColor(
        g_psps_video.renderer,
        psps_color_r(color_abgr),
        psps_color_g(color_abgr),
        psps_color_b(color_abgr),
        psps_color_a(color_abgr)
    );

    SDL_Rect rect = {
        .x = (int)screen.x,
        .y = (int)screen.y,
        .w = (int)bounds.w,
        .h = (int)bounds.h
    };

    SDL_RenderDrawRect(g_psps_video.renderer, &rect);
}

void psps_draw_text_scaled(const char* text, vec2_t pos, int scale, psps_color_t color_abgr) {
    if (g_psps_video.renderer == NULL || g_psps_video.font_atlas == NULL || text == NULL) {
        return;
    }

    if (scale < 1) {
        scale = 1;
    }

    SDL_SetTextureColorMod(
        g_psps_video.font_atlas,
        psps_color_r(color_abgr),
        psps_color_g(color_abgr),
        psps_color_b(color_abgr)
    );
    SDL_SetTextureAlphaMod(g_psps_video.font_atlas, psps_color_a(color_abgr));

    int glyph_size = 8 * scale;
    int start_x = (int)pos.x;
    int cursor_x = start_x;
    int cursor_y = (int)pos.y;

    for (const char* p = text; *p != '\0'; ++p) {
        unsigned char ch = (unsigned char)*p;
        if (ch == '\n') {
            cursor_x = start_x;
            cursor_y += glyph_size + scale;
            continue;
        }
        if (ch < 0x20u || ch > 0x7Eu) {
            ch = (unsigned char)'?';
        }

        int glyph = (int)(ch - 0x20u);
        SDL_Rect src = {
            .x = (glyph % 16) * 8,
            .y = (glyph / 16) * 8,
            .w = 8,
            .h = 8
        };

        SDL_Rect dst = {
            .x = cursor_x,
            .y = cursor_y,
            .w = glyph_size,
            .h = glyph_size
        };

        SDL_RenderCopy(g_psps_video.renderer, g_psps_video.font_atlas, &src, &dst);
        cursor_x += glyph_size;
    }
}

void psps_draw_text(const char* text, vec2_t pos, psps_color_t color_abgr) {
    psps_draw_text_scaled(text, pos, 1, color_abgr);
}

void psps_draw_number(int32_t value, vec2_t pos, psps_color_t color_abgr, int scale) {
    char buf[16];
    snprintf(buf, sizeof(buf), "%d", (int)value);
    psps_draw_text_scaled(buf, pos, scale, color_abgr);
}

#endif /* PSPS_VIDEO_IMPL_INCLUDED */
#endif /* PSPS_VIDEO_IMPLEMENTATION */
