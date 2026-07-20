# CI Test Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a self-contained set of test BOFs and a Python test runner, wired into a GitHub Actions workflow, so every push to `main` and every pull request is verified to build `coffloader.exe` and correctly execute a representative set of BOFs — with no dependency on the external `CS-Situational-Awareness-BOF` repository.

**Architecture:** Five small C "BOF" source files under `tests/bofs/`, each compiled fresh by MinGW and each exercising one code path in `CoffLoader/src/CoffParser.cs` (basic load/execute, argument marshaling, relocations, symbol resolution tiers, and the function-mapping-table regression from commit `bd1268e`). `tests/run_tests.py` compiles each, runs it through `bin/coffloader.exe`, and asserts on captured stdout. `.github/workflows/ci.yml` runs the existing `Scripts/build.py` then `tests/run_tests.py` on `windows-latest`.

**Tech Stack:** C (MinGW-w64 / `x86_64-w64-mingw32-gcc`), Python 3 (test runner, reusing `Scripts/build.py`'s `find_gcc()` and `Scripts/beacon_generate.py`'s `bof_pack()`), GitHub Actions.

## Global Constraints

- Entry point function name for every test BOF is `go`, signature `void go(char *args, int len)` — matches the existing `coffloader.exe <functionName> <bof> <args>` CLI convention (`Scripts/tester.py`, README).
- Every test BOF source file must start with `#include <stdint.h>` before `#include "beacon_compatibility.h"` — the header uses `uint32_t` without including it itself (verified empirically; omitting this include fails to compile).
- Compile flag for all test BOFs: `-O0` (no optimization) — verified empirically to produce one relocation entry per call site, needed for the function-mapping regression test in Task 5.
- CI triggers: `push` to `main`; `pull_request` targeting `main`. Runner: `windows-latest`. Build config: Release only.
- Marker-match on captured stdout is the sole pass/fail authority. A nonzero exit code (or a hung/never-returning process) is reported as a distinct `CRASH` category, never conflated with a marker-mismatch `FAIL` — see spec's "Exit code is not a meaningful pass/fail signal on its own."
- The binary (`b`-format) argument payload used in `test_args.c`'s test case must be printable ASCII only (bytes < 0x80) — `CoffParser.cs` sizes the argument buffer via `Encoding.Default.GetString(...)`, which is codepage-dependent for bytes ≥ 0x80.
- REL32_1–5 relocation sub-variants are out of scope — verified empirically (GCC 13, `-O0`) that ordinary C constructs (byte/word/dword/qword immediate stores) do not produce them; only plain `REL32`, `ADDR64`, and `ADDR32NB` are targeted.
- `tests/run_tests.py` assumes `bin/coffloader.exe` already exists (built via `Scripts/build.py`); it does not build it itself.

---

### Task 1: Scaffolding + `test_basic.c`

**Files:**
- Create: `tests/bofs/test_basic.c`
- Modify: `.gitignore` (add `tests/bofs/build/`)

**Interfaces:**
- Produces: a compilable BOF object exposing `go(char*, int)` that prints the literal marker `BASIC_OK` via `BeaconPrintf`. No other task depends on this file's internals, only on the fact that `tests/bofs/test_basic.c` exists and compiles.

- [ ] **Step 1: Create the `tests/bofs/` directory and write `test_basic.c`**

```c
#include <stdint.h>
#include <windows.h>
#include "beacon_compatibility.h"

void go(char *args, int len) {
    BeaconPrintf(CALLBACK_OUTPUT, "BASIC_OK");
}
```

- [ ] **Step 2: Add the test-build output directory to `.gitignore`**

Append to `.gitignore`:

```
tests/bofs/build/
```

- [ ] **Step 3: Compile it to verify it builds cleanly**

Run:
```bash
mkdir -p tests/bofs/build
x86_64-w64-mingw32-gcc -o tests/bofs/build/test_basic.o -I beacon_object/include -O0 -c tests/bofs/test_basic.c
```
Expected: exit code 0, no warnings/errors, `tests/bofs/build/test_basic.o` created.

- [ ] **Step 4: Commit**

```bash
git add tests/bofs/test_basic.c .gitignore
git commit -m "test: add basic load/execute test BOF"
```

---

### Task 2: `test_symbols.c`

**Files:**
- Create: `tests/bofs/test_symbols.c`

