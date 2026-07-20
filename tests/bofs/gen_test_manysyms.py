#!/usr/bin/env python3
"""Generates test_manysyms.c: N repeated calls to a single resolvable
external symbol, to regression-test the function-mapping table limit
(commit bd1268e raised it from 256 to 1024 entries)."""
import os

N = 300
OUT_PATH = os.path.join(os.path.dirname(__file__), "test_manysyms.c")

HEADER = '''#include <stdint.h>
#include <windows.h>
#include "beacon_compatibility.h"

WINBASEAPI size_t __cdecl MSVCRT$strlen(const char *_Str);

void go(char *args, int len) {
    volatile size_t total = 0;
'''

FOOTER = '''
    BeaconPrintf(CALLBACK_OUTPUT, "MANYSYMS_OK:%d", (int)total);
}
'''


def main():
    with open(OUT_PATH, "w") as f:
        f.write(HEADER)
        for _ in range(N):
            f.write('    total += MSVCRT$strlen("a");\n')
        f.write(FOOTER)
    print(f"Wrote {OUT_PATH} with {N} repeated external-symbol calls.")


if __name__ == "__main__":
    main()
