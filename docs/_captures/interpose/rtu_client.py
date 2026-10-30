#!/usr/bin/env python3
"""Loopback test client for `gridsim interpose` (documentation worked example).

It plays the part of a station: it connects to the interposer's listening port and sends hand-built,
valid IEC 60870-5-104 or Modbus TCP frames, in two bursts separated by a pause longer than the
interposer's --window-ms so that two measurement windows are emitted.

    python rtu_client.py iec104 --port 2404
    python rtu_client.py modbus --port 5020
    python rtu_client.py iec104-full --port 2404     # one window: all 14 case14 voltages and angles

Only the Python standard library is used. Every frame it sends is printed (hex) with a label.
"""
import argparse
import socket
import struct
import sys
import time
from datetime import datetime, timezone

# ---------------------------------------------------------------- IEC 60870-5-104 builders

def cp56(dt: datetime, iv: bool = False) -> bytes:
    """7-octet CP56Time2a: ms (u16 LE), minute+IV, hour, day, month, year-2000."""
    ms = dt.second * 1000 + dt.microsecond // 1000
    return struct.pack("<HBBBBB", ms, dt.minute | (0x80 if iv else 0), dt.hour, dt.day, dt.month, dt.year - 2000)

def ioa3(n: int) -> bytes:
    return struct.pack("<I", n)[:3]

def asdu(type_id: int, objects: bytes, count: int, sq: bool = False, cot: int = 3, test: bool = False, ca: int = 1) -> bytes:
    vsq = count | (0x80 if sq else 0)
    return struct.pack("<BBBBH", type_id, vsq, cot | (0x80 if test else 0), 0, ca) + objects

def i_frame(ns: int, nr: int, payload: bytes) -> bytes:
    ctrl = struct.pack("<HH", ns << 1, nr << 1)
    return bytes([0x68, 4 + len(payload)]) + ctrl + payload

def u_frame(code: int) -> bytes:
    return bytes([0x68, 4, code, 0, 0, 0])

def s_frame(nr: int) -> bytes:
    return bytes([0x68, 4, 0x01, 0x00]) + struct.pack("<H", nr << 1)

def tf(ioa: int, value: float, q: int, t: datetime) -> bytes:      # type 36 element: IOA + float + QDS + CP56
    return ioa3(ioa) + struct.pack("<fB", value, q) + cp56(t)

T1 = datetime(2026, 7, 20, 13, 45, 12, 500000, tzinfo=timezone.utc)
T2 = datetime(2026, 7, 20, 13, 45, 13, 250000, tzinfo=timezone.utc)
T3 = datetime(2026, 7, 20, 13, 45, 14, 0, tzinfo=timezone.utc)

def iec_burst_a():
    ns = iter(range(100))
    yield "U-format STARTDT act", u_frame(0x07)
    yield "noise before a frame (3 octets, no 0x68)", b"\xff\x00\x13"
    yield "type 36 (timed float): IOA 1001 good, 1002 not topical, 1003 good", i_frame(next(ns), 0, asdu(36,
        tf(1001, 1.018, 0x00, T1) + tf(1002, 0.987, 0x40, T1) + tf(1003, 138.0, 0x00, T1), 3))
    yield "type 36: IOA 1001 reported again, later stamp", i_frame(next(ns), 0, asdu(36, tf(1001, 1.021, 0x00, T2), 1))
    yield "type 11 (scaled), SQ=1, base IOA 2001, two elements (second is invalid)", i_frame(next(ns), 0, asdu(11,
        ioa3(2001) + struct.pack("<hB", 1568, 0x00) + struct.pack("<hB", 400, 0x80), 2, sq=True))
    yield "type 13 (short float, untimed): IOA 3001 angle", i_frame(next(ns), 0, asdu(13,
        ioa3(3001) + struct.pack("<fB", -0.31, 0x00), 1))
    yield "type 31 (timed double point): IOA 4001 breaker ON, not in the crosswalk", i_frame(next(ns), 0, asdu(31,
        ioa3(4001) + bytes([0x02]) + cp56(T1), 1))
    yield "type 5 (step position): unsupported, counted", i_frame(next(ns), 0, asdu(5,
        ioa3(5001) + bytes([0x05, 0x00]), 1))
    yield "S-format acknowledgement", s_frame(7)

def iec_burst_b():
    ns = iter(range(10, 100))
    yield "type 36 declaring 2 objects but carrying 1 (truncated)", i_frame(next(ns), 0, asdu(36, tf(1001, 1.0, 0x00, T3), 2))
    yield "header with APDU length 2 (framing reject)", bytes([0x68, 0x02, 0x00, 0x00])
    yield "type 36: IOA 1001 = 1.019 at T3", i_frame(next(ns), 0, asdu(36, tf(1001, 1.019, 0x00, T3), 1))
    yield "type 13 with the COT test bit set (never live telemetry)", i_frame(next(ns), 0, asdu(13,
        ioa3(3001) + struct.pack("<fB", -0.35, 0x00), 1, test=True))

