# ==============================================================================
# PSPScript Cross-Compilation Makefile (PSPSDK + SDL2 -> EBOOT.PBP)
# ==============================================================================

BUILD_DIR        ?= build
RUNTIME_DIR      ?= Runtime
RUNTIME_PARENT   := $(patsubst %/,%,$(dir $(abspath $(RUNTIME_DIR))))
SRC              ?= $(BUILD_DIR)/out.c
OBJ              := $(BUILD_DIR)/out.o
ELF              := $(BUILD_DIR)/out.elf
PRX              := $(BUILD_DIR)/out.prx
SFO              := $(BUILD_DIR)/PARAM.SFO
EBOOT            := $(BUILD_DIR)/EBOOT.PBP

PSP_EBOOT_TITLE  ?= PSPScript Game
PSP_GAME_ID      ?= PSPS00001
PSP_APP_VER      ?= 01.00
PSP_MEMSIZE      ?= 1
PSP_FW_VERSION   ?= 600

# Optional PBP metadata assets (NULL when unused)
PSP_EBOOT_ICON   ?= NULL
PSP_EBOOT_ICON1  ?= NULL
PSP_EBOOT_UNKPNG ?= NULL
PSP_EBOOT_PIC1   ?= NULL
PSP_EBOOT_SND0   ?= NULL
PSP_EBOOT_PSAR   ?= NULL

# PSPSDK & PSPDEV paths resolved inside pspdev/pspdev container
PSPSDK           := $(shell psp-config --pspsdk-path 2>/dev/null)
PSPDEV           := $(shell psp-config --pspdev-path 2>/dev/null)

CC               := psp-gcc
FIXUP            := psp-fixup-imports
PRXGEN           := psp-prxgen
MKSFO            := mksfoex
PACK_PBP         := pack-pbp
STRIP            := psp-strip

# Query PSP sdl2-config when running inside the pspdev container
PSP_SDL2_CFLAGS  := $(shell $(PSPDEV)/psp/bin/sdl2-config --cflags 2>/dev/null)
PSP_SDL2_LIBS    := $(shell $(PSPDEV)/psp/bin/sdl2-config --libs 2>/dev/null)

CFLAGS           := -std=c99 -O2 -G0 -Wall -Wextra \
                    -D__PSP__ \
                    -D_PSP_FW_VERSION=$(PSP_FW_VERSION) \
                    -I. \
                    -I$(RUNTIME_DIR) \
                    -I$(RUNTIME_PARENT) \
                    $(PSP_SDL2_CFLAGS) \
                    -I$(PSPDEV)/psp/include \
                    -I$(PSPDEV)/psp/include/SDL2 \
                    -I$(PSPSDK)/include

LDFLAGS          := -L$(PSPDEV)/psp/lib \
                    -L$(PSPSDK)/lib \
                    -specs=$(PSPSDK)/lib/prxspecs \
                    -Wl,-q,-T$(PSPSDK)/lib/linkfile.prx

LIBS             := $(if $(PSP_SDL2_LIBS),$(PSP_SDL2_LIBS),-lSDL2main -lSDL2) \
                    -lGL -lpspgu -lpspgum -lpspvfpu -lpspvram \
                    -lpspaudio -lpspctrl -lpspdisplay -lpspge \
                    -lpsprtc -lpsppower -lpspdebug -lpsphprm -lpsputility \
                    -lm -lc -lpspuser -lpspkernel

# Host desktop compiler settings for fast local testing (`make desktop`)
HOST_CC          ?= gcc
HOST_BIN         := $(BUILD_DIR)/game_desktop
HOST_CFLAGS      := -std=c99 -O2 -Wall -Wextra -I. -I$(RUNTIME_DIR) -I$(RUNTIME_PARENT) $(shell pkg-config --cflags sdl2 2>/dev/null)
HOST_LIBS        := $(shell pkg-config --libs sdl2 2>/dev/null || echo -lSDL2) -lm

.PHONY: all psp desktop clean

all: psp

psp: $(EBOOT)

$(BUILD_DIR):
	mkdir -p $(BUILD_DIR)

$(OBJ): $(SRC) | $(BUILD_DIR)
	$(CC) $(CFLAGS) -c $(SRC) -o $(OBJ)

$(ELF): $(OBJ)
	$(CC) $(CFLAGS) $(LDFLAGS) $(OBJ) $(PSPSDK)/lib/prxexports.o $(LIBS) -o $(ELF)
	$(FIXUP) $(ELF)

$(PRX): $(ELF)
	$(PRXGEN) $(ELF) $(PRX)

MKSFO_MEM_FLAG   := $(if $(filter 1,$(PSP_MEMSIZE)),-d MEMSIZE=1,)

$(SFO): $(SRC) | $(BUILD_DIR)
	$(MKSFO) $(MKSFO_MEM_FLAG) -s DISC_ID=$(PSP_GAME_ID) -s APP_VER=$(PSP_APP_VER) "$(PSP_EBOOT_TITLE)" $(SFO)

$(EBOOT): $(PRX) $(SFO)
	$(PACK_PBP) $(EBOOT) $(SFO) $(PSP_EBOOT_ICON) $(PSP_EBOOT_ICON1) \
		$(PSP_EBOOT_UNKPNG) $(PSP_EBOOT_PIC1) $(PSP_EBOOT_SND0) \
		$(PRX) $(PSP_EBOOT_PSAR)
	@echo "Successfully packaged PSP homebrew executable: $(EBOOT)"

desktop: $(SRC) | $(BUILD_DIR)
	$(HOST_CC) $(HOST_CFLAGS) $(SRC) $(HOST_LIBS) -o $(HOST_BIN)
	@echo "Successfully built desktop preview executable: $(HOST_BIN)"

clean:
	rm -f $(OBJ) $(ELF) $(PRX) $(SFO) $(EBOOT) $(HOST_BIN)
