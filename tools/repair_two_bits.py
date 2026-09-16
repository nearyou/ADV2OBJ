"""Search a small damaged DEFLATE region for an exact two-bit repair."""

from __future__ import annotations

import argparse
import itertools
import multiprocessing
import struct
import zlib
from pathlib import Path


COMPRESSED = b""
EXPECTED_SIZE = 0
EXPECTED_CRC = 0


def init_worker(compressed: bytes, size: int, crc: int) -> None:
    global COMPRESSED, EXPECTED_SIZE, EXPECTED_CRC
    COMPRESSED = compressed
    EXPECTED_SIZE = size
    EXPECTED_CRC = crc


def test_pair(pair: tuple[int, int]):
    first, second = pair
    candidate = bytearray(COMPRESSED)
    candidate[first // 8] ^= 1 << (first % 8)
    candidate[second // 8] ^= 1 << (second % 8)
    try:
        payload = zlib.decompress(candidate, -15)
    except zlib.error:
        return None
    if len(payload) == EXPECTED_SIZE and zlib.crc32(payload) & 0xFFFFFFFF == EXPECTED_CRC:
        return first, second
    return None


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
    bits = range(args.first * 8, (args.last + 1) * 8)
    pairs = itertools.combinations(bits, 2)
    count = ((args.last - args.first + 1) * 8)
    total = count * (count - 1) // 2
    print(f"testing {total:,} two-bit repairs in {args.first:#x}..{args.last:#x}", flush=True)
    with multiprocessing.Pool(
        initializer=init_worker,
        initargs=(compressed, size, crc),
    ) as pool:
        for index, result in enumerate(pool.imap_unordered(test_pair, pairs, chunksize=16), 1):
            if result is not None:
                print(
                    f"EXACT: offset={result[0] // 8:#x} bit={result[0] % 8}; "
                    f"offset={result[1] // 8:#x} bit={result[1] % 8}",
                    flush=True,
                )
                pool.terminate()
                return
            if index % 5000 == 0:
                print(f"tested {index:,}/{total:,}", flush=True)
    print("no exact repair found", flush=True)


if __name__ == "__main__":
    multiprocessing.freeze_support()
    main()
