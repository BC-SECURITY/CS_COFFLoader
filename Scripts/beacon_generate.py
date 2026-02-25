#!/usr/bin/env python3
import os
import sys
import base64
import shlex
from binascii import hexlify, unhexlify
import struct


class Packer:
    """
    Emulates Cobalt Strike's bof_pack() style:
      - final blob is: [uint32 total_size][packed_fields...]
      - fields are packed per-format

    Format chars:
      b = binary bytes (uint32 len + bytes)
      i = int32
      s = uint16
      z = utf-8 string, null-terminated (uint32 len+1 + bytes + 0x00)
      Z = utf-16le string, null-terminated (uint32 len+2 + bytes + 0x00 0x00)

    Extra (optional):
      u = uint32
      B = bool (uint32 0/1)
    """

    def __init__(self):
        self.buffer = bytearray()

    @property
    def size(self) -> int:
        return len(self.buffer)

    def getbuffer(self) -> bytes:
        return struct.pack("<I", self.size) + bytes(self.buffer)

    def addbytes(self, b):
        if b is None:
            b = b""
        if isinstance(b, str):
            b = b.encode("utf-8")
        else:
            b = bytes(b)

        self.buffer += struct.pack("<I", len(b))
        self.buffer += b

    def addstr(self, s):
        if s is None:
            s = ""
        if isinstance(s, bytes):
            raw = s
        else:
            raw = str(s).encode("utf-8")

        raw0 = raw + b"\x00"
        self.buffer += struct.pack("<I", len(raw0))
        self.buffer += raw0

    def addWstr(self, s):
        if s is None:
            s = ""
        raw0 = (str(s) + "\x00").encode("utf-16le")
        self.buffer += struct.pack("<I", len(raw0))
        self.buffer += raw0

    def addbool(self, b):
        self.buffer += struct.pack("<I", 1 if bool(b) else 0)

    def adduint32(self, n):
        self.buffer += struct.pack("<I", int(n) & 0xFFFFFFFF)

    def addint(self, n):
        self.buffer += struct.pack("<i", int(n))

    def addshort(self, n):
        # Match your new packer: unsigned 16-bit
        self.buffer += struct.pack("<H", int(n) & 0xFFFF)


def _parse_binary_arg(arg: str) -> bytes:
    """
    Back-compat + quality-of-life:
      - if arg is a file path that exists -> read file bytes
      - else if startswith hex: -> hex-decode
      - else if startswith b64: -> base64-decode
      - else -> treat as utf-8 bytes
    """
    if arg is None:
        return b""

    # file path behavior (old packer)
    if isinstance(arg, str) and os.path.isfile(arg):
        with open(arg, "rb") as f:
            return f.read()

    if isinstance(arg, (bytes, bytearray)):
        return bytes(arg)

    s = str(arg)

    if s.startswith("hex:"):
        return unhexlify(s[4:].strip())
    if s.startswith("b64:"):
        return base64.b64decode(s[4:].strip())

    return s.encode("utf-8")


def bof_pack(fstring: str, args: list) -> bytes:
    if len(fstring) != len(args):
        raise ValueError(
            f"Format string length must match arguments: fstring={len(fstring)} args={len(args)}"
        )

    p = Packer()

    for i, c in enumerate(fstring):
        a = args[i]

        if c == "b":
            p.addbytes(_parse_binary_arg(a))
        elif c == "i":
            p.addint(a)
        elif c == "s":
            p.addshort(a)
        elif c == "z":
            p.addstr(a)
        elif c == "Z":
            p.addWstr(a)
        elif c == "u":
            p.adduint32(a)
        elif c == "B":
            # accept "true/false/1/0" strings too
            if isinstance(a, str):
                p.addbool(a.strip().lower() in ("1", "true", "yes", "y", "on"))
            else:
                p.addbool(a)
        else:
            raise ValueError(f"Invalid format character '{c}' at position {i}.")

    return p.getbuffer()


def pack_to_b64hex(fstring: str, arg_list: list) -> str:
    packed = bof_pack(fstring, arg_list)
    # matches your new: base64(hexlify(bytes))
    return base64.b64encode(hexlify(packed)).decode("utf-8")


if __name__ == "__main__":
    if len(sys.argv) < 3 or "-h" in sys.argv or "--help" in sys.argv:
        print("bof_pack: pack arguments in a format suitable to send to a beacon-object-file")
        print("Usage:")
        print("  bof_pack.py <format_string> <arg1> [arg2] [arg3] [...]")
        print("")
        print("Format chars: b i s z Z (optional: u B)")
        print("Binary 'b' accepts:")
        print("  - a file path (existing file) OR")
        print("  - hex:<hexbytes> OR b64:<base64bytes> OR raw string bytes")
        sys.exit(0)

    fstring = sys.argv[1]
    args = sys.argv[2:]

    packed = bof_pack(fstring, args)

    print(" ".join(args) + ' ("' + fstring + '")')
    print("-hex-> " + hexlify(packed).decode("utf-8"))
    print("-b64(hex)-> " + base64.b64encode(hexlify(packed)).decode("utf-8"))
    print("-b64(raw)-> " + base64.b64encode(packed).decode("utf-8"))