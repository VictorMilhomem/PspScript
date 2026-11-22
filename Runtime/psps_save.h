#ifndef PSPS_SAVE_H
#define PSPS_SAVE_H

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include <string.h>
#include <stdio.h>
#include "psps_utils.h"

/* ============================================================================
 * PSPScript Static Save Data Persistence Module (Zero Dynamic Heap)
 * ============================================================================
 * - On PSP (__PSP__): Uses sceUtilitySavedata (AUTOSAVE / AUTOLOAD) to persist
 *   contiguous global state to ms0:/PSP/SAVEDATA/<GAME_ID>0000/DATA.BIN.
 * - On Desktop: Persists the same header + FNV-1a checksummed binary format to
 *   savedata/<GAME_ID>.bin via <stdio.h>.
 * ============================================================================ */

#define PSPS_SAVE_MAGIC        ((uint32_t)0x53505350u) /* "PSPS" */
#define PSPS_SAVE_VERSION      ((uint16_t)1u)
#define PSPS_SAVE_MAX_PAYLOAD  ((size_t)16384u)

typedef struct psps_save_header {
    uint32_t magic;
    uint16_t version;
    uint16_t reserved;
    uint32_t payload_size;
    uint32_t checksum_fnv1a;
} psps_save_header_t;

bool psps_save_write(const char* game_id, const char* title, const void* data, size_t size);
bool psps_save_read(const char* game_id, void* out_data, size_t expected_size);

#endif /* PSPS_SAVE_H */

#ifdef PSPS_SAVE_IMPLEMENTATION
#ifndef PSPS_SAVE_IMPL_INCLUDED
#define PSPS_SAVE_IMPL_INCLUDED

#if defined(__PSP__)
    #include <pspkernel.h>
    #include <pspdisplay.h>
    #include <psputility.h>
    #include <psputility_savedata.h>
#else
    #include <sys/stat.h>
    #if defined(_WIN32)
        #include <direct.h>
    #endif
#endif

static uint8_t g_psps_save_io_buf[sizeof(psps_save_header_t) + PSPS_SAVE_MAX_PAYLOAD] PSPS_ALIGNED(16);

static uint32_t psps_save_fnv1a(const uint8_t* bytes, size_t len) {
    uint32_t hash = 2166136261u;
    for (size_t i = 0; i < len; ++i) {
        hash ^= (uint32_t)bytes[i];
        hash *= 16777619u;
    }
    return hash;
}

#if defined(__PSP__)

static SceUtilitySavedataParam g_psps_savedata_param;
static const char g_psps_savedata_key[16] = "PSPSCRIPTSAVEKEY";

static bool psps_save_run_utility(PspUtilitySavedataMode mode, const char* game_id, const char* title, size_t total_size) {
    memset(&g_psps_savedata_param, 0, sizeof(g_psps_savedata_param));
    g_psps_savedata_param.base.size            = sizeof(g_psps_savedata_param);
    g_psps_savedata_param.base.language        = PSP_SYSTEMPARAM_LANGUAGE_ENGLISH;
    g_psps_savedata_param.base.buttonSwap      = PSP_UTILITY_ACCEPT_CROSS;
    g_psps_savedata_param.base.graphicsThread  = 0x11;
    g_psps_savedata_param.base.accessThread    = 0x13;
    g_psps_savedata_param.base.fontThread      = 0x12;
    g_psps_savedata_param.base.soundThread     = 0x10;

    g_psps_savedata_param.mode                 = mode;
    g_psps_savedata_param.overwrite            = 1;
    g_psps_savedata_param.focus                = PSP_UTILITY_SAVEDATA_FOCUS_LATEST;

    const char* safe_id = (game_id != NULL && game_id[0] != '\0') ? game_id : "PSPS00001";
    const char* safe_title = (title != NULL && title[0] != '\0') ? title : "PSPScript Game";

    snprintf(g_psps_savedata_param.gameName, sizeof(g_psps_savedata_param.gameName), "%.9s", safe_id);
    snprintf(g_psps_savedata_param.saveName, sizeof(g_psps_savedata_param.saveName), "0000");
    snprintf(g_psps_savedata_param.fileName, sizeof(g_psps_savedata_param.fileName), "DATA.BIN");

    memcpy(g_psps_savedata_param.key, g_psps_savedata_key, sizeof(g_psps_savedata_key));

    g_psps_savedata_param.dataBuf     = g_psps_save_io_buf;
    g_psps_savedata_param.dataBufSize = (SceSize)sizeof(g_psps_save_io_buf);
    g_psps_savedata_param.dataSize    = (SceSize)total_size;

    if (mode == PSP_UTILITY_SAVEDATA_AUTOSAVE) {
        snprintf(g_psps_savedata_param.sfoParam.title, sizeof(g_psps_savedata_param.sfoParam.title), "%s", safe_title);
        snprintf(g_psps_savedata_param.sfoParam.savedataTitle, sizeof(g_psps_savedata_param.sfoParam.savedataTitle), "Save Data");
        snprintf(g_psps_savedata_param.sfoParam.detail, sizeof(g_psps_savedata_param.sfoParam.detail), "PSPScript Saved State");
        g_psps_savedata_param.sfoParam.parentalLevel = 1;
    }

    if (sceUtilitySavedataInitStart(&g_psps_savedata_param) < 0) {
        return false;
    }

    bool running = true;
    while (running) {
        int status = sceUtilitySavedataGetStatus();
        switch (status) {
            case PSP_UTILITY_DIALOG_VISIBLE:
                sceUtilitySavedataUpdate(1);
                break;
            case PSP_UTILITY_DIALOG_QUIT:
                sceUtilitySavedataShutdownStart();
                break;
            case PSP_UTILITY_DIALOG_FINISHED:
            case PSP_UTILITY_DIALOG_NONE:
                running = false;
                break;
            default:
                break;
        }
        sceDisplayWaitVblankStart();
    }

    return g_psps_savedata_param.base.result == 0;
}