**Interfaces:**
- Produces: a compilable BOF exposing `go(char*, int)` that prints `SYMBOLS_OK:1:4` when both the hardcoded-Kernel32 tier (`GetModuleHandleA`) and the dynamic `LIBRARY$function` tier (`MSVCRT$strlen`) resolve correctly.

- [ ] **Step 1: Write `test_symbols.c`**

```c
#include <stdint.h>
#include <windows.h>
#include "beacon_compatibility.h"

WINBASEAPI size_t __cdecl MSVCRT$strlen(const char *_Str);

void go(char *args, int len) {
    HMODULE hKernel32 = GetModuleHandleA("kernel32.dll");
    size_t slen = MSVCRT$strlen("test");

    BeaconPrintf(CALLBACK_OUTPUT, "SYMBOLS_OK:%d:%d", hKernel32 != NULL, (int)slen);
}
```

- [ ] **Step 2: Compile it**

```bash
x86_64-w64-mingw32-gcc -o tests/bofs/build/test_symbols.o -I beacon_object/include -O0 -c tests/bofs/test_symbols.c
```
Expected: exit code 0.

- [ ] **Step 3: Verify both symbol-resolution tiers are actually referenced**

```bash
x86_64-w64-mingw32-objdump -r tests/bofs/build/test_symbols.o | grep -E "__imp_GetModuleHandleA|__imp_MSVCRT\\\$strlen"
```
Expected output (two lines, both `IMAGE_REL_AMD64_REL32`):
```
0000000000000... IMAGE_REL_AMD64_REL32  __imp_GetModuleHandleA
0000000000000... IMAGE_REL_AMD64_REL32  __imp_MSVCRT$strlen
```
`__imp_GetModuleHandleA` (no `$`) confirms this hits `CoffParser.cs`'s hardcoded-Kernel32 branch; `__imp_MSVCRT$strlen` confirms it hits the dynamic `LIBRARY$function` branch.

- [ ] **Step 4: Commit**

```bash
git add tests/bofs/test_symbols.c
git commit -m "test: add symbol-resolution-tier test BOF"
```

---

### Task 3: `test_reloc.c`

**Files:**
- Create: `tests/bofs/test_reloc.c`

**Interfaces:**
- Produces: a compilable BOF exposing `go(char*, int)` that prints `RELOC_OK:42:alpha` when cross-function calls and global-data relocations (REL32, ADDR64, ADDR32NB) are applied correctly.

- [ ] **Step 1: Write `test_reloc.c`**

```c
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
```

- [ ] **Step 2: Compile it**

```bash
x86_64-w64-mingw32-gcc -o tests/bofs/build/test_reloc.o -I beacon_object/include -O0 -c tests/bofs/test_reloc.c
```
Expected: exit code 0.

- [ ] **Step 3: Verify the expected relocation types are present**

```bash
x86_64-w64-mingw32-objdump -r tests/bofs/build/test_reloc.o
```
Expected: `RELOCATION RECORDS FOR [.text]` contains multiple `IMAGE_REL_AMD64_REL32` entries (targeting `.data`, `.rdata`, and `BeaconPrintf`); `RELOCATION RECORDS FOR [.data]` contains three `IMAGE_REL_AMD64_ADDR64` entries targeting `.rdata` (the `g_labels` pointer array); `RELOCATION RECORDS FOR [.pdata]` contains `IMAGE_REL_AMD64_ADDR32NB` entries (compiler-generated SEH unwind info). If any of these three types is missing, the compiler/binutils version in use has changed codegen — note it and adjust the C source, don't silently accept reduced coverage.

- [ ] **Step 4: Commit**

```bash
git add tests/bofs/test_reloc.c
git commit -m "test: add relocation-coverage test BOF"
```

---

### Task 4: `test_args.c`

**Files:**
- Create: `tests/bofs/test_args.c`

**Interfaces:**
- Produces: a compilable BOF exposing `go(char*, int)` that parses a packed argument buffer (`i`=int, `s`=short, `z`=UTF-8 string, `Z`=UTF-16LE string, `b`=binary) and prints `ARGS_OK:<int>:<short>:<str>:<wstrlen>:<binlen>`.
- Consumes: `datap`, `BeaconDataParse`, `BeaconDataInt`, `BeaconDataShort`, `BeaconDataExtract` from `beacon_object/include/beacon_compatibility.h` (already exist, unmodified).

- [ ] **Step 1: Write `test_args.c`**

```c
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
```

- [ ] **Step 2: Compile it**

```bash
x86_64-w64-mingw32-gcc -o tests/bofs/build/test_args.o -I beacon_object/include -O0 -c tests/bofs/test_args.c
```
Expected: exit code 0.

- [ ] **Step 3: Verify the expected argument packing/marker match by hand**

Run:
```bash
python3 -c "
import sys; sys.path.insert(0, 'Scripts')
from beacon_generate import bof_pack
from binascii import hexlify
packed = bof_pack('iszZb', [42, 7, 'hello', 'hi', b'XYZ'])
print(hexlify(packed).decode())
"
```
Expected output: `210000002a00000007000600000068656c6c6f00060000006800690000000300000058595a`

This is the exact hex-args string Task 6's `tests/run_tests.py` will pass to `coffloader.exe go test_args.o <hex>`, and the corresponding expected marker is `ARGS_OK:42:7:hello:6:3` (int `42`, short `7`, string `"hello"`, UTF-16LE-encoded `"hi\0"` is 6 bytes, binary `b"XYZ"` is 3 bytes).

- [ ] **Step 4: Commit**

```bash
git add tests/bofs/test_args.c
git commit -m "test: add argument-marshaling test BOF"
```

---

### Task 5: `test_manysyms.c` (function-mapping-limit regression)

**Files:**
- Create: `tests/bofs/gen_test_manysyms.py`
- Create: `tests/bofs/test_manysyms.c` (generated by the script above, then committed as a static file)

**Interfaces:**
- Produces: a compilable BOF exposing `go(char*, int)` containing 300 distinct call sites to the same external symbol (`MSVCRT$strlen`), each producing its own relocation entry — regression coverage for commit `bd1268e` (function-mapping table limit raised from 256 to 1024). Prints `MANYSYMS_OK:300`.

- [ ] **Step 1: Write the generator script**

```python
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
```

Save as `tests/bofs/gen_test_manysyms.py`.

- [ ] **Step 2: Run the generator**

```bash
python3 tests/bofs/gen_test_manysyms.py
```
Expected output: `Wrote tests/bofs/test_manysyms.c with 300 repeated external-symbol calls.`

- [ ] **Step 3: Compile the generated file**

```bash
x86_64-w64-mingw32-gcc -o tests/bofs/build/test_manysyms.o -I beacon_object/include -O0 -c tests/bofs/test_manysyms.c
```
Expected: exit code 0.

- [ ] **Step 4: Verify 300 distinct relocation entries reference the external symbol**

```bash
python3 -c "
import struct
with open('tests/bofs/build/test_manysyms.o', 'rb') as f:
    data = f.read()
NumberOfSections, = struct.unpack_from('<H', data, 2)
PointerToSymbolTable, NumberOfSymbols = struct.unpack_from('<II', data, 8)
SYM_SIZE = 18
strtab_off = PointerToSymbolTable + NumberOfSymbols * SYM_SIZE
names = {}
for i in range(NumberOfSymbols):
    rec = data[PointerToSymbolTable + i*SYM_SIZE : PointerToSymbolTable + i*SYM_SIZE + SYM_SIZE]
    zeros, offset = struct.unpack_from('<II', rec, 0)
    if zeros != 0:
        names[i] = rec[0:8].rstrip(b'\x00').decode('ascii', 'replace')
    else:
        end = data.index(b'\x00', strtab_off + offset)
        names[i] = data[strtab_off + offset:end].decode('ascii', 'replace')
target = [i for i, n in names.items() if 'strlen' in n]
off = 20
for i in range(NumberOfSections):
    sect = data[off:off+40]
    sname = sect[0:8].rstrip(b'\x00').decode('ascii', 'replace')
    ptr_reloc = struct.unpack_from('<I', sect, 24)[0]
    n_reloc = struct.unpack_from('<H', sect, 32)[0]
    if sname == '.text':
        count = 0
        for r in range(n_reloc):
            rec = data[ptr_reloc + r*10 : ptr_reloc + r*10 + 10]
            _, symidx, _ = struct.unpack_from('<IIH', rec, 0)
            if symidx in target:
                count += 1
        print('relocations referencing strlen symbol:', count)
    off += 40
"
```
Expected output: `relocations referencing strlen symbol: 300`

- [ ] **Step 5: Commit both files**

```bash
git add tests/bofs/gen_test_manysyms.py tests/bofs/test_manysyms.c
git commit -m "test: add function-mapping-limit regression test BOF"
```

---

### Task 6: `tests/run_tests.py`

**Files:**
- Create: `tests/run_tests.py`

