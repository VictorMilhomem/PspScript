#ifndef PSPS_AUDIO_H
#define PSPS_AUDIO_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

/* ============================================================================
 * PSPScript Zero-Heap SDL2 Software Audio Mixer (44.1 kHz, 16-bit Stereo)
 * ============================================================================
 * Fixed 8 concurrent SFX voices (oldest-voice stealing) + 1 dedicated looping
 * BGM channel. Works identically on Sony PSP (__PSP__ via SDL2 sceAudio) and
 * desktop Linux/Windows with zero dynamic heap allocations.
 * ============================================================================ */

#define PSPS_AUDIO_SAMPLE_RATE    44100
#define PSPS_AUDIO_MAX_VOICES     8u
#define PSPS_AUDIO_BUFFER_SAMPLES 1024

typedef struct psps_sound {
    const int16_t* samples;     /* Interleaved 16-bit signed PCM in .rodata      */
    uint32_t       frame_count; /* Number of PCM frames (samples / channels)     */
    uint8_t        channels;    /* 1 = mono, 2 = stereo                          */
    uint8_t        half_rate;   /* 0 = 44100 Hz, 1 = 22050 Hz (2x stepped)       */
} psps_sound_t;

bool psps_audio_init(void);
void psps_audio_shutdown(void);

void psps_audio_play_sound(const psps_sound_t* sfx, float volume);
void psps_audio_stop_all_sounds(void);

void psps_audio_play_music(const psps_sound_t* bgm, float volume);
void psps_audio_stop_music(void);
void psps_audio_pause_music(void);
void psps_audio_resume_music(void);

void psps_audio_set_master_volume(float volume);

#endif /* PSPS_AUDIO_H */

#ifdef PSPS_AUDIO_IMPLEMENTATION
#ifndef PSPS_AUDIO_IMPL_INCLUDED
#define PSPS_AUDIO_IMPL_INCLUDED

#include <string.h>
#include <stdio.h>
#ifndef PSPS_AUDIO_NO_SDL
#include <SDL2/SDL.h>
#endif

typedef struct psps_audio_voice {
    const psps_sound_t* sound;
    uint32_t            cursor;    /* Output-rate frame counter */
    uint32_t            age;       /* Monotonic start timestamp for stealing */
    uint16_t            gain;      /* Fixed-point 0..256 (0.0..1.0) */
    bool                active;
    bool                looping;
    bool                paused;
} psps_audio_voice_t;

static struct {
#ifndef PSPS_AUDIO_NO_SDL
    SDL_AudioDeviceID  device_id;
#endif
    bool               initialized;
    uint16_t           master_gain; /* Fixed-point 0..256 */
    uint32_t           age_counter;
    psps_audio_voice_t voices[PSPS_AUDIO_MAX_VOICES];
    psps_audio_voice_t bgm;
} g_psps_audio;

static inline uint16_t psps_audio_float_to_gain(float volume) {
    if (volume <= 0.0f) return 0u;
    if (volume >= 1.0f) return 256u;
    return (uint16_t)(volume * 256.0f + 0.5f);
}

static inline int16_t psps_audio_clamp_i16(int32_t sample) {
    if (sample > 32767) return 32767;
    if (sample < -32768) return -32768;
    return (int16_t)sample;
}

static void psps_audio_mix_voice(
    psps_audio_voice_t* v,
    int32_t*            accum_l,
    int32_t*            accum_r,
    uint32_t            frame_idx)
{
    (void)frame_idx;
    if (!v->active || v->paused || v->sound == NULL ||
        v->sound->samples == NULL || v->sound->frame_count == 0u) {
        return;
    }

    const psps_sound_t* snd = v->sound;
    uint32_t src_frame = (snd->half_rate != 0u) ? (v->cursor >> 1) : v->cursor;

    if (src_frame >= snd->frame_count) {
        if (v->looping) {
            v->cursor = 0u;
            src_frame = 0u;
        } else {
            v->active = false;
            return;
        }
    }

    int32_t sample_l;
    int32_t sample_r;
    if (snd->channels == 2u) {
        size_t base = (size_t)src_frame * 2u;
        sample_l = (int32_t)snd->samples[base];
        sample_r = (int32_t)snd->samples[base + 1u];
    } else {
        sample_l = (int32_t)snd->samples[src_frame];
        sample_r = sample_l;
    }

    *accum_l += (sample_l * (int32_t)v->gain) >> 8;
    *accum_r += (sample_r * (int32_t)v->gain) >> 8;

    v->cursor += 1u;
    uint32_t next_frame = (snd->half_rate != 0u) ? (v->cursor >> 1) : v->cursor;
    if (next_frame >= snd->frame_count) {
        if (v->looping) {
            v->cursor = 0u;
        } else {
            v->active = false;
        }
    }
}

