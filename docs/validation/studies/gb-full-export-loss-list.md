# gb-full through every writer: what each format carries

This page records what happens when the full GB case (`gb-full`: 3,539 buses, 5,318 generators, 5,918 branches, 1,383
on-load tap changers, 5 boundaries, the asset-dynamics and protection bundles) is written by each of GridSim's case
writers with `gridsim convert gb-full <output>`. Each conversion is proved: the written file is read back and both are
solved with the contingency study options and the OLTC loop, and every bus voltage, angle, injection, line flow and the
losses are compared bit for bit ([manual chapter 19](<../../manual/0019 CLI reference.md#convert>)). Measured on GridSim
2.0.170.

## Summary

| Output | Writer | Exit | Result |
|---|---|---|---|
| `gb-full.case.json` | JSON v2 (`V2CaseWriter`) | 0 | Bit-identical solve, fault sequence data read back bit for bit. Nothing lost. |
| `gb-full.m` | MATPOWER (`MatpowerWriter`) | 3 | Written; the solve differs because MATPOWER has no tap changers. |
| `gb-full.raw` | PSS/E RAW 33 (`PsseRawWriter`) | 3 | Written; the solve differs because the tap-changer bands cannot be expressed exactly. |
| `gb-full.xml` | CGMES (`CgmesWriter`) | 1 | Refused, nothing written. |

## The tap changers move, so dropping them moves the answer

With its 1,383 tap changers, the gb-full solve runs the OLTC loop for 4 re-solve passes and moves the tap on 936
transformers before it settles (71 tap changers are held because their controlled bus is generator-regulated, none is
at a limit). A format that cannot carry the tap changers leaves every tap at its starting position, so the written case
solves to a different state. For both the MATPOWER and the RAW output the proof reports:

- first difference in bus order: Vm at bus 1010, 1.002217402005259 vs 1.0022174020052588 pu (one ulp);
- largest difference: |dVm| = 0.05984993965502905 pu at bus 4105 (1.003220700640626 pu with the taps regulated,
  0.9433707609855969 pu with the taps fixed), and |dVa| = 1.0842401116068583 degrees at bus 4105;
- 3,535 of the 3,539 buses differ.

The same case with its tap-changer records removed solves to exactly the MATPOWER read-back (every voltage and angle
bit for bit, and no bus, generator or branch record differs), so the tap changers are the whole difference.

## Per format

**JSON v2** carries every element the power flow reads, including the tap changers, and the fault sequence data
(generator X''d and X2, transformer windings, zero-sequence impedances and neutral reactances). The round trip is exact
on both, with no note.

**MATPOWER** (version 2). Not written, each counted in a note: 1,383 on-load tap changers (MATPOWER has none; the
solve differs, above); 5 boundaries; the asset-dynamics and protection bundles; 3,539 bus names and 3,539 layout
coordinate pairs; the name, type, inertia and dynamics or fault data of 5,318 generators; the name, length, equipment,
dynamics class and zero-sequence data of 5,918 branches. Written with a note: no bus carries its own voltage limits, so
VMAX / VMIN are the operational band 1.06 / 0.94 pu that GridSim's checks use.

**PSS/E RAW** (revision 33). Not written, each counted in a note: 1,383 tap changers, left at their present tap,
because each has a target and deadband that RAW's VMA and VMI cannot give back to the bit ((VMA + VMI) / 2 and
(VMA - VMI) / 2; the solve differs, above); 5 boundaries; the asset-dynamics and protection bundles; the dynamics and
fault data of 5,318 generators (RAW holds the power-flow record only).

**CGMES** refuses the case with three reasons, all listed, and writes nothing:

- 1,383 on-load tap changers: GridSim's CIM reader turns a regulating RatioTapChanger into a controlled power flow, not
  the tap-changer loop the case solves with;
- 1 slack bus with no in-service generator (bus 6774): GridSim's CIM reader chooses the angle reference among the
  machines;
- 199 voltage-controlled buses whose machines hold different set-points (1.0 and 1.02 pu at each; the first are buses
  1000, 1004, 1008, 1012 and 1029). GridSim's power flow uses the last unit's set-point at such a bus; CGMES gives each
  machine its own target, so the writer refuses rather than choose.

## Limits

- The four writers are compared on the power flow and, for JSON v2, the fault sequence data. Dynamics, protection and
  the boundaries are counted in notes, not proved.
- Measured with the Release build on one machine; the proof itself is bit-exact, so the counts and differences above do
  not depend on the machine.