bool psps_save_write(const char* game_id, const char* title, const void* data, size_t size) {
    if (data == NULL || size == 0 || size > PSPS_SAVE_MAX_PAYLOAD) {
        return false;
    }

    psps_save_header_t header = {
        .magic          = PSPS_SAVE_MAGIC,
        .version        = PSPS_SAVE_VERSION,
        .reserved       = 0u,
        .payload_size   = (uint32_t)size,
        .checksum_fnv1a = psps_save_fnv1a((const uint8_t*)data, size)
    };

    memcpy(g_psps_save_io_buf, &header, sizeof(header));
    memcpy(g_psps_save_io_buf + sizeof(header), data, size);

    return psps_save_run_utility(PSP_UTILITY_SAVEDATA_AUTOSAVE, game_id, title, sizeof(header) + size);
}

bool psps_save_read(const char* game_id, void* out_data, size_t expected_size) {
    if (out_data == NULL || expected_size == 0 || expected_size > PSPS_SAVE_MAX_PAYLOAD) {
        return false;
    }

    memset(g_psps_save_io_buf, 0, sizeof(g_psps_save_io_buf));
    if (!psps_save_run_utility(PSP_UTILITY_SAVEDATA_AUTOLOAD, game_id, NULL, sizeof(psps_save_header_t) + expected_size)) {
        return false;
    }

    psps_save_header_t header;
    memcpy(&header, g_psps_save_io_buf, sizeof(header));

    if (header.magic != PSPS_SAVE_MAGIC ||
        header.version != PSPS_SAVE_VERSION ||
        header.payload_size != (uint32_t)expected_size) {
        return false;
    }

    const uint8_t* payload = g_psps_save_io_buf + sizeof(header);
    if (psps_save_fnv1a(payload, expected_size) != header.checksum_fnv1a) {
        return false;
    }

    memcpy(out_data, payload, expected_size);
    return true;
}

#else /* Desktop Linux / Windows fallback */

static void psps_save_format_desktop_path(char* out_path, size_t out_cap, const char* game_id) {
    const char* safe_id = (game_id != NULL && game_id[0] != '\0') ? game_id : "PSPS00001";
    snprintf(out_path, out_cap, "savedata/%s.bin", safe_id);
}

static void psps_save_ensure_desktop_dir(void) {
    #if defined(_WIN32)
        _mkdir("savedata");
    #else
        mkdir("savedata", 0755);
    #endif
}

bool psps_save_write(const char* game_id, const char* title, const void* data, size_t size) {
    (void)title;
    if (data == NULL || size == 0 || size > PSPS_SAVE_MAX_PAYLOAD) {
        return false;
    }

    psps_save_header_t header = {
        .magic          = PSPS_SAVE_MAGIC,
        .version        = PSPS_SAVE_VERSION,
        .reserved       = 0u,
        .payload_size   = (uint32_t)size,
        .checksum_fnv1a = psps_save_fnv1a((const uint8_t*)data, size)
    };

    memcpy(g_psps_save_io_buf, &header, sizeof(header));
    memcpy(g_psps_save_io_buf + sizeof(header), data, size);

    psps_save_ensure_desktop_dir();
    char path[128];
    psps_save_format_desktop_path(path, sizeof(path), game_id);

    FILE* fp = fopen(path, "wb");
    if (fp == NULL) {
        return false;
    }

    size_t total = sizeof(header) + size;
    size_t written = fwrite(g_psps_save_io_buf, 1, total, fp);
    fclose(fp);
    return written == total;
}

bool psps_save_read(const char* game_id, void* out_data, size_t expected_size) {
    if (out_data == NULL || expected_size == 0 || expected_size > PSPS_SAVE_MAX_PAYLOAD) {
        return false;
    }

    char path[128];
    psps_save_format_desktop_path(path, sizeof(path), game_id);

    FILE* fp = fopen(path, "rb");
    if (fp == NULL) {
        return false;
    }

    size_t total = sizeof(psps_save_header_t) + expected_size;
    size_t read_bytes = fread(g_psps_save_io_buf, 1, total, fp);
    fclose(fp);

    if (read_bytes != total) {
        return false;
    }

    psps_save_header_t header;
    memcpy(&header, g_psps_save_io_buf, sizeof(header));

    if (header.magic != PSPS_SAVE_MAGIC ||
        header.version != PSPS_SAVE_VERSION ||
        header.payload_size != (uint32_t)expected_size) {
        return false;
    }

    const uint8_t* payload = g_psps_save_io_buf + sizeof(header);
    if (psps_save_fnv1a(payload, expected_size) != header.checksum_fnv1a) {
        return false;
    }

    memcpy(out_data, payload, expected_size);
    return true;
}

#endif /* __PSP__ */

#endif /* PSPS_SAVE_IMPL_INCLUDED */
#endif /* PSPS_SAVE_IMPLEMENTATION */