static void psps_audio_mix_buffer(int16_t* out_stereo, uint32_t frame_count) {
    uint16_t master = g_psps_audio.master_gain;

    for (uint32_t f = 0u; f < frame_count; ++f) {
        int32_t mix_l = 0;
        int32_t mix_r = 0;

        for (uint32_t i = 0u; i < PSPS_AUDIO_MAX_VOICES; ++i) {
            if (g_psps_audio.voices[i].active) {
                psps_audio_mix_voice(&g_psps_audio.voices[i], &mix_l, &mix_r, f);
            }
        }

        if (g_psps_audio.bgm.active && !g_psps_audio.bgm.paused) {
            psps_audio_mix_voice(&g_psps_audio.bgm, &mix_l, &mix_r, f);
        }

        mix_l = (mix_l * (int32_t)master) >> 8;
        mix_r = (mix_r * (int32_t)master) >> 8;

        out_stereo[f * 2u]      = psps_audio_clamp_i16(mix_l);
        out_stereo[f * 2u + 1u] = psps_audio_clamp_i16(mix_r);
    }
}

#ifndef PSPS_AUDIO_NO_SDL
static void psps_audio_callback(void* userdata, Uint8* stream, int len) {
    (void)userdata;
    if (stream == NULL || len <= 0) {
        return;
    }
    uint32_t frames = (uint32_t)(len / (int)(sizeof(int16_t) * 2u));
    psps_audio_mix_buffer((int16_t*)stream, frames);
}
#endif

static inline void psps_audio_lock(void) {
#ifndef PSPS_AUDIO_NO_SDL
    if (g_psps_audio.device_id != 0u) {
        SDL_LockAudioDevice(g_psps_audio.device_id);
    }
#endif
}

static inline void psps_audio_unlock(void) {
#ifndef PSPS_AUDIO_NO_SDL
    if (g_psps_audio.device_id != 0u) {
        SDL_UnlockAudioDevice(g_psps_audio.device_id);
    }
#endif
}

bool psps_audio_init(void) {
    memset(&g_psps_audio, 0, sizeof(g_psps_audio));
    g_psps_audio.master_gain = 256u;

#ifndef PSPS_AUDIO_NO_SDL
    if ((SDL_WasInit(SDL_INIT_AUDIO) & SDL_INIT_AUDIO) == 0u) {
        if (SDL_InitSubSystem(SDL_INIT_AUDIO) < 0) {
            fprintf(stderr, "psps_audio_init: SDL_InitSubSystem(SDL_INIT_AUDIO) failed: %s\n", SDL_GetError());
            return false;
        }
    }

    SDL_AudioSpec desired;
    memset(&desired, 0, sizeof(desired));
    desired.freq     = PSPS_AUDIO_SAMPLE_RATE;
    desired.format   = AUDIO_S16SYS;
    desired.channels = 2;
    desired.samples  = PSPS_AUDIO_BUFFER_SAMPLES;
    desired.callback = psps_audio_callback;

    g_psps_audio.device_id = SDL_OpenAudioDevice(NULL, 0, &desired, NULL, 0);
    if (g_psps_audio.device_id == 0u) {
        fprintf(stderr, "psps_audio_init: SDL_OpenAudioDevice failed: %s\n", SDL_GetError());
        return false;
    }

    SDL_PauseAudioDevice(g_psps_audio.device_id, 0);
#endif

    g_psps_audio.initialized = true;
    return true;
}