**Interfaces:**
- Consumes: `find_gcc()` from `Scripts/build.py` (returns `(gcc_cmd: list[str], use_wsl: bool)`); `bof_pack(fstring: str, args: list) -> bytes` from `Scripts/beacon_generate.py`; the five `tests/bofs/*.c` files and their expected markers from Tasks 1–5; `bin/coffloader.exe` (built separately via `Scripts/build.py`, not by this script).
- Produces: an executable script `python tests/run_tests.py` that exits `0` if every test BOF compiles, runs, and prints its expected marker; exits `1` otherwise. Prints a `[PASS]`/`[FAIL]`/`[CRASH]` line per test and a final `RESULTS: N/M passed` summary.

- [ ] **Step 1: Write `tests/run_tests.py`**

```python
#!/usr/bin/env python3
import subprocess
import sys
from pathlib import Path
from binascii import hexlify

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO_ROOT / "Scripts"))
from build import find_gcc          # noqa: E402
from beacon_generate import bof_pack  # noqa: E402

BOFS_DIR = REPO_ROOT / "tests" / "bofs"
BUILD_DIR = BOFS_DIR / "build"
INCLUDE_DIR = REPO_ROOT / "beacon_object" / "include"
COFFLOADER_EXE = REPO_ROOT / "bin" / "coffloader.exe"


def classify_result(exit_code, stdout, expected_marker):
    """Return (status, detail) where status is one of PASS/FAIL/CRASH."""
    if exit_code != 0:
        return "CRASH", f"coffloader.exe exited {exit_code}"
    if expected_marker not in stdout:
        return "FAIL", f"expected {expected_marker!r} not found in output"
    return "PASS", ""


def compile_bof(gcc_cmd, source_name):
    src = BOFS_DIR / source_name
    BUILD_DIR.mkdir(parents=True, exist_ok=True)
    out = BUILD_DIR / (src.stem + ".o")
    cmd = list(gcc_cmd) + ["-o", str(out), "-I", str(INCLUDE_DIR), "-O0", "-c", str(src)]
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        return None, result.stderr
    return out, None


def run_bof(bof_path, hex_args):
    cmd = [str(COFFLOADER_EXE), "go", str(bof_path), hex_args]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
    return result.returncode, result.stdout


TEST_CASES = [
    {
        "name": "test_basic",
        "source": "test_basic.c",
        "hex_args": "00",
        "expected": "BASIC_OK",
    },
    {
        "name": "test_args",
        "source": "test_args.c",
        "hex_args": hexlify(bof_pack("iszZb", [42, 7, "hello", "hi", b"XYZ"])).decode("utf-8"),
        "expected": "ARGS_OK:42:7:hello:6:3",
    },
    {
        "name": "test_reloc",
        "source": "test_reloc.c",
        "hex_args": "00",
        "expected": "RELOC_OK:42:alpha",
    },
    {
        "name": "test_symbols",
        "source": "test_symbols.c",
        "hex_args": "00",
        "expected": "SYMBOLS_OK:1:4",
    },
    {
        "name": "test_manysyms",
        "source": "test_manysyms.c",
        "hex_args": "00",
        "expected": "MANYSYMS_OK:300",
    },
]


def main():
    if not COFFLOADER_EXE.exists():
        print(f"ERROR: {COFFLOADER_EXE} not found. Build it first: python Scripts/build.py")
        sys.exit(1)

    gcc_cmd, _ = find_gcc()

    results = []
    for test in TEST_CASES:
        name = test["name"]
        bof_path, compile_err = compile_bof(gcc_cmd, test["source"])
        if bof_path is None:
            print(f"[FAIL] {name}: compile error\n{compile_err}")
            results.append((name, "FAIL"))
            continue

        try:
            exit_code, stdout = run_bof(bof_path, test["hex_args"])
        except subprocess.TimeoutExpired:
            print(f"[CRASH] {name}: coffloader.exe timed out")
            results.append((name, "CRASH"))
            continue

        status, detail = classify_result(exit_code, stdout, test["expected"])
        if status == "PASS":
            print(f"[PASS] {name}")
        else:
            print(f"[{status}] {name}: {detail}\n{stdout}")
        results.append((name, status))

    passed = sum(1 for _, status in results if status == "PASS")
    total = len(results)
    print(f"\nRESULTS: {passed}/{total} passed")
    if passed != total:
        sys.exit(1)


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Verify `classify_result`'s logic directly (no Windows/coffloader.exe needed for this part)**

```bash
python3 -c "
import sys; sys.path.insert(0, 'tests')
from run_tests import classify_result
assert classify_result(0, 'BASIC_OK', 'BASIC_OK') == ('PASS', '')
assert classify_result(1, 'BASIC_OK', 'BASIC_OK')[0] == 'CRASH'
assert classify_result(0, 'WRONG', 'BASIC_OK')[0] == 'FAIL'
print('classify_result: all checks passed')
"
```
Expected output: `classify_result: all checks passed`

- [ ] **Step 3: Verify the five test cases all compile via the runner's own `compile_bof()` (compile-only — running `coffloader.exe` itself requires the Windows build from Task 7/CI, since it's a .NET Framework 4.0 Windows executable not runnable in this environment)**

```bash
python3 -c "
import sys; sys.path.insert(0, 'tests')
from run_tests import compile_bof, TEST_CASES, find_gcc
gcc_cmd, _ = find_gcc()
for t in TEST_CASES:
    out, err = compile_bof(gcc_cmd, t['source'])
    print(t['name'], 'OK' if out else f'FAILED: {err}')
