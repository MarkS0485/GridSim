From GridSim technical ch. 13 Asset-dynamics data grounding: the provenance tags and the grounded value of every class parameter. A value tagged CuratedPlaceholder carries [VERIFY]: cite it as a placeholder, never as a measured value.

### Provenance tags

Every parameter of an authored class has one provenance tag (`ProvenanceTag`, `src/GridSim.Core/Diagnostics/Provenance.cs`).
Three tags occur in the bundled asset classes, and the loader accepts any `ProvenanceTag` name. A fourth tag,
`UnvalidatedAtScale`, tags fleet-level results:

| Tag | Meaning in an asset class | Note must contain `[VERIFY]` |
|---|---|---|
| `DerivedFromRecorded` | Computed from recorded data (a ratio, a median, a coincident block) | no |
| `CuratedPlaceholder` | Stands in for data that is not available; vendor-proprietary or absent from every source | yes |
| `UnvalidatedAtScale` | A fleet-level result whose absolute value at GB scale has no reference | not applicable |

`Synthetic`, `Counterfactual` and `Interpolated` are valid tags for evidence series
([ch. 7](<0007 The evidence engine.md>)) but are not used by asset classes. `UnvalidatedAtScale` tags a result, not a
parameter: the 2019-08-09 cascade reproduction is tagged `UnvalidatedAtScale` because its depth depends on the
embedded-generation volume that tripped, which no source records.

A class lists its provenance as a block of `parameter -> { tag, note }` entries. `AssetClass.ProvenanceFor(key)`
resolves a parameter in this order:

1. the entry named for the parameter;
2. the entry named `_default`;
3. a synthetic `CuratedPlaceholder` with the note `[VERIFY] unspecified provenance`.

A parameter with no entry of its own therefore inherits the class `_default`, which is a `CuratedPlaceholder` in every
bundled class that has one. The two protection classes in `gb-synthetic` have no `_default`: a parameter they do not
list (`rocofTripConfirmS` of `prot-lom-legacy`, `lfddDwellS` of `prot-lfdd-stage1`) resolves to the synthetic
placeholder.

## Grounded values per class

Tags are those in the file. "gf" is `gb-full`, "gs" is `gb-synthetic`. A parameter marked `P` is a `CuratedPlaceholder`
(its note carries `[VERIFY]`), explicit or inherited from `_default`.

### Recorded and derived parameters

| Parameter | Value | Tag | Classes (dataset) | Source |
|---|---|---|---|---|
| `rocofTripHzPerS` (modern) | 1.0 Hz/s | Recorded | `gfl-wind-2024` (gf, gs), `gfl-solar-2024` (gf, gs), `hvdc-2024` (gf), `dg-alomcp` (gs) | `dg-alomcp` cites EREC G99 Issue 2 (S124) Table 10.1, p.125; `gfl-wind-2024` cites the modern GB LoM setting (`FrequencyLimits.RocofLimitModernHzPerS`) or ALoMCP; `gfl-solar-2024` and `hvdc-2024` cite the modern GB LoM setting. The value matches S124 p.125. |
| `vectorShiftTripDeg` | 6.0 degrees | Recorded | `legacy-dg-old-lom` (gf, gs) | G59 Issue 3 Amendment 8 (S135), Historic Vector Shift Settings, p.63: K1 x 6 degrees with K1 = 1.0 (low-impedance network); not allowed on or after 01/09/22 and not acceptable under G99 Issue 2 (S124) clause 10.4.11 |
| `deadbandHz` | 0.015 Hz | Recorded | `gfl-wind-2024` (gf, gs) | NESO frequency-response service deadband; `P` on the other classes that carry the same value |
| `lfddStageHz` | 48.8 Hz | Recorded | `prot-lfdd-stage1` (gs) | Grid Code Issue 6 Revision 16 (S076) CC.A.5.5.1, Table CC.A.5.5.1a (PDF p.264); `FrequencyLimits.LfddFirstStageHz` |
| `targetVpu` | 1.0 pu | Recorded | `tx-oltc-2024` (gf) | nominal voltage target |
| `tapStepPu` | 0.0125 pu | DerivedFromRecorded | `tx-oltc-2024` (gf) | SCADA-recovered OLTC step, 1.0 to 1.25 % nominal, `Derived/tap_changers/tap_changers.parquet` `step_pct` (0.46 % observed) |
| `minTap`, `maxTap` | 0.85, 1.15 | DerivedFromRecorded | `tx-oltc-2024` (gf) | LTDS tap span, `ltds_tap_span_pct` median 31 %, and `NetworkModel/normalized/<licence>_tx2.parquet` tap positions (about 16 steps, -12 to +4) |
| `derateAtFullThermal` (cable) | 0.11 | DerivedFromRecorded | `cable-thermal-2024` (gf) | seasonal derate: summer rating is 0.89 x winter, `<licence>_circuits.parquet` `summer_rating_mva / winter_rating_mva` (0.86 to 0.91; NPGN 1.0) |