void psps_audio_shutdown(void) {
#ifndef PSPS_AUDIO_NO_SDL
    if (g_psps_audio.device_id != 0u) {
        SDL_CloseAudioDevice(g_psps_audio.device_id);
        g_psps_audio.device_id = 0u;
    }
    if ((SDL_WasInit(SDL_INIT_AUDIO) & SDL_INIT_AUDIO) != 0u) {
        SDL_QuitSubSystem(SDL_INIT_AUDIO);
    }
#endif
    g_psps_audio.initialized = false;
}

void psps_audio_play_sound(const psps_sound_t* sfx, float volume) {
    if (!g_psps_audio.initialized || sfx == NULL || sfx->samples == NULL || sfx->frame_count == 0u) {
        return;
    }

    psps_audio_lock();

    uint32_t chosen = 0u;
    bool found_free = false;
    uint32_t oldest_age = 0xFFFFFFFFu;

    for (uint32_t i = 0u; i < PSPS_AUDIO_MAX_VOICES; ++i) {
        if (!g_psps_audio.voices[i].active) {
            chosen = i;
            found_free = true;
            break;
        }
        if (g_psps_audio.voices[i].age < oldest_age) {
            oldest_age = g_psps_audio.voices[i].age;
            chosen = i;
        }
    }

    (void)found_free;
    psps_audio_voice_t* v = &g_psps_audio.voices[chosen];
    v->sound   = sfx;
    v->cursor  = 0u;
    v->age     = ++g_psps_audio.age_counter;
    v->gain    = psps_audio_float_to_gain(volume);
    v->active  = true;
    v->looping = false;
    v->paused  = false;

    psps_audio_unlock();
}

void psps_audio_stop_all_sounds(void) {
    if (!g_psps_audio.initialized) {
        return;
    }
    psps_audio_lock();
    for (uint32_t i = 0u; i < PSPS_AUDIO_MAX_VOICES; ++i) {
        g_psps_audio.voices[i].active = false;
    }
    psps_audio_unlock();
}

void psps_audio_play_music(const psps_sound_t* bgm, float volume) {
    if (!g_psps_audio.initialized || bgm == NULL || bgm->samples == NULL || bgm->frame_count == 0u) {
        return;
    }
    psps_audio_lock();
    g_psps_audio.bgm.sound   = bgm;
    g_psps_audio.bgm.cursor  = 0u;
    g_psps_audio.bgm.age     = ++g_psps_audio.age_counter;
    g_psps_audio.bgm.gain    = psps_audio_float_to_gain(volume);
    g_psps_audio.bgm.active  = true;
    g_psps_audio.bgm.looping = true;
    g_psps_audio.bgm.paused  = false;
    psps_audio_unlock();
}

void psps_audio_stop_music(void) {
    if (!g_psps_audio.initialized) {
        return;
    }
    psps_audio_lock();
    g_psps_audio.bgm.active = false;
    g_psps_audio.bgm.paused = false;
    g_psps_audio.bgm.cursor = 0u;
    psps_audio_unlock();
}

void psps_audio_pause_music(void) {
    if (!g_psps_audio.initialized) {
        return;
    }
    psps_audio_lock();
    if (g_psps_audio.bgm.active) {
        g_psps_audio.bgm.paused = true;
    }
    psps_audio_unlock();
}

void psps_audio_resume_music(void) {
    if (!g_psps_audio.initialized) {
        return;
    }
    psps_audio_lock();
    if (g_psps_audio.bgm.active) {
        g_psps_audio.bgm.paused = false;
    }
    psps_audio_unlock();
}

void psps_audio_set_master_volume(float volume) {
    psps_audio_lock();
    g_psps_audio.master_gain = psps_audio_float_to_gain(volume);
    psps_audio_unlock();
}

#endif /* PSPS_AUDIO_IMPL_INCLUDED */
#endif /* PSPS_AUDIO_IMPLEMENTATION */
