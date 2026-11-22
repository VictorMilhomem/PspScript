#ifndef PSPS_INPUT_H
#define PSPS_INPUT_H

#include <stdint.h>
#include <stdbool.h>
#include <SDL2/SDL.h>
#include "psps_utils.h"
#include "psps_types.h"

/* ============================================================================
 * PSPScript Controller & Analog Stick Input Module
 * ============================================================================ */

typedef enum psps_button {
    PSPS_BTN_CROSS    = (1u << 0),
    PSPS_BTN_CIRCLE   = (1u << 1),
    PSPS_BTN_SQUARE   = (1u << 2),
    PSPS_BTN_TRIANGLE = (1u << 3),
    PSPS_BTN_UP       = (1u << 4),
    PSPS_BTN_DOWN     = (1u << 5),
    PSPS_BTN_LEFT     = (1u << 6),
    PSPS_BTN_RIGHT    = (1u << 7),
    PSPS_BTN_L        = (1u << 8),
    PSPS_BTN_R        = (1u << 9),
    PSPS_BTN_START    = (1u << 10),
    PSPS_BTN_SELECT   = (1u << 11)
} psps_button_t;

bool   psps_input_init(void);
void   psps_input_shutdown(void);
void   psps_input_update(void);

bool   psps_input_down(uint32_t button_mask);
bool   psps_input_pressed(uint32_t button_mask);
bool   psps_input_released(uint32_t button_mask);
vec2_t psps_input_analog(void);
bool   psps_input_should_quit(void);

#endif /* PSPS_INPUT_H */

#ifdef PSPS_INPUT_IMPLEMENTATION
#ifndef PSPS_INPUT_IMPL_INCLUDED
#define PSPS_INPUT_IMPL_INCLUDED

typedef struct psps_input_state {
    SDL_GameController* controller;
    uint32_t            current_buttons;
    uint32_t            previous_buttons;
    vec2_t              analog_stick;
    bool                quit_requested;
    bool                initialized;
} psps_input_state_t;

static psps_input_state_t g_psps_input = {
    .controller       = NULL,
    .current_buttons  = 0u,
    .previous_buttons = 0u,
    .analog_stick     = { .x = 0.0f, .y = 0.0f },
    .quit_requested   = false,
    .initialized      = false
};

static void psps_input_open_first_controller(void) {
    if (g_psps_input.controller != NULL) {
        return;
    }

    int num_joysticks = SDL_NumJoysticks();
    for (int i = 0; i < num_joysticks; ++i) {
        if (SDL_IsGameController(i)) {
            g_psps_input.controller = SDL_GameControllerOpen(i);
            if (g_psps_input.controller != NULL) {
                break;
            }
        }
    }
}

static vec2_t psps_input_filter_deadzone(float raw_x, float raw_y, float deadzone) {
    vec2_t v = vec2_make(raw_x, raw_y);
    float mag = vec2_len(v);

    if (mag <= deadzone) {
        return vec2_make(0.0f, 0.0f);
    }

    /* Smooth radial deadzone rescaling from [deadzone, 1.0] -> [0.0, 1.0] */
    float normalized_mag = (mag - deadzone) / (1.0f - deadzone);
    if (normalized_mag > 1.0f) {
        normalized_mag = 1.0f;
    }

    float scale = normalized_mag / mag;
    return vec2_make(v.x * scale, v.y * scale);
}

bool psps_input_init(void) {
    if (g_psps_input.initialized) {
        return true;
    }

    uint32_t flags = SDL_INIT_EVENTS | SDL_INIT_GAMECONTROLLER | SDL_INIT_JOYSTICK;
    if ((SDL_WasInit(flags) & flags) != flags) {
        if (SDL_InitSubSystem(flags) < 0) {
            /* Events alone still allow desktop keyboard input if controller init fails */
            SDL_InitSubSystem(SDL_INIT_EVENTS);
        }
    }

    g_psps_input.current_buttons  = 0u;
    g_psps_input.previous_buttons = 0u;
    g_psps_input.analog_stick     = vec2_make(0.0f, 0.0f);
    g_psps_input.quit_requested   = false;
    g_psps_input.initialized      = true;

    psps_input_open_first_controller();
    return true;
}

void psps_input_shutdown(void) {
    if (!g_psps_input.initialized) {
        return;
    }

    if (g_psps_input.controller != NULL) {
        SDL_GameControllerClose(g_psps_input.controller);
        g_psps_input.controller = NULL;
    }

    g_psps_input.initialized = false;
}