"
```
Expected: all five lines print `OK`.

- [ ] **Step 4: Commit**

```bash
git add tests/run_tests.py
git commit -m "test: add test runner for CI-executed test BOFs"
```

---

### Task 7: `.github/workflows/ci.yml`

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `Scripts/build.py` (existing, unmodified) and `tests/run_tests.py` (Task 6).
- Produces: a GitHub Actions workflow named `CI` that runs on every push to `main` and every PR targeting `main`.

- [ ] **Step 1: Write the workflow**

```yaml
name: CI

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  build-and-test:
    runs-on: windows-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Set up Python
        uses: actions/setup-python@v5
        with:
          python-version: '3.x'

      - name: Install MinGW-w64
        shell: pwsh
        run: |
          choco install mingw -y --no-progress
          $gcc = Get-ChildItem -Path "C:\ProgramData\chocolatey\lib\mingw" -Recurse -Filter "gcc.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
          if (-not $gcc) {
            Write-Error "gcc.exe not found after 'choco install mingw'"
            exit 1
          }
          Write-Host "Found gcc at $($gcc.FullName)"
          Add-Content -Path $env:GITHUB_PATH -Value $gcc.Directory.FullName

      - name: Verify gcc is on PATH
        run: gcc --version

      - name: Build (Release)
        run: python Scripts/build.py

      - name: Run tests
        run: python tests/run_tests.py
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add GitHub Actions workflow to build and run test BOFs"
```

- [ ] **Step 3: Push and observe the first real run**

```bash
git push -u origin main
gh run watch
```
Expected: the workflow appears under the repo's Actions tab and completes with `RESULTS: 5/5 passed` in the "Run tests" step's log.

**Known contingency:** `Scripts/build.py` invokes `dotnet build` against a legacy, non-SDK-style project targeting `.NET Framework v4.0`. If the "Build (Release)" step fails because `dotnet build` can't resolve the full-framework MSBuild toolset or the `v4.0` reference assemblies, the fix is one of:
- Add a step installing the .NET Framework 4.0/4.8 targeting pack (e.g. `choco install netfx-4.8-devpack -y`), before the build step.
- Replace the `dotnet build` invocation with a direct `msbuild` call (locate via `vswhere.exe`, which ships on GitHub's `windows-latest` image).

This can only be diagnosed from the actual failing CI log — don't pre-guess further; fix based on what the run actually reports.

---

## Self-Review

**Spec coverage:**
- Four original test BOFs + the fifth (function-mapping regression) added after adversarial review → Tasks 1–5. ✓
- Test runner with crash-vs-fail distinction → Task 6. ✓
- CI workflow, triggers, runner OS, Release-only → Task 7. ✓
- Binary-payload locale-safety constraint → honored in Task 4/6 (`b"XYZ"`, all bytes < 0x80). ✓
- REL32_1–5 out of scope, empirically confirmed → Global Constraints + Task 3 note. ✓
- `Scripts/tester.py` untouched → no task modifies it. ✓

**Placeholder scan:** No TBD/TODO; every step has literal, complete code or exact commands with expected output.

**Type consistency:** `find_gcc()` return shape `(list[str], bool)` used consistently in Task 6 Step 1 and Step 3's verification snippet. `compile_bof(gcc_cmd, source_name) -> (Path|None, str|None)` used consistently. `classify_result(exit_code, stdout, expected_marker) -> (str, str)` matches its one call site in `main()`.
