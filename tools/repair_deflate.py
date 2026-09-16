"""Forensic search for a one-bit corruption in an embedded raw DEFLATE stream."""

from __future__ import annotations

import argparse
import struct
import zlib
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("error_offset", type=lambda value: int(value, 0))
    parser.add_argument("--radius", type=int, default=512)
    args = parser.parse_args()
    data = args.adv.read_bytes()
    metadata = struct.unpack_from("<I", data, 0x34)[0]
    header = data.find(b"PK\x03\x04", metadata, metadata + 1024)
    crc, compressed_size, output_size = struct.unpack_from("<III", data, header + 14)
    name_len, extra_len = struct.unpack_from("<HH", data, header + 26)
    start = header + 30 + name_len + extra_len
    compressed = bytearray(data[start:start + compressed_size])
    lo = max(0, args.error_offset - args.radius)
    hi = min(len(compressed), args.error_offset + args.radius)
    print(f"testing {lo:#x}..{hi:#x}; expected size={output_size}, crc={crc:#x}")
    structural_offsets = set()
    for offset in range(lo, hi):
        original = compressed[offset]
        for bit in range(8):
            compressed[offset] = original ^ (1 << bit)
            try:
                payload = zlib.decompress(compressed, -zlib.MAX_WBITS)
            except zlib.error:
                continue
            actual_crc = zlib.crc32(payload) & 0xFFFFFFFF
            structural_offsets.add(offset)
            print(
                f"candidate offset={offset:#x} bit={bit}: "
                f"size={len(payload)}, crc={actual_crc:#x}, exact={len(payload) == output_size and actual_crc == crc}"
            )
            if len(payload) == output_size and actual_crc == crc:
                return
        compressed[offset] = original
    print("testing one-byte deletions")
    original_bytes = bytes(compressed)
    for offset in range(lo, hi):
        candidate = original_bytes[:offset] + original_bytes[offset + 1:]
        try:
            payload = zlib.decompress(candidate, -zlib.MAX_WBITS)
        except zlib.error:
            continue
        actual_crc = zlib.crc32(payload) & 0xFFFFFFFF
        print(f"delete {offset:#x}: size={len(payload)}, crc={actual_crc:#x}")
        if len(payload) == output_size and actual_crc == crc:
            return
    replacement_offsets = sorted(structural_offsets)
    print("testing byte replacements", [hex(x) for x in replacement_offsets])
    mutable = bytearray(original_bytes)
    for offset in replacement_offsets:
        original = mutable[offset]
        for value in range(256):
            if value == original:
                continue
            mutable[offset] = value
            try:
                payload = zlib.decompress(mutable, -zlib.MAX_WBITS)
            except zlib.error:
                continue
            actual_crc = zlib.crc32(payload) & 0xFFFFFFFF
            if len(payload) == output_size and actual_crc == crc:
                print(f"EXACT replacement offset={offset:#x}: {original:#x}->{value:#x}")
                return
        mutable[offset] = original
    print("testing byte insertions")
    insertion_offsets = sorted({offset + delta for offset in replacement_offsets for delta in (-1, 0, 1)})
    for offset in insertion_offsets:
        for value in range(256):
            candidate = original_bytes[:offset] + bytes([value]) + original_bytes[offset:]
            try:
                payload = zlib.decompress(candidate, -zlib.MAX_WBITS)
            except zlib.error:
                continue
            actual_crc = zlib.crc32(payload) & 0xFFFFFFFF
            if len(payload) == output_size and actual_crc == crc:
                print(f"EXACT insertion offset={offset:#x}: {value:#x}")
                return
    print("no exact single-byte repair found")


if __name__ == "__main__":
    main()
