### protection-eval

`gridsim protection-eval <case> --devices <set.json> (--bus <id> | --branch <i> --fraction <d>) [--type 3ph|slg|ll|llg] [--xf 0] [--energised true|false] [--curves <dir>]`

`gridsim protection-eval <case> --devices <set.json> --swing <trace.csv|result.json> [--curves <dir>]`

Every placed device measured on the phasors of one fault (RC5 WP4, Core `GridSim.Core.Protection.Placed`). The prefault
power flow is solved on the fault network (the split copy for a fault along a line) and must converge; the fault is the
three-phase study for `3ph` and the sequence-network study for `slg`, `ll` and `llg`, through Zf = j`--xf`. Each relay
uses its own bus voltage and the branch current into the branch at its end. 50/51 operate on the largest phase current
and 50N/51N on |3I0|, both on their curves (IEC 60255-151, IEEE C37.112). A `67` with quadrature polarisation is a phase
element (Ia with Vbc, Ib with Vca, Ic with Vab), with zero-sequence polarisation a 67N (3I0 against -3V0), with
negative-sequence polarisation a 67Q (I2 against -V2); it is forward when cos(arg Iop - arg Vpol - RCA) > 0 and operates
only when the direction matches its setting and its overcurrent element operates; below `minPolarisingV` it reports no
polarising voltage. A `21` measures the phase loops (Vx - Vy) / (Ix - Iy) and the earth loops Vp / (Ip + k0 3I0), the
earth loops released only when |3I0| is above 10 % of the largest phase current, against mho circles (operate when
Re(Z conj(Zr - Z)) >= 0) or quadrilaterals (reactance line, two resistive blinders parallel to the zone angle, and the
directional limits); the fastest zone hit over all loops wins. A loop whose voltage is at or below 1e-6 of its nominal
(1 pu for an earth loop, sqrt(3) pu for a phase loop) has collapsed, a bolted fault at the relay point where V / I is
rounding noise: its direction is taken from the prefault (memory) voltage, forward when Vmem / I lies within 90 degrees
of the zone-1 angle, and the loop is then in every zone of that direction (reported as Z = 0, with "close-in fault, loop
voltage collapsed" in the reason). The generic `21` of `protection-check` and `fault`, which has no memory voltage,
reports such an end as not measured. An `87` is a dual-slope biased line differential on the two
ends of its own branch (Id = |I_near + I_far|, Ib = (|I_near| + |I_far|) / 2; operate above k1 Ib + Is1 up to Is2 and
above k2 Ib - (k2 - k1) Is2 + Is1 beyond), instantaneous, with no charging-current compensation. The result names, per
device, the element, the measured quantity (A or ohm secondary), the loop or phase, the zone, the direction and the time.
Converter-interfaced units keep their prefault current in an unbalanced study and are named in a note.

The optional `distance` members (see [`devices-check`](#devices-check)) work as follows:
- A reverse zone tests -Z against its characteristic. It trips on its own `timeS` (a time-delayed back-up for faults
  behind the relay), so it counts in the time and in `protection-grade` like any other zone.
- With `mutual`, the earth loop current adds k0m 3I0m, where I0m is the parallel line's zero-sequence current at the
  relay's bus.
- With `loadEncroachment`, a phase loop with |Z| >= `minLoadOhms` within `fwdAngleDeg` of 0 degrees or `revAngleDeg` of
  180 degrees is blocked.
- With `sotf` and `--energised true`, the relays on the faulted branch (the line just closed) trip at 0 s: mode `Z2` when
  zone 1 or 2 picks up, and mode `nondirectional` when a phase current exceeds the CT primary. Relays on other lines keep
  their zone timers. `--energised true` needs `--branch`; with `--bus` it is a usage error, since no line was closed.
- With a `scheme`, each end's aided trip time follows: DUTT t_Z1,r + channel; PUTT max(p_Z2, t_Z1,r + channel); POTT
  max(p_Z2, p_Z2,r + channel); DCB p_Z2 + coordination unless a block is received. The remote end sends the block when
  its reverse zone picks up and its zone 1 or zone 2 does not (the forward pickup stops the carrier). The carrier stop
  and the POTT permissive come from the remote end's faulted-phase loops only. The pickup p is 0 s when
  the element picks up. The end trips at the smaller of its zone time and this time, and a line `scheme ...` is printed
  for it.

`--swing <trace>` takes no fault. It reads a bus-voltage trace in one of two forms:
- a `dynamics-study` result file (JSON), read as it is: the one simulated case's `timeSeconds`, and each pair of
  `busVoltage N` channels (unit `pu` for the magnitude, `deg` for the angle) as bus N. A result with more than one
  simulated case is refused, so keep one fixed-time case in the study;
- a CSV: lines starting `#` are skipped; the header is `t_s` then `vm_<bus>` (pu) and `va_<bus>` (degrees).

The times must increase. For each 21 with `powerSwing` it forms Z(t) = V / I at the relay, with the current from the
branch's pi stamp on its two bus voltages. The stamp uses the case's fixed tap, so a tap changer that moves during the
RMS run is not followed. It then runs the
two-blinder detector: a pass that stays between the outer and inner rectangles (|R| <= R, |X| <= X) for `deltaTS` is a
swing. The `blockZones` are then blocked until Z leaves the outer rectangle or `unblockS` has passed. One line is printed
per pass. A relay whose two buses are not both in the trace is listed as not evaluated.

Not modelled: fault resistance in the unbalanced study (Zf is a reactance), channel failure, the current-reversal guard,
echo and weak-infeed logic, out-of-step tripping, swing detection during an unbalanced fault, CVT transients, 87T and
87B. Exit 0 evaluated; 3 the fault could not be applied; 1 invalid input (including an unreadable trace); 2 usage
(including `--swing` with a fault flag, and `--energised true` with `--bus`).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (case, fault, type, xf, fault bus) and the table `devices` (every row printed, with the
measured values in round-trip form). When a device carries a scheme, the table adds the columns `scheme`, `aided` and
`aided_trip_s`. With `--swing`, the summary is the case, `mode` = `swing` and the sample count, and the table is `swing`
(`device`, `pass`, `outer_entry_s`, `inner_entry_s`, `swing`, `block_from_s`, `block_to_s`, `release`, `block_zones`,
`reason`).

| Flag | Default | Meaning |
|---|---|---|
| `--devices` | required | The `gridsim-devices/1` file. |
| `--bus` | - | Fault bus ID. Exactly one of `--bus` or `--branch`. |
| `--branch` | - | Branch index for a fault along a line; with `--fraction`. |
| `--fraction` | 0.5 | Position along the branch in [0, 1]. |
| `--type` | `3ph` | `3ph`, `slg` (phase a to earth), `ll` (b to c) or `llg` (b and c to earth). |
| `--xf` | 0 | Fault reactance, pu (Zf = j xf). |
| `--energised` | `false` | `true` marks the faulted branch as just closed onto the fault, for the `sotf` setting of the 21s on it. Needs `--branch`. |
| `--swing` | - | A bus-voltage trace for power-swing blocking (CSV or a `dynamics-study` result file); takes no fault flags. |
| `--curves` | installed data | Curve data directory. |