# Solved case14 state as printed by `gridsim case14 --once` (Vm in pu to 3 dp, Va in degrees to 2 dp).
CASE14_VM = [1.060, 1.045, 1.010, 1.018, 1.020, 1.070, 1.062, 1.090, 1.056, 1.051, 1.057, 1.055, 1.050, 1.036]
CASE14_VA = [0.00, -4.98, -12.73, -10.31, -8.77, -14.22, -13.36, -13.36, -14.94, -15.10, -14.79, -15.08, -15.16, -16.03]

def iec_full():
    """Two type-36 ASDUs in sequence-of-elements form (SQ=1): IOA 1001..1014 are Vm, IOA 1101..1114 are Va."""
    ns = iter(range(100))
    vm = b"".join(struct.pack("<fB", v, 0) + cp56(T1) for v in CASE14_VM)
    va = b"".join(struct.pack("<fB", v, 0) + cp56(T1) for v in CASE14_VA)
    yield "type 36, SQ=1, base IOA 1001, 14 elements: Vm of buses 1..14", i_frame(next(ns), 0, asdu(36, ioa3(1001) + vm, 14, sq=True))
    yield "type 36, SQ=1, base IOA 1101, 14 elements: Va of buses 1..14", i_frame(next(ns), 0, asdu(36, ioa3(1101) + va, 14, sq=True))

# ---------------------------------------------------------------- Modbus TCP builders

def mbap(txn: int, unit: int, pdu: bytes) -> bytes:
    return struct.pack(">HHHB", txn, 0, 1 + len(pdu), unit) + pdu

def read_request(txn: int, unit: int, fc: int, start: int, qty: int) -> bytes:
    return mbap(txn, unit, struct.pack(">BHH", fc, start, qty))

def read_response(txn: int, unit: int, fc: int, regs) -> bytes:
    return mbap(txn, unit, struct.pack(">BB", fc, 2 * len(regs)) + b"".join(struct.pack(">H", r) for r in regs))

def f32_regs(x: float):
    hi, lo = struct.unpack(">HH", struct.pack(">f", x))
    return [hi, lo]

def mb_burst_a():
    yield "noise before a frame (3 octets)", b"\xaa\xbb\xcc"
    yield "FC3 request txn 1: unit 1, start 100, quantity 4", read_request(1, 1, 3, 100, 4)
    yield "FC3 response txn 1: 1030, -120 (0xFF88), 123.456 as F32 high word first", read_response(1, 1, 3, [1030, 0xFF88] + f32_regs(123.456))

def mb_burst_b():
    yield "FC3 request txn 2: unit 1, start 100, quantity 4", read_request(2, 1, 3, 100, 4)
    yield "FC3 response txn 2: 1031, -100 (0xFF9C), 130.5 as F32", read_response(2, 1, 3, [1031, 0xFF9C] + f32_regs(130.5))
    yield "FC3 response txn 9, no request seen: unit 2, 2 registers (positional)", read_response(9, 2, 3, [11, 22])
    yield "FC3 response txn 10, no request seen: unit 2, 3 registers (refused)", read_response(10, 2, 3, [1, 2, 3])

# ---------------------------------------------------------------- driver

def connect(host: str, port: int, wait_s: float = 15.0) -> socket.socket:
    deadline = time.time() + wait_s
    while True:
        try:
            return socket.create_connection((host, port), timeout=5)
        except OSError:
            if time.time() > deadline:
                raise
            time.sleep(0.1)

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("protocol", choices=["iec104", "modbus", "iec104-full"])
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--pause", type=float, default=2.6, help="seconds between the two bursts (must exceed --window-ms)")
    args = ap.parse_args()

    a, b = {"iec104": (iec_burst_a, iec_burst_b), "modbus": (mb_burst_a, mb_burst_b),
            "iec104-full": (iec_full, lambda: iter(()))}[args.protocol]
    s = connect(args.host, args.port)
    n = 0
    for label, burst in (("burst A", a), ("burst B", b)):
        print(f"-- {label}")
        for what, frame in burst():
            s.sendall(frame)
            n += 1
            print(f"  {frame.hex(' ')}   # {what}")
            time.sleep(0.05)
        if label == "burst A":
            time.sleep(args.pause if args.protocol != "iec104-full" else 0)
    time.sleep(0.5)      # let the interposer read the last frame before the socket closes
    s.close()
    print(f"-- sent {n} write(s)")
    return 0

if __name__ == "__main__":
    sys.exit(main())
