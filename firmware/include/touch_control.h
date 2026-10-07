#pragma once

constexpr int controlAt(int x, int y) {
    return x >= 0 && x < 320 && y >= 190 && y < 238 ? x / 80 : -1;
}
