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
