#include <stdint.h>
#include <windows.h>
#include "beacon_compatibility.h"

static const char* g_labels[3] = { "alpha", "beta", "gamma" };
static int g_values[3] = { 10, 20, 12 };

static int add_values(int a, int b) {
    return a + b;
}

static int sum_all(void) {
    int total = 0;
    int i;
    for (i = 0; i < 3; i++) {
        total = add_values(total, g_values[i]);
    }
    return total;
}

static const char* label_for(int idx) {
    return g_labels[idx];
}

void go(char *args, int len) {
    int total = sum_all();
    const char* first_label = label_for(0);
    BeaconPrintf(CALLBACK_OUTPUT, "RELOC_OK:%d:%s", total, first_label);
}
