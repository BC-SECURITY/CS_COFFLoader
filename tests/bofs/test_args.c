#include <stdint.h>
#include <windows.h>
#include "beacon_compatibility.h"

void go(char *args, int len) {
    datap parser;
    BeaconDataParse(&parser, args, len);

    int   ival = BeaconDataInt(&parser);
    short sval = BeaconDataShort(&parser);

    int strsz = 0;
    char* str = BeaconDataExtract(&parser, &strsz);

    int wstrsz = 0;
    char* wstr = BeaconDataExtract(&parser, &wstrsz);

    int binsz = 0;
    char* bin = BeaconDataExtract(&parser, &binsz);

    BeaconPrintf(CALLBACK_OUTPUT, "ARGS_OK:%d:%d:%s:%d:%d", ival, sval, str, wstrsz, binsz);
}