The legacy and modern relay values are the two ends of the loss-of-mains population that the 2019-08-09 cascade turns
on: `legacy-dg-old-lom` is the pre-ALoMCP tripping population, `dg-alomcp` the population upgraded under the
Accelerated Loss-of-Mains Change Programme.

### Placeholder parameters (`CuratedPlaceholder`, `[VERIFY]`)

| Class | Placeholder parameters and values |
|---|---|
| `gfl-wind-2024` | `droopGainMwPerHzPerMw` 2.0; `rocofGainMwsPerHz` 0.0 (gs only); `detectionLatencyS` 0.10; `computeLatencyS` 0.02; `actuateTauS` 0.30; `fastSlewUpMwPerSPerMw` 1.0; `fastSlewDownMwPerSPerMw` 1.0; `headroomUpFraction` 0.10; `headroomDownFraction` 0.10; `lvrtVoltagePu` 0.80; `rocofTripConfirmS` 0.5 in gs |
| `gfl-solar-2024` | `deadbandHz` 0.015; `droopGainMwPerHzPerMw` 2.0; `detectionLatencyS` 0.12; `computeLatencyS` 0.02; `actuateTauS` 0.30; `headroomUpFraction` 0.05; `headroomDownFraction` 0.05; `lvrtVoltagePu` 0.80; `rocofTripConfirmS` 0.5 in gs |
| `gfm-bess-2024` | `synInertiaMwsPerHz` 8.0; `dampingMwPerHz` 15.0; `deadbandHz` 0.0; `actuateTauS` 0.05; `headroomUpFraction` 0.20; `headroomDownFraction` 0.20; `capacityMwh` 2.0; `socInit` 0.55; `socMin` 0.05; `socMax` 0.95; `rocofTripHzPerS` 2.5; `rocofTripConfirmS` 1.0; `lvrtVoltagePu` 0.50 |
| `gfm-vsm-study-2024` (gs) | `synInertiaMwsPerHz` 120.0; `dampingMwPerHz` 40.0; `deadbandHz` 0.0; `actuateTauS` 0.05; `headroomUpFraction` 1.0; `headroomDownFraction` 1.0; `capacityMwh` 0.0; `rocofTripHzPerS` 1000000000.0; `lvrtVoltagePu` 0.0 (an idealised ride-through former with no SoC cap, for the H=0 counterfactual) |
| `hvdc-2024` (gf) | `deadbandHz` 0.015; `droopGainMwPerHzPerMw` 3.0; `detectionLatencyS` 0.05; `computeLatencyS` 0.02; `actuateTauS` 0.15; `headroomUpFraction` 0.10; `headroomDownFraction` 0.10 |
| `legacy-dg-old-lom` | `deadbandHz` 0.015; `droopGainMwPerHzPerMw` 0.0; `headroomUpFraction` 0.0; `headroomDownFraction` 0.0 (the fleet has no frequency response); `rocofTripConfirmS` 0.0 in gs |
| `dg-alomcp` (gs) | `deadbandHz` 0.015; `droopGainMwPerHzPerMw` 0.0; `headroomUpFraction` 0.0; `headroomDownFraction` 0.0 |
| `prot-lom-legacy` (gs) | `rocofTripConfirmS` 0.0 (no entry; resolves to the synthetic placeholder) |
| `prot-lfdd-stage1` (gs) | `lfddDwellS` 0.0 (no entry; resolves to the synthetic placeholder) |
| `tx-oltc-2024` (gf) | `tapDeadbandPu` 0.01; `interTapDelayS` 5.0; `initialTapDelayS` 10.0; `thermalTauMin` 30.0; `derateAtFullThermal` 0.1 |
| `cable-thermal-2024` (gf) | `thermalTauMin` 15.0; `ambientLoadingFraction` 0.0 |