void psps_input_update(void) {
    g_psps_input.previous_buttons = g_psps_input.current_buttons;

    SDL_Event ev;
    while (SDL_PollEvent(&ev)) {
        switch (ev.type) {
            case SDL_QUIT:
                g_psps_input.quit_requested = true;
                break;

            case SDL_CONTROLLERDEVICEADDED:
                if (g_psps_input.controller == NULL && SDL_IsGameController(ev.cdevice.which)) {
                    g_psps_input.controller = SDL_GameControllerOpen(ev.cdevice.which);
                }
                break;

            case SDL_CONTROLLERDEVICEREMOVED:
                if (g_psps_input.controller != NULL) {
                    SDL_Joystick* joy = SDL_GameControllerGetJoystick(g_psps_input.controller);
                    if (joy != NULL && SDL_JoystickInstanceID(joy) == ev.cdevice.which) {
                        SDL_GameControllerClose(g_psps_input.controller);
                        g_psps_input.controller = NULL;
                        psps_input_open_first_controller();
                    }
                }
                break;

            default:
                break;
        }
    }

    uint32_t mask = 0u;
    float raw_ax  = 0.0f;
    float raw_ay  = 0.0f;

    /* 1. Read hardware controller / PSP buttons & analog nub */
    if (g_psps_input.controller != NULL) {
        SDL_GameController* pad = g_psps_input.controller;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_A))             mask |= PSPS_BTN_CROSS;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_B))             mask |= PSPS_BTN_CIRCLE;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_X))             mask |= PSPS_BTN_SQUARE;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_Y))             mask |= PSPS_BTN_TRIANGLE;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_DPAD_UP))       mask |= PSPS_BTN_UP;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_DPAD_DOWN))     mask |= PSPS_BTN_DOWN;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_DPAD_LEFT))     mask |= PSPS_BTN_LEFT;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_DPAD_RIGHT))    mask |= PSPS_BTN_RIGHT;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_LEFTSHOULDER))  mask |= PSPS_BTN_L;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_RIGHTSHOULDER)) mask |= PSPS_BTN_R;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_START))         mask |= PSPS_BTN_START;
        if (SDL_GameControllerGetButton(pad, SDL_CONTROLLER_BUTTON_BACK))          mask |= PSPS_BTN_SELECT;

        int16_t axis_x = SDL_GameControllerGetAxis(pad, SDL_CONTROLLER_AXIS_LEFTX);
        int16_t axis_y = SDL_GameControllerGetAxis(pad, SDL_CONTROLLER_AXIS_LEFTY);
        raw_ax = (float)axis_x / 32767.0f;
        raw_ay = (float)axis_y / 32767.0f;
    }

    /* 2. Desktop keyboard fallback for fast local testing */
    int num_keys = 0;
    const uint8_t* keys = SDL_GetKeyboardState(&num_keys);
    if (keys != NULL) {
        if (keys[SDL_SCANCODE_Z] || keys[SDL_SCANCODE_J] || keys[SDL_SCANCODE_SPACE]) mask |= PSPS_BTN_CROSS;
        if (keys[SDL_SCANCODE_X] || keys[SDL_SCANCODE_K])                             mask |= PSPS_BTN_CIRCLE;
        if (keys[SDL_SCANCODE_A] || keys[SDL_SCANCODE_U])                             mask |= PSPS_BTN_SQUARE;
        if (keys[SDL_SCANCODE_S] || keys[SDL_SCANCODE_I])                             mask |= PSPS_BTN_TRIANGLE;
        if (keys[SDL_SCANCODE_UP])                                                    mask |= PSPS_BTN_UP;
        if (keys[SDL_SCANCODE_DOWN])                                                  mask |= PSPS_BTN_DOWN;
        if (keys[SDL_SCANCODE_LEFT])                                                  mask |= PSPS_BTN_LEFT;
        if (keys[SDL_SCANCODE_RIGHT])                                                 mask |= PSPS_BTN_RIGHT;
        if (keys[SDL_SCANCODE_Q])                                                     mask |= PSPS_BTN_L;
        if (keys[SDL_SCANCODE_W] || keys[SDL_SCANCODE_E])                             mask |= PSPS_BTN_R;
        if (keys[SDL_SCANCODE_RETURN])                                                mask |= PSPS_BTN_START;
        if (keys[SDL_SCANCODE_BACKSPACE] || keys[SDL_SCANCODE_RSHIFT])                mask |= PSPS_BTN_SELECT;
        if (keys[SDL_SCANCODE_ESCAPE])                                                g_psps_input.quit_requested = true;
    }

    if (raw_ax < -1.0f) raw_ax = -1.0f;
    if (raw_ax >  1.0f) raw_ax =  1.0f;
    if (raw_ay < -1.0f) raw_ay = -1.0f;
    if (raw_ay >  1.0f) raw_ay =  1.0f;

    g_psps_input.current_buttons = mask;
    g_psps_input.analog_stick    = psps_input_filter_deadzone(raw_ax, raw_ay, ANALOG_DEADZONE);
}

bool psps_input_down(uint32_t button_mask) {
    return (g_psps_input.current_buttons & button_mask) != 0u;
}

bool psps_input_pressed(uint32_t button_mask) {
    uint32_t newly_pressed = g_psps_input.current_buttons & ~g_psps_input.previous_buttons;
    return (newly_pressed & button_mask) != 0u;
}

bool psps_input_released(uint32_t button_mask) {
    uint32_t newly_released = ~g_psps_input.current_buttons & g_psps_input.previous_buttons;
    return (newly_released & button_mask) != 0u;
}

vec2_t psps_input_analog(void) {
    return g_psps_input.analog_stick;
}

bool psps_input_should_quit(void) {
    return g_psps_input.quit_requested;
}

#endif /* PSPS_INPUT_IMPL_INCLUDED */
#endif /* PSPS_INPUT_IMPLEMENTATION */
