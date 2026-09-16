"""Test inserted/deleted bit repairs around a damaged DEFLATE block."""

from __future__ import annotations

import argparse
import struct
import zlib
from pathlib import Path


def valid(candidate: bytes, size: int, crc: int) -> bool:
    try:
        payload = zlib.decompress(candidate, -15)
    except zlib.error:
        return False
    return len(payload) == size and zlib.crc32(payload) & 0xFFFFFFFF == crc


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("first", type=lambda value: int(value, 0))
    parser.add_argument("last", type=lambda value: int(value, 0))
    args = parser.parse_args()
    raw = args.adv.read_bytes()
    header = raw.find(b"PK\x03\x04")
    crc, compressed_size, size = struct.unpack_from("<III", raw, header + 14)
    name_len, extra_len = struct.unpack_from("<HH", raw, header + 26)
    start = header + 30 + name_len + extra_len
    compressed = raw[start:start + compressed_size]
    source = int.from_bytes(compressed, "little")
    total_bits = len(compressed) * 8
    first_bit, last_bit = args.first * 8, (args.last + 1) * 8
    print(f"testing bit slips {first_bit}..{last_bit}", flush=True)
    for position in range(first_bit, last_bit):
        # A spurious bit in the stored stream: delete it and retain byte length
        # by trying either value at the final position.
        low_mask = (1 << position) - 1
        shortened = (source & low_mask) | ((source >> (position + 1)) << position)
        for tail in (0, 1):
            candidate = shortened | (tail << (total_bits - 1))
            if valid(candidate.to_bytes(len(compressed), "little"), size, crc):
                print(f"EXACT delete bit {position} (byte {position // 8:#x}, bit {position % 8})")
                return
        # A missing bit in the stored stream: insert either value and discard
        # the final bit to keep the declared compressed size.
        for value in (0, 1):
            expanded = (source & low_mask) | (value << position) | ((source >> position) << (position + 1))
            expanded &= (1 << total_bits) - 1
            if valid(expanded.to_bytes(len(compressed), "little"), size, crc):
                print(f"EXACT insert {value} at bit {position} (byte {position // 8:#x}, bit {position % 8})")
                return
    print("no exact repair found")


if __name__ == "__main__":
    main()
