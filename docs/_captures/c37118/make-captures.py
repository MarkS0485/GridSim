"""Builds the capture inputs for chapter 33 (C37.118 synchrophasor ingest).

The frames are the N061 test fixture of tests/GridSim.Tests/N061PmuIngestTests.cs, byte for byte: two PMUs (STN_A
101, float phasors and float FREQ; STN_B 102, integer rectangular phasors, one analogue, one digital word, PHUNIT
1,000,000 = 10 V per count), TIME_BASE 1,000,000, a CONFIG-2 frame and three DATA frames 20 ms apart.
Run from this directory: python make-captures.py
"""
import struct


def crc(b):
    c = 0xFFFF
    for x in b:
        c ^= x << 8
        for _ in range(8):
            c = ((c << 1) ^ 0x1021) & 0xFFFF if c & 0x8000 else (c << 1) & 0xFFFF
    return c


def finish(f):
    f[-2:] = struct.pack(">H", crc(bytes(f[:-2])))
    return bytes(f)


def header(f, ftype, idcode, soc, fracsec):
    f[0] = 0xAA
    f[1] = (ftype << 4) | 1
    f[2:4] = struct.pack(">H", len(f))
    f[4:6] = struct.pack(">H", idcode)
    f[6:10] = struct.pack(">I", soc)
    f[10:14] = struct.pack(">I", fracsec)


FLOAT_ALL = 0x0002 | 0x0008
A = dict(name="STN_A", id=101, fmt=FLOAT_ALL, ph=2, an=0, dg=0, units=[0, 0x01000000])
B = dict(name="STN_B", id=102, fmt=0, ph=1, an=1, dg=1, units=[1_000_000])


def config(pmus):
    size = 14 + 4 + 2 + 2 + 2
    for p in pmus:
        size += 16 + 10 + 16 * (p["ph"] + p["an"] + 16 * p["dg"]) + 4 * (p["ph"] + p["an"] + p["dg"]) + 4
    f = bytearray(size)
    header(f, 3, 7, 1_700_000_000, 0)
    off = 14
    f[off:off + 4] = struct.pack(">I", 1_000_000); off += 4
    f[off:off + 2] = struct.pack(">H", len(pmus)); off += 2
    for p in pmus:
        f[off:off + 16] = p["name"].ljust(16).encode("ascii"); off += 16
        f[off:off + 10] = struct.pack(">HHHHH", p["id"], p["fmt"], p["ph"], p["an"], p["dg"]); off += 10
        off += 16 * (p["ph"] + p["an"] + 16 * p["dg"])
        for u in p["units"]:
            f[off:off + 4] = struct.pack(">I", u); off += 4
        off += 4 * p["an"] + 4 * p["dg"]
        f[off:off + 4] = struct.pack(">HH", 1, 1); off += 4      # FNOM bit 0 = 50 Hz, CFGCNT 1
    f[off:off + 2] = struct.pack(">h", 50)                         # DATA_RATE
    return finish(f)


def data(soc, fracsec):
    # STN_A block: STAT 0, two float phasors, float FREQ and DFREQ.
    a = struct.pack(">H", 0) + struct.pack(">ffff", 230000.0, -1000.0, 120.5, 30.25) + struct.pack(">ff", 50.01, -0.02)
    # STN_B block: STAT 0x8000, one integer phasor, FREQ +15 mHz, DFREQ -4, analogue 0, digital 0.
    b = struct.pack(">H", 0x8000) + struct.pack(">hh", 2300, -100) + struct.pack(">hh", 15, -4) + struct.pack(">hH", 0, 0)
    f = bytearray(14 + len(a) + len(b) + 2)
    header(f, 0, 7, soc, fracsec)
    f[14:14 + len(a) + len(b)] = a + b
    return finish(f)


cfg = config([A, B])
d = [data(1_700_000_001, t) for t in (0, 20_000, 40_000)]

with open("two-pmu.bin", "wb") as fh:
    fh.write(cfg + b"".join(d))

# The same capture with one byte of the third DATA frame flipped (the CRC no longer matches) and a DATA frame in
# front of the CONFIG frame (decoded against nothing).
bad = bytearray(d[2]); bad[20] ^= 0xFF
with open("two-pmu-damaged.bin", "wb") as fh:
    fh.write(data(1_700_000_000, 0) + cfg + d[0] + d[1] + bytes(bad))

# The clean capture cut off in the middle of its last frame.
with open("two-pmu-truncated.bin", "wb") as fh:
    fh.write((cfg + b"".join(d))[:-10])

print("config", len(cfg), "bytes; data", len(d[0]), "bytes")
print("data[0] hex:", d[0].hex(" ").upper())
print("config hex:", cfg.hex(" ").upper())