These placeholders carry their own note beyond the `_default`: in `gb-full`, `gfm-bess-2024` `synInertiaMwsPerHz` and
`capacityMwh`, `tx-oltc-2024` `interTapDelayS`, `initialTapDelayS`, `thermalTauMin` and `derateAtFullThermal`, and
`cable-thermal-2024` `thermalTauMin`; in `gb-synthetic`, `synInertiaMwsPerHz` of `gfm-bess-2024` and
`gfm-vsm-study-2024`. Three of them carry substance:

- `synInertiaMwsPerHz` (both grid-forming classes): the gain is power per df/dt, not stored energy. In
  `gfm-vsm-study-2024` it is read as `Ev = synInertia * f0 / 2` in the forming denominator.
- `capacityMwh` (`gfm-bess-2024`, gf): the note reads "BESS duration absent from REPD/TEC/DUKES/BMU; assumed C-rate x MW [VERIFY]".
- `derateAtFullThermal` (`tx-oltc-2024`): emergency headroom is 1.58 to 2.0 x nominal
  (`<licence>_tx2.parquet` `emergency_rating_mva / nominal_rating_mva`, median 1.583), which grounds a steady-state
  figure, not the 0.1 sustained derate.

### Quantities with no source

- **Inverter internal control loops.** Droop and df/dt gains, detection, compute and actuate latencies, PLL and
  current-loop time constants, fast-slew MW/s, LVRT/FRT behaviour are vendor-proprietary.
- **BESS `capacityMwh` and duration.** The MW sizing is grounded (REPD operational batteries: 171 sites, 4,755 MW,
  median 23 MW); the duration is not.
- **The legacy-versus-upgraded embedded-generation fraction.** The relay thresholds are recorded; no register says how
  much embedded generation was upgraded under ALoMCP. It is the highest-leverage knob of the RoCoF-trip cascade, which is
  why a real-fleet claim is a parameter-sweep envelope (`AssetEnvelope`, `AssetEnvelopeResult.CascadeIsPossible`)
  and not a point prediction.
- **Transformer MW and MVAr loading.** There is no `transformer_flows` product, only a BSP power-factor summary (mean
  0.95, median 0.98, so MVA is close to MW at most BSPs).
- **Inter-domain propagation delay.** `Derived/propagation_delays` is synthetic (`is_synthetic_speed=True`, literature
  band 500 to 1500 km/s, 5 to 8 ms per corridor).
- **Protection settings with no operational data.** Recloser, auto-reclose and breaker operation (only customer-outage
  counts exist), under- and over-voltage (no measured GB voltage series), thermal and reactive overload, and fault level.
  The recloser mechanism is modelled in full; its settings are assumed.
- **Impedance-screen converter parameters.** `pllBandwidthHz`, `pllDampingRatio`, `currentLoopBandwidthHz`,
  `virtualReactancePu` and `powerLoopBandwidthHz` are set by no bundled class. The impedance screen takes the
  `ConverterDefaults` literature values (PLL bandwidth 20 Hz, the one default EMT, impedance and small-signal share; damping 0.707) and labels the result
  `CuratedPlaceholder`; it labels it `Recorded` only when the bandwidth came from a class whose provenance for
  `pllBandwidthHz` is `Recorded` or `DerivedFromRecorded` and no override was typed. The same rule applies to
  `virtualReactancePu`.
