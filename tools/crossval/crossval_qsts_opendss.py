"""OpenDSS reference for the QSTS cross-check (GridSim QstsOpenDssCrossCheckTests).

Builds, in OpenDSS (through OpenDSSDirect.py), the balanced radial test feeder of QstsEngineTests.Feeder: a stiff
33 kV source, the grid transformer T1 (33/11 kV, 10 MVA, R 0.5 %, X 8 %, taps on the HV winding, 16 steps of 1.25 %
over 0.9..1.1) with a regulator holding the 11 kV bus at TARGET +/- DEADBAND pu, four 11 kV sections (0.363 + j0.484
ohm, no charging, zero sequence equal to positive so the balanced solution is the positive-sequence one) and
1 MW + 0.3 MVAr constant-power loads at buses 3 to 6, plus a 0.3 MVAr capacitor at bus 6 under voltage control.
A daily load shape (GridSim LoadProfile.DailyFactor at 15 minute samples) drives the loads.

Two runs are written to data/qsts/:
  opendss-feeder-static.csv  - ControlMode=STATIC (controls settle at every step, delays only order actions:
                               capacitor before regulator, as GridSim does), 96 steps of 15 min;
  opendss-feeder-time-off096.csv - the TIME window again with the capacitor OFF setting at 0.96 pu;
  opendss-feeder-time.csv    - ControlMode=TIME with regulator delay 30 s / tap delay 5 s and capacitor delay 60 s,
                               a 20 minute window at 1 s steps across a load step.
Each row: step, time_s, load multiplier OpenDSS applied (read back from the loads), the six bus voltages (pu, phase A,
positive sequence equal by construction), tap ratio of T1 winding 1, capacitor state, and the cumulative tap and
capacitor operation counts. A header comment records the OpenDSS and OpenDSSDirect.py versions.

Usage: python -I tools/crossval/crossval_qsts_opendss.py <repo root>   (needs opendssdirect.py in the interpreter)
"""
import math
import os
import sys

import opendssdirect as dss

TARGET, DEADBAND = 1.02, 0.01   # band 0.02 pu, wider than one tap step (about 0.0127 pu), so no hunting
CAP_ON, CAP_OFF = 0.94, 0.97
SOURCE_MVASC = 1e13  # a near-ideal source (GridSim holds the slack bus at exactly 1.0 pu)
LOAD_SCALE = 1.8   # the daily shape (0.6..1.0) times 1.8: 1.08..1.8 MW per load bus
VLN = 11000.0 / math.sqrt(3.0)
PT = VLN / 120.0


def daily_factor(hour):
    # GridSim LoadProfile.DailyFactor: 0.60 + 0.40 * (0.5 - 0.5 cos(2 pi x)), x = hours since 04:00 / 24.
    x = ((hour - 4 + 24) % 24) / 24.0
    return 0.60 + 0.40 * (0.5 - 0.5 * math.cos(2 * math.pi * x))


def build(mults, interval_h, reg_delay, tap_delay, cap_delay):
    dss.Basic.ClearAll()
    c = dss.Text.Command
    c(f"new circuit.qsts basekv=33 pu=1.0 angle=0 phases=3 bus1=src MVAsc3={SOURCE_MVASC!r} MVAsc1={SOURCE_MVASC!r}")
    c("new transformer.T1 phases=3 windings=2 buses=[src b2] conns=[wye wye] kvs=[33 11] kvas=[10000 10000] "
      "%rs=[0.25 0.25] xhl=8 %noloadloss=0 %imag=0 ppm_antifloat=0 taps=[1 1]")
    # Tap properties are per winding in OpenDSS (set on the active winding): 16 steps of 1.25 % on both.
    c("transformer.T1.wdg=1 numtaps=16 maxtap=1.1 mintap=0.9 wdg=2 numtaps=16 maxtap=1.1 mintap=0.9")
    for i in range(2, 6):
        c(f"new line.L{i}{i + 1} phases=3 bus1=b{i} bus2=b{i + 1} r1=0.363 x1=0.484 r0=0.363 x0=0.484 c1=0 c0=0 length=1 units=none")
    npts = len(mults)
    # An OpenDSS fixed-interval load shape applies point i at hour (i + 1) * interval, so the shape is rotated by one
    # point: the multiplier OpenDSS uses at t = k * dt is then mults[k], the value GridSim's hold profile has at k * dt.
    shifted = mults[1:] + mults[:1]
    c(f"new loadshape.ls npts={npts} interval={interval_h!r} mult=({' '.join(repr(m) for m in shifted)})")
    for i in range(3, 7):
        c(f"new load.L{i} phases=3 bus1=b{i} kv=11 kw=1000 kvar=300 model=1 vminpu=0.5 vmaxpu=1.5 daily=ls")
    c("new capacitor.C6 phases=3 bus1=b6 kv=11 kvar=300")
    c(f"new capcontrol.CC6 element=capacitor.C6 capacitor=C6 type=voltage ptratio={PT!r} "
      f"ONsetting={120 * CAP_ON!r} OFFsetting={120 * CAP_OFF!r} delay={cap_delay} delayoff={cap_delay} deadtime=0")
    c(f"new regcontrol.R1 transformer=T1 winding=2 tapwinding=1 vreg={120 * TARGET!r} band={120 * 2 * DEADBAND!r} "
      f"ptratio={PT!r} delay={reg_delay} tapdelay={tap_delay} reversible=no")
    c("set voltagebases=[33 11]")
    c("calcvoltagebases")
    c("set maxcontroliter=100")
    c("set tolerance=1e-10 maxiterations=100")   # OpenDSS default 1e-4 pu would dominate the comparison
    c("capacitor.C6.states=[0]")


