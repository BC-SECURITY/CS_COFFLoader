# CI Test Pipeline — Design

## Goal

CS_COFFLoader currently has no automated testing. The only existing test harness,
`Scripts/tester.py`, is a manual, ad-hoc smoke-test script: it checks process exit
codes only (not output correctness), and it depends on an external sibling
repository (`CS-Situational-Awareness-BOF`) with hardcoded Windows paths that
aren't available outside the original author's machine.

This design adds a self-contained automated test pipeline wired into GitHub
Actions CI, so every push to `main` and every pull request is verified to build
and to correctly execute a representative set of BOFs — without any dependency
on external repositories. `Scripts/tester.py` is left untouched; it remains
available for manual, ad-hoc testing against the external BOF corpus.

## Architecture

Two new, additive pieces:

- `tests/bofs/` — four small C source files, each compiled fresh by MinGW during
  the test run, each exercising one code path in `CoffLoader/src/CoffParser.cs`.
- `tests/run_tests.py` — a test runner that compiles each test BOF, invokes
  `bin/coffloader.exe` against it, and asserts on exit code + stdout content.
- `.github/workflows/ci.yml` — a GitHub Actions workflow (`windows-latest`) that
  runs `Scripts/build.py` (Release) and then `tests/run_tests.py`, triggered on
  push to `main` and on pull requests targeting `main`.

Nothing in the existing build scripts, `CoffLoader/`, or `beacon_object/` is
modified.

## Test BOFs (`tests/bofs/*.c`)

| File | Exercises | Output marker |
|---|---|---|
| `test_basic.c` | Full load → relocate → execute → output pipeline, zero arguments | `BASIC_OK` |
| `test_args.c` | Argument marshaling for all format types (`i`/`s`/`z`/`Z`/`b`) — echoes each value back via `BeaconPrintf` | `ARGS_OK:<int>:<short>:<str>:<wstr>:<binlen>` |
| `test_reloc.c` | Multiple global variables/string constants and cross-function calls within one object, forcing ADDR64/ADDR32NB/REL32(_1–5) relocations and multiple function-mapping-table entries | `RELOC_OK:<computed>` |
| `test_symbols.c` | All three symbol-resolution tiers: a beacon-internal call, one of the hardcoded Kernel32 functions (e.g. `GetModuleHandleA`), and a dynamically-resolved `LIBRARY$function` call (e.g. `MSVCRT$strlen`) | `SYMBOLS_OK` |

Each BOF prints its marker via `BeaconPrintf`. The test runner greps the marker
out of `coffloader.exe`'s captured stdout. For `test_args.c`, the runner packs
a fixed, known set of input values via `beacon_generate.bof_pack()` before
invocation, and asserts the echoed marker matches those exact values — this
catches silent argument-marshaling corruption, not just crashes.

## Test runner (`tests/run_tests.py`)

For each test BOF:

1. Compile with MinGW (`x86_64-w64-mingw32-gcc`/`gcc`, matching the compiler
   resolution approach already used in `Scripts/build.py`'s `find_gcc()`).
2. Run `bin/coffloader.exe go <bof.o> <hex-args>`, capturing stdout and exit
   code.
3. Assert exit code is 0 **and** the expected marker (with expected values,
   where applicable) appears in stdout.

Failures are collected rather than fail-fast (mirroring `tester.py`'s
per-test try/except pattern), so one failing BOF doesn't prevent the others
from running. A summary is printed at the end (`N/M passed`), and the script
exits non-zero if any test failed — this is what fails the CI job.

A BOF that fails to *compile* counts as a failed test for that BOF, not a
runner crash.

The runner assumes `bin/coffloader.exe` has already been built (matching
`tester.py`'s existing convention); it does not invoke `Scripts/build.py`
itself. This keeps build and test as separate, single-responsibility steps,
both locally and in CI.

## CI workflow (`.github/workflows/ci.yml`)

- **Triggers:** `push` to `main`; `pull_request` targeting `main`.
- **Runner:** `windows-latest`.
- **Build configuration:** Release only (matches the README's documented
  quick-build path; Debug remains a local dev convenience, not CI-gated).
- **Steps:**
  1. Checkout.
  2. Ensure MinGW-w64 is available on `PATH` (not preinstalled on the
     `windows-latest` image).
  3. `python Scripts/build.py` (Release) — reuses existing build logic rather
     than duplicating compile/embed/build steps in YAML.
  4. `python tests/run_tests.py`.

**Known risk:** `Scripts/build.py` invokes `dotnet build` against a legacy,
non-SDK-style project targeting `.NET Framework v4.0`. This is expected to
work on `windows-latest` (the image ships full Visual Studio/MSBuild), but
will be verified during implementation; if `dotnet build` cannot resolve the
full-framework toolset, the workflow will invoke `msbuild` directly as a
fallback.

## Out of scope

- Rewriting or wiring `Scripts/tester.py` into CI.
- A Debug build matrix.
- Build artifact upload, coverage reporting, or Linux/Wine-based execution.
- Pulling in `CS-Situational-Awareness-BOF` as a submodule.

These can be revisited later if needed, but aren't required for the stated
goal of catching regressions on push/PR with a self-contained test corpus.
