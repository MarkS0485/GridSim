### line-constants

`gridsim line-constants <geometry.json> [--length-km <km> --base-kv <kV> --base-mva <MVA>]`

`gridsim line-constants --cable <cables.json> [--length-km <km> --base-kv <kV> --base-mva <MVA>]`

Overhead line constants from conductor geometry (Core `GridSim.Core.Lines`): the series impedance and potential
coefficient matrices of the cross-section, reduced to what a power-flow or fault study needs. `<geometry.json>` is a
strict `gridsim-lineconstants/1` document (an unknown or missing key, or a wrong type, exits 1 naming it):

```json
{
  "schema": "gridsim-lineconstants/1",
  "method": "perfect-ground",
  "frequency_hz": 50,
  "earth_resistivity_ohm_m": 100,
  "transposed": true,
  "conductors": [
    {"name": "A", "role": "phase", "x_m": -3.0, "height_m": 12.0,
     "resistance_ohm_per_km": 0.06, "gmr_m": 0.011, "radius_m": 0.014}
  ]
}
```

`method` is `perfect-ground` (image conductors only; earth resistivity is ignored and may be left out) or `earth-integral`
(the exact Pollaczek and Carson earth-return integral by fixed-order quadrature, with its truncation bound reported;
`earth_resistivity_ohm_m` is then required). The series expansions of Carson's integral (`carson-series` and
`carson-modified` in the plan) are not implemented. `role` is `phase` or `ground-wire`; a ground wire is eliminated by
Kron reduction. `resistance_ohm_per_km` is the AC resistance at the stated temperature, an input: no skin-effect or
temperature model is applied. `gmr_m` is the geometric mean radius used for the self reactance and `radius_m` the physical
radius used for the capacitance. `transposed` (default true) averages the phase matrices over a full transposition before
the sequence transform; for an untransposed three-phase line the sequence values use the mean self and mean mutual terms,
stated in `sequence_note`.

The calculation runs in a fixed order and every stage is kept in the result: primitive Z (ohm/km) and Maxwell potential
coefficients P (km/F) over every conductor; the Kron-reduced phase Z and the phase capacitance C (F/km, the inverse of the
reduced P); the transposition average; the sequence constants Z1, Z0 and C1, C0 (three phase conductors only). With
`--length-km`, `--base-kv` and `--base-mva` (all three) the positive-sequence R1, X1 and B1 are also given in per unit
(Zbase = kV squared over MVA; B = omega C1 length Zbase). Exit 0 done; 1 the file is missing or not a valid geometry, or
the geometry is unusable (no phase, coincident or overlapping conductors, a conductor not wholly above ground, a
non-positive frequency); 2 usage (a missing file, a flag the verb does not take, one or two of the three per-unit flags, a
value that is not above 0); 3 per-unit values were asked for but the line has not three phase conductors (the matrices are
still printed and written).

| Flag | Default | Meaning |
|---|---|---|
| `--length-km` | - | Line length for the per-unit values (km). Needs `--base-kv` and `--base-mva`. |
| `--base-kv` | - | Base voltage for the per-unit values (kV). |
| `--base-mva` | - | Base power for the per-unit values (MVA). |
| `--cable` | - | Read a `gridsim-cableconstants/1` file of buried single-core cables instead of an overhead geometry (see below). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (method, frequency, sequence constants, tail bound and, when asked, the
per-unit values) and one table per stage (`primitive_z`, `primitive_p`, `phase_z`, `phase_c`, `transposed_z`,
`transposed_c`), each as `row`, `col`, value rows.

#### Single-core cables (`--cable`)

With `--cable <cables.json>` the verb solves buried single-core cables, each with a core, an optional metallic sheath and
an optional armour, instead of an overhead line. The file is a strict `gridsim-cableconstants/1` document:

```json
{
  "schema": "gridsim-cableconstants/1",
  "earth": "wedepohl",
  "bonding": "both",
  "frequency_hz": 50,
  "earth_resistivity_ohm_m": 100,
  "transposed": true,
  "cables": [
    {"name": "A", "x_m": -0.1, "depth_m": 1.0, "outer_radius_m": 0.0365,
     "conductors": [
       {"inner_radius_m": 0, "outer_radius_m": 0.0145, "resistivity_ohm_m": 1.72e-8,
        "insulation": {"relative_permittivity": 2.5, "loss_tangent": 0.0004}},
       {"inner_radius_m": 0.0315, "outer_radius_m": 0.0325, "resistivity_ohm_m": 2.83e-8,
        "insulation": {"relative_permittivity": 2.3}}]}
  ]
}
```

Each cable lists its conductors from the inside out: the core first (an `inner_radius_m` of 0 is a solid core, above 0 a
hollow one), then the sheath, then the armour. `insulation` is the layer outside that conductor; it runs to the next
conductor's inner radius, or to `outer_radius_m` for the outermost layer, so every cable needs an outer jacket.
`relative_permeability` (conductor or insulation) defaults to 1 and `loss_tangent` to 0. A stranded core is entered with
the resistivity that gives its DC resistance on the stated radius. Semiconducting screens count as part of the
insulation: enter a permittivity already corrected for them if a correction is wanted. `depth_m` is the depth of the
cable's centre below the surface.

The calculation follows Ametani's general formulation (IEEE Trans. PAS-99, 1980) and the cable constants chapter of the
EMTP Theory Book. Each conductor's surface impedances are Schelkunoff's, with the modified Bessel functions of complex
argument computed natively; the loop impedances (core to sheath, sheath to armour, outermost layer to earth) are
transformed to the conductor matrix; the insulation gives C = 2 pi eps0 eps_r / ln(r_o / r_i) and G = omega C tan(delta)
per layer. `earth` picks the earth-return formula: `wedepohl` is Wedepohl and Wilcox's closed form, which is accurate
while |m_e| times the larger of the spacing and the depth sum is small (a warning is printed above 0.05, where its error
reaches about 0.1%); `pollaczek` is the exact Pollaczek integral by fixed-order quadrature, with its truncation bound
reported. `bonding` is `both` (sheaths and armour earthed at both ends and eliminated by Kron reduction), `single` (bonded
at one point: no current in the metallic layers) or `cross` (ideal cross-bonding: the impedance averaged over the three
positions each metallic layer takes along a major section, then eliminated; three cables with the same metallic layers:
a sheath, and an armour on all or none). In every
case the metallic layers are at earth potential for the capacitance, so the phase C and G are those of the core
insulation. `transposed` (default true) works as for overhead lines.

Every stage is kept: the primitive Z and P over every conductor, named `<cable>.core`, `<cable>.sheath` and
`<cable>.armour`; the conductor admittance (`conductor_y`, G and B in S/km); the bonded phase Z, C and G (`phase_g`); the
transposition average and the sequence constants. The summary adds `earth`, `bonding`, `cables`,
`wedepohl_argument_max` and `warnings`. Exit codes are as for overhead lines; a cable that overlaps another, lies above
ground, has layers out of order, or a cross-bonded system that is not three cables with the same metallic layers, exits 1
naming it.
Scope: single-core cables in homogeneous earth. Pipe-type and three-core cables are not modelled, and there is no
proximity effect between cables beyond the earth-return coupling. The values are verified against closed forms (the DC
resistance, the skin-effect asymptote, the coaxial capacitance) and against an independent 30-digit mpmath calculation of
the same formulas; no published worked example with complete data was available to compare against.
