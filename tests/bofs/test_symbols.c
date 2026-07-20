#include <stdint.h>
#include <windows.h>
#include "beacon_compatibility.h"

WINBASEAPI size_t __cdecl MSVCRT$strlen(const char *_Str);

void go(char *args, int len) {
    HMODULE hKernel32 = GetModuleHandleA("kernel32.dll");
    size_t slen = MSVCRT$strlen("test");

    BeaconPrintf(CALLBACK_OUTPUT, "SYMBOLS_OK:%d:%d", hKernel32 != NULL, (int)slen);
}
