From GridSim technical ch. 2 Data model and per-unit conventions.

## Per-unit conventions

- **One system base.** All power is converted with `GridModel.BaseMva` alone. The specified injection at a bus is
  `Pspec = (sum Pg - Pd) / BaseMva` and `Qspec = (sum Qg - Qd) / BaseMva`
  (`NewtonShell.BuildBusSpec`, `src/GridSim.Core/PowerFlow/NewtonShell.cs`). `Pspec` and `Qspec` hold the nominal load;
  a ZIP load is applied as a deviation inside the iteration.
- **Voltage bases.** `Bus.BaseKv` is the nominal line-to-line voltage of the bus. The solve never reads it. A branch's
  `R`, `X` and `B` are entered per-unit on `BaseMva` and on the voltage bases of its ends, so no impedance conversion
  happens inside the solvers. Converting an impedance given in ohms is a loader job and uses the base impedance of the
  bus voltage:

```
Zbase = BaseKv^2 / BaseMva            ohm   (BaseKv in kV, BaseMva in MVA)
r_pu  = r_ohm / Zbase,   x_pu = x_ohm / Zbase
b_pu  = b_siemens * Zbase             a susceptance converts the other way
```

- **Shunts** are entered in engineering units (MW and MVAr at 1.0 pu) and divided by `BaseMva` at stamp time, the only
  place a bus field is converted inside the Y-bus builders.
- **Voltages** are per-unit throughout; `BaseKv` converts back to kV for display.
- **Angles** are degrees in `Bus.VaDeg` and `PowerFlowResult.VaDeg` and radians inside every solver loop.
- **Results return to engineering units** at assembly: `PInjMw = P_pu * BaseMva`, and line flows likewise
  (`NewtonShell.AssembleResult`, `NewtonShell.ComputeLineFlows`).
- **Convergence tolerances** are per-unit power mismatches; the default `1e-8` is 1 W on a 100 MVA base.

### Transformer conventions

A transformer is one `Branch`. Its off-nominal ratio and phase shift form one complex tap on the **from** side:

```
a   = TapRatio                         (a <= 0 is replaced by 1.0)
phi = PhaseShiftDeg * pi / 180
tap = a * e^(j*phi)                    DetMath.FromPolar(a, phi)
```

The from terminal sees the pi-model through an ideal transformer of complex ratio `tap : 1`, as in MATPOWER; the
to terminal connects to the pi-model directly. A tap of 1.0 with zero shift is a plain line. `Oltc` limits (`MinTap`,
`MaxTap`) are tap ratios in the same convention. The phase shift acts only through the asymmetric mutual terms below.
The magnetising branch is not modelled: no loader sets a shunt on a transformer branch, and `B` is 0 for every transformer a loader builds.
