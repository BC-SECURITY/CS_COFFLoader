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

- `tests/bofs/` — five small C source files, each compiled fresh by MinGW during
  the test run, each exercising one code path in `CoffLoader/src/CoffParser.cs`.
- `tests/run_tests.py` — a test runner that compiles each test BOF, invokes
  `bin/coffloader.exe` against it, and asserts on exit code + stdout content.
- `.github/workflows/ci.yml` — a GitHub Actions workflow (`windows-latest`) that
  runs `Scripts/build.py` (Release) and then `tests/run_tests.py`, triggered on
  push to `main` and on pull requests targeting `main`.

Nothing in the existing build scripts or `beacon_object/` is modified.

**Deviation (discovered via the first real CI runs, not anticipated at design
time):** `CoffLoader/CoffLoader.csproj` gained one build-time-only
`PackageReference` to `Microsoft.NETFramework.ReferenceAssemblies.net40`
(`PrivateAssets=All`), and the now-empty, unused `CoffLoader/packages.config`
was removed. `windows-latest` ships a modern dotnet SDK with no
`.NETFramework,Version=v4.0` reference assemblies installed, and the
chocolatey-packaged `.NET Framework 4.0` devpack installer (a circa-2010
standalone `.exe`) fails outright (exit 5100) on the runner's current Windows
Server image — installing it is not viable at all, not just unverified. The
NuGet reference-assemblies package is Microsoft's own documented fix for
exactly this "old target framework, no legacy devpack available on this OS"
scenario: it supplies the reference assemblies as ordinary NuGet content, so
no installer ever runs. It does not change `TargetFrameworkVersion`, does not
become a dependency of the built binary, and has no runtime effect — it only
affects what's available at compile time in CI (and for any other machine
that restores this project without a full legacy VS install). This was
confirmed with the user before making the change, since it crosses the
original "`CoffLoader/` untouched" boundary above.

## Test BOFs (`tests/bofs/*.c`)

| File | Exercises | Output marker |
|---|---|---|
| `test_basic.c` | Full load → relocate → execute → output pipeline, zero arguments | `BASIC_OK` |
| `test_args.c` | Argument marshaling for all format types (`i`/`s`/`z`/`Z`/`b`) — echoes each value back via `BeaconPrintf` | `ARGS_OK:<int>:<short>:<str>:<wstr>:<binlen>` |
| `test_reloc.c` | Multiple global variables/string constants and cross-function calls within one object, forcing ADDR64/ADDR32NB/REL32 relocations and multiple function-mapping-table entries | `RELOC_OK:<computed>` |
| `test_symbols.c` | The two symbol-resolution tiers not already incidentally covered by every other test's `BeaconPrintf` call: one of the hardcoded Kernel32 functions (e.g. `GetModuleHandleA`), and a dynamically-resolved `LIBRARY$function` call (e.g. `MSVCRT$strlen`) | `SYMBOLS_OK` |
| `test_manysyms.c` | Regression coverage for commit `bd1268e` (function-mapping limit raised from 256 to 1024): references 300+ distinct external symbols/relocations, past the old 256-slot limit | `MANYSYMS_OK` |

Note: every test BOF calls `BeaconPrintf`, which is itself resolved through the
beacon-internal function table (tier 1 of symbol resolution). `test_symbols.c`
is scoped to the two tiers it uniquely adds — the hardcoded Kernel32 shortlist
and dynamic `LIBRARY$function` resolution — rather than re-claiming tier 1.

Each BOF prints its marker via `BeaconPrintf`. The test runner greps the marker
out of `coffloader.exe`'s captured stdout. For `test_args.c`, the runner packs
a fixed, known set of input values via `beacon_generate.bof_pack()` before
invocation, and asserts the echoed marker matches those exact values — this
catches silent argument-marshaling corruption, not just crashes.

**`test_args.c` binary payload constraint:** `CoffParser.cs` sizes the incoming
argument buffer via `Encoding.Default.GetString(argumentdata).Length` rather
than the raw byte length — a pre-existing, codepage-dependent quirk unrelated
to this test pipeline. The fixed binary (`b`) payload used by `test_args.c`
must therefore be constrained to byte values that round-trip stably under
`Encoding.Default` (e.g. printable ASCII, avoiding bytes ≥ 0x80), so the test
doesn't become flaky across differently-configured CI runner locales for
reasons unrelated to what it's meant to verify.

**`test_reloc.c` coverage verification:** which specific relocation types
MinGW emits for a given C construct is a compiler codegen detail, not
something "multiple globals and cross-function calls" guarantees on its own.
This was verified empirically during design (GCC 13, `x86_64-w64-mingw32-gcc`,
`-O0`) by compiling candidate source and inspecting the raw COFF relocation
table: cross-function calls and `.rdata`/`.data` references reliably produce
`IMAGE_REL_AMD64_REL32` (type 4), and a global array of string-literal
pointers produces `IMAGE_REL_AMD64_ADDR64` (type 1) entries in `.data`.
`IMAGE_REL_AMD64_ADDR32NB` (type 3) is also present, generated automatically
in `.pdata` (x64 SEH unwind info) by the compiler for every function.

The REL32_1–5 sub-variants (types 5–9) were *not* reproducible: byte/word/
dword/qword immediate stores to global variables, tried specifically to
force them, all still emit plain REL32. `CoffParser.cs`'s REL32_1–5 branch
(lines 541–580) is therefore accepted as untested by this pipeline — hitting
it would require hand-crafted assembly or a different/older compiler
toolchain, which is out of scope. This is a known, accepted gap, not an
oversight.

Because relocation-type codegen can shift with compiler versions, `objdump -r`
should be re-run against `test_reloc.o` if `test_reloc.c` is ever edited, to
confirm the ADDR64/ADDR32NB/REL32 coverage claimed above still holds.

## Test runner (`tests/run_tests.py`)

For each test BOF:

1. Compile with MinGW (`x86_64-w64-mingw32-gcc`/`gcc`, matching the compiler
   resolution approach already used in `Scripts/build.py`'s `find_gcc()`).
2. Run `bin/coffloader.exe go <bof.o> <hex-args>`, capturing stdout and exit
   code.
3. Assert the expected marker (with expected values, where applicable)
   appears in stdout.

**Exit code is not a meaningful pass/fail signal on its own.** `Program.cs`
wraps `Main()` and `RunCoff()` in `try/catch` blocks that print an error
message but never set a nonzero exit code or rethrow — even a logical
failure inside `parseCOFF` (e.g. `Result == "ERROR"`) still exits 0. In
practice a nonzero exit code can only mean a hard native crash (e.g. an
access violation), something no marker string will survive to report either.
The runner therefore treats these as two distinct failure categories rather
than one combined assertion:

- **Crash:** nonzero exit code, or the process not returning at all —
  reported as `CRASH` regardless of what (if anything) was captured on
  stdout.
- **Assertion failure:** exit code 0, but the expected marker/values are
  missing or wrong from stdout — reported as `FAIL`.

Only the marker check determines logical pass/fail; the exit-code check
exists solely to catch and label crashes distinctly, since the pipeline's
goal is to catch regressions, and a silent crash is at least as important a
regression to catch as a wrong marker.

Failures are collected rather than fail-fast (mirroring `tester.py`'s
per-test try/except pattern), so one failing BOF doesn't prevent the others
from running. A summary is printed at the end (`N/M passed`), and the script
exits non-zero if any test failed (`CRASH` or `FAIL`) — this is what fails
the CI job.

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
