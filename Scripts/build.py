#!/usr/bin/env python3
"""
Windows-compatible build script for CS_COFFLoader.

Equivalent to Scripts/build.sh but runs natively on Windows with Python.
Falls back to WSL for gcc if MinGW is not installed on Windows.

Requirements:
  - gcc (MinGW-w64) on PATH, or WSL with x86_64-w64-mingw32-gcc
  - dotnet SDK on PATH
  - Python 3.6+

Usage:
  python Scripts\\build.py           # Release build
  python Scripts\\build.py --debug   # Debug build
"""

import os
import subprocess
import sys
from pathlib import Path


def run(cmd, desc):
    """Run a command, print it, and abort on failure."""
    print(f"\n=== {desc} ===")
    print(f"> {' '.join(cmd)}")
    result = subprocess.run(cmd)
    if result.returncode != 0:
        print(f"ERROR: {desc} failed (exit code {result.returncode})")
        sys.exit(1)


def find_gcc():
    """Find a working gcc for compiling COFF objects."""
    # Try native gcc first (MinGW on PATH)
    for gcc in ["x86_64-w64-mingw32-gcc", "gcc"]:
        try:
            result = subprocess.run(
                [gcc, "--version"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            if result.returncode == 0:
                return [gcc], False
        except FileNotFoundError:
            continue

    # Fall back to WSL
    for gcc in ["x86_64-w64-mingw32-gcc", "gcc"]:
        try:
            result = subprocess.run(
                ["wsl", gcc, "--version"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            if result.returncode == 0:
                return ["wsl", gcc], True
        except FileNotFoundError:
            break  # wsl not available at all

    print("ERROR: No gcc found. Install MinGW-w64 or ensure WSL has gcc.")
    print("  Native:  choco install mingw  /  scoop install gcc")
    print("  WSL:     sudo apt install gcc-mingw-w64-x86-64")
    sys.exit(1)

def to_wsl_path(win_path):
    """Convert a Windows path (D:\\foo\\bar) to WSL path (/mnt/d/foo/bar)."""
    p = str(win_path)
    if len(p) >= 2 and p[1] == ':':
        drive = p[0].lower()
        rest = p[2:].replace('\\', '/')
        return f'/mnt/{drive}{rest}'
    return p.replace('\\', '/')

def main():
    # Resolve project root (parent of Scripts/)
    script_dir = Path(__file__).resolve().parent
    project_root = script_dir.parent
    os.chdir(project_root)

    debug = "--debug" in sys.argv or "-d" in sys.argv

    # 1. Create output directory
    bin_dir = project_root / "bin"
    bin_dir.mkdir(exist_ok=True)

    # 2. Compile beacon_compatibility.c
    gcc_cmd, use_wsl = find_gcc()
    beacon_o = str(bin_dir / "beacon_compatibility.o")
    include_dir = str(project_root / "beacon_object" / "include")
    source_file = str(project_root / "beacon_object" / "src" / "beacon_compatibility.c")

    if use_wsl:
        # gcc in WSL needs Linux-style paths
        run(
            gcc_cmd + [
                "-o", to_wsl_path(beacon_o),
                "-I", to_wsl_path(include_dir),
                "-Os", to_wsl_path(source_file),
                "-lws2_32", "-c",
            ],
            "Building beacon_compatibility.o (via WSL)",
        )
    else:
        run(
            gcc_cmd + ["-o", beacon_o, "-I", include_dir, "-Os", source_file, "-lws2_32", "-c"],
            "Building beacon_compatibility.o",
        )

    # 3. Embed blob into Program.cs (always uses Windows paths)
    embed_script = str(script_dir / "embed_blob.py")
    program_cs = str(project_root / "CoffLoader" / "Program.cs")
    run(
        [sys.executable, embed_script, beacon_o, program_cs],
        "Embedding beacon blob into Program.cs",
    )

    # 4. Build C# project
    csproj = str(project_root / "CoffLoader" / "CoffLoader.csproj")
    config = "Debug" if debug else "Release"
    run(
        ["dotnet", "build", csproj, "-c", config, "-o", str(bin_dir)],
        f"Building CoffLoader ({config})",
    )

    # 5. Rename to lowercase for consistency with build.sh
    upper_exe = bin_dir / "CoffLoader.exe"
    if upper_exe.exists():
        upper_exe.rename(bin_dir / "coffloader.exe")

    print(f"\n=== Build complete ({config}) ===")
    print(f"Output: {bin_dir / 'coffloader.exe'}")


if __name__ == "__main__":
    main()