def snapshot(step, t, mult_intended):
    vs = []
    for b in ["src", "b2", "b3", "b4", "b5", "b6"]:
        dss.Circuit.SetActiveBus(b)
        vs.append(dss.Bus.puVmagAngle()[0])
    dss.Transformers.Name("T1")
    dss.Transformers.Wdg(1)
    tap = dss.Transformers.Tap()
    dss.Capacitors.Name("C6")
    cap = dss.Capacitors.States()[0]
    dss.Loads.Name("L3")
    dss.Circuit.SetActiveElement("load.L3")
    # The multiplier read back from the load (its P over 1 MW), a check that the shape lines up with the intended one.
    readback = sum(dss.CktElement.Powers()[0:6:2]) / 1000.0
    return [step, t, mult_intended, readback] + vs + [tap, cap]


def run(mults, interval_h, steps, step_s, mode, reg_delay, tap_delay, cap_delay):
    build(mults, interval_h, reg_delay, tap_delay, cap_delay)
    c = dss.Text.Command
    c(f"set controlmode={mode}")
    c(f"set mode=daily stepsize={step_s}s number=1")
    c("set hour=0 sec=0")
    rows = []
    taps, caps = 0, 0
    last_tap, last_cap = None, None
    for k in range(steps):
        # OpenDSS advances the clock by one step before it solves; start one step early so step k is solved at k * dt.
        if k == 0:
            c(f"set hour=0 sec={-step_s}")
        dss.Solution.Solve()
        if not dss.Solution.Converged():
            raise SystemExit(f"OpenDSS did not converge at step {k}")
        t = round(dss.Solution.Hour() * 3600 + dss.Solution.Seconds())
        row = snapshot(k, t, mults[k % len(mults)])
        tap, cap = row[10], row[11]
        taps += round(abs(tap - (1.0 if last_tap is None else last_tap)) / 0.0125)
        caps += abs(cap - (0 if last_cap is None else last_cap))
        last_tap, last_cap = tap, cap
        rows.append(row + [taps, caps])
    return rows


def write(path, rows, note):
    with open(path, "w", newline="\n") as f:
        version = " | ".join(x.strip() for x in dss.Basic.Version().splitlines() if x.strip())
        f.write(f"# OpenDSS {version}\n")
        f.write(f"# OpenDSSDirect.py {dss.__version__}\n")
        f.write(f"# {note}\n")
        f.write("step,time_s,load_mult,load_mult_readback,v_src,v_b2,v_b3,v_b4,v_b5,v_b6,tap,cap,tap_ops,cap_ops\n")
        for r in rows:
            f.write(",".join(repr(x) if isinstance(x, float) else str(x) for x in r) + "\n")


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else "."
    out = os.path.join(root, "data", "qsts")
    os.makedirs(out, exist_ok=True)
    mults = [LOAD_SCALE * daily_factor(i / 4.0) for i in range(96)]
    # In STATIC mode the delays only order the actions within a step: capacitor 15 s before regulator 30 s, the order
    # GridSim's timed engine uses (banks before tap changers).
    rows = run(mults, 0.25, 96, 900, "static", 30, 30, 15)
    write(os.path.join(out, "opendss-feeder-static.csv"), rows, "ControlMode=STATIC, 96 x 900 s, daily shape at 15 min, delays (ordering only) capacitor 15 s, regulator 30 s")
    # TIME mode: 20 minutes at 1 s; the load steps from 0.6 to 1.0 (times LOAD_SCALE) at t = 60 s and back at t = 600 s.
    step_mults = [LOAD_SCALE * 0.6] * 60 + [LOAD_SCALE * 1.0] * 540 + [LOAD_SCALE * 0.6] * 600
    rows = run(step_mults, 1.0 / 3600.0, 1200, 1, "time", 30, 5, 60)
    write(os.path.join(out, "opendss-feeder-time.csv"), rows, "ControlMode=TIME, 1200 x 1 s, reg delay 30 s tapdelay 5 s, cap delay 60 s")
    # The same window with the capacitor OFF setting at 0.96 pu, which bus 6 crosses at t = 35 s while the bank is
    # open: shows how OpenDSS times a bank from any exit of its band (see QstsOpenDssCrossCheckTests).
    global CAP_OFF
    keep, CAP_OFF = CAP_OFF, 0.96
    rows = run(step_mults, 1.0 / 3600.0, 1200, 1, "time", 30, 5, 60)
    CAP_OFF = keep
    write(os.path.join(out, "opendss-feeder-time-off096.csv"), rows, "ControlMode=TIME, 1200 x 1 s, reg delay 30 s tapdelay 5 s, cap delay 60 s, cap OFF 0.96 pu")


if __name__ == "__main__":
    main()
