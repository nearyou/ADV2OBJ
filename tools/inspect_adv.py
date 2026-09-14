"""Forensic, read-only inspector for Sarine Advisor ADV containers."""

from __future__ import annotations

import argparse
import struct
import zlib
from pathlib import Path


LOCAL_FILE = b"PK\x03\x04"


def iter_zip_members(data: bytes):
    """Yield decompressible, non-data-descriptor ZIP members embedded in ADV."""
    start = 0
    while True:
        offset = data.find(LOCAL_FILE, start)
        if offset < 0:
            return
        start = offset + 1
        if offset + 30 > len(data):
            continue
        fields = struct.unpack_from("<IHHHHHIIIHH", data, offset)
        _, version, flags, method, _, _, crc, compressed_size, size, name_len, extra_len = fields
        if version > 63 or method not in (0, 8) or flags & 0x08:
            continue
        header_end = offset + 30 + name_len + extra_len
        data_end = header_end + compressed_size
        if data_end > len(data):
            continue
        name = data[offset + 30 : offset + 30 + name_len].decode("utf-8", "replace")
        compressed = data[header_end:data_end]
        try:
            payload = compressed if method == 0 else zlib.decompress(compressed, -zlib.MAX_WBITS)
        except zlib.error:
            continue
        valid = len(payload) == size and zlib.crc32(payload) & 0xFFFFFFFF == crc
        yield offset, name, compressed_size, size, payload, valid


def find_number(payload: bytes, value: float):
    results = []
    for label, fmt in (("f32", "<f"), ("f64", "<d")):
        needle = struct.pack(fmt, value)
        cursor = 0
        positions = []
        while len(positions) < 8:
            cursor = payload.find(needle, cursor)
            if cursor < 0:
                break
            positions.append(cursor)
            cursor += 1
        if positions:
            results.append((label, positions))
    return results


def inspect(path: Path, probes: list[float], verbose: bool):
    data = path.read_bytes()
    print(f"{path}: {len(data):,} bytes")
    members = list(iter_zip_members(data))
    print(f"  embedded ZIP members: {len(members)}")
    valid_count = sum(member[-1] for member in members)
    print(f"  ZIP checksums valid: {valid_count}/{len(members)}")
    for index, (offset, name, compressed, size, payload, valid) in enumerate(members):
        ratio = compressed / size if size else 0
        hits_for_member = []
        for probe in probes:
            hits = find_number(payload, probe)
            if hits:
                hits_for_member.append((probe, hits))
        if verbose or hits_for_member or not valid:
            validation = "valid" if valid else f"declared {size:,}, got {len(payload):,}"
            print(
                f"  [{index:03}] @0x{offset:08x} {name!r}: "
                f"{compressed:,} -> {len(payload):,} bytes ({ratio:.1%}, {validation})"
            )
            for probe, hits in hits_for_member:
                print(f"        {probe}: {hits}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("files", nargs="+", type=Path)
    parser.add_argument("--probe", action="append", type=float, default=[])
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()
    for path in args.files:
        inspect(path, args.probe, args.verbose)


if __name__ == "__main__":
    main()
