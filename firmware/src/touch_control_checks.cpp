#include "touch_control.h"

static_assert(controlAt(-1, 208) == -1, "Negative x is outside controls");
static_assert(controlAt(320, 208) == -1, "Right edge is outside controls");
static_assert(controlAt(0, 189) == -1, "Above buttons is outside controls");
static_assert(controlAt(0, 238) == -1, "Below buttons is outside controls");
static_assert(controlAt(0, -1) == -1, "Negative y is outside controls");
static_assert(controlAt(0, 240) == -1, "Below display is outside controls");
static_assert(controlAt(0, 190) == 0, "START top left");
static_assert(controlAt(79, 237) == 0, "START bottom right");
static_assert(controlAt(80, 190) == 1, "PHOTO top left");
static_assert(controlAt(159, 237) == 1, "PHOTO bottom right");
static_assert(controlAt(160, 190) == 2, "NEW top left");
static_assert(controlAt(239, 237) == 2, "NEW bottom right");
static_assert(controlAt(240, 190) == 3, "BLE top left");
static_assert(controlAt(319, 237) == 3, "BLE bottom right");
