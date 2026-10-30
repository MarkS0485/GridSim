# The 22 December 2023 GB Excursion - an Estimated-State Dossier

*GridSim + GDA evidence artefact, assembled 2026-07-14. Every figure carries its
source inline; nothing here is a placeholder.*

---

## 1. What this document is

On 22 December 2023 at 13:09:51 UTC, GB lost ~1,000 MW of IFA interconnector
import, followed about nine seconds later by the Cottam CCGT (345 MW).
Frequency fell to 49.275 Hz and stayed below the 49.5 Hz statutory limit for
roughly 59 seconds. No demand was disconnected.

This is the catalogue's **second sourced event** (`gb-2023-12-22`), and it is
the first whose record is **measured end to end by the apparatus itself**:
losses from settlement metering, operating point from the recorded inertia
outturn + the WLS estimator + the trace, response services from the auction
record, and the balancing-mechanism recovery from the BOALF record. The gate's
verdict is an honest **MARGINAL**, and the residual is isolated - by the
model's own declared-uncertainty envelope - to a specific, nameable gap in the
public record: the inertia measurement basis (sec. 5).

**A self-caught citation defect.** An earlier draft of this event cited "the
FRCR 2025 independent review" for the loss figures (IFA bipole 1 1,000 MW +
Cottam 383 MW). On verification against the document itself, that review does
not discuss this event at all - the figures had come from a conflated search
summary. The record was rebuilt from settlement metering (below); the metered
Cottam loss is **345 MW, not 383**. The lesson is a standing rule now: a
citation is only a citation once it has been checked against the document.

## 2. The measured event

| Quantity | Value | Source |
|---|---:|---|
| IFA loss | -1,000 MW at ~13:10 | Settlement metering: Sellindge landing flow 1,988 -> 999 MW; the event period's 1,322 MW average is exactly the time-weighted value of a 1,000 MW step ~10 min into the half-hour (`Derived/gsp_ic_flows`) |
| Cottam CCGT loss | -345 MW at ~13:09:4x | B1610: `T_CDCL-1` metered 172.5 MWh/half-hour (345 MW) -> 55.7 -> ~0; 55.7/172.5 = 32% of the period delivered => trip ~9.7 min in |
| Total exogenous loss | **1,345 MW** | sum of the above; two-stage fall on the trace (arrest ~49.65 Hz after the first, second fall from 13:10:00) matches the two metered losses |
| Pre-event inertia | 161 GVA.s | NESO recorded outturn, SP 27 (`system_inertia`; same feed's outturn for the 2019 event half-hours is 209-215, matching Ofgem's 215) |
| Transmission-net demand | 22,449 MW -> 22,400 in the record | WLS estimated state, SP 26 (48/48 chi-square day, zero removals) |
| Pre-event frequency | 49.879 Hz | NESO 1-s trace, flat 49.879-49.881 for >=6 s before the fall |
| DCL / DML / DRL held | 873 / 150 / 203 MW | EAC auction 70 cleared volumes, block 11:00-15:00 UTC (results-summary `clearedVolume`) |
| BM recovery | +904 MW by +129 s, ramp starting +19 s | BOALF net instructed level, interpolated at 10 s |
| Recorded outcome | -0.102 Hz/s max 1-s delta; nadir 49.275 Hz | NESO public 1-s trace (`data/neso/gb-2023-12-22-frequency.csv`) |

**Pre-event context, also settlement-measured:** North Sea Link (Blyth
landing) ran back ~292 MW at ~13:02:50 (time-weighted), and a ~213 MW
scheduled flow swing landed exactly on the 13:00 period boundary. Together
they explain the recorded 49.879 Hz sag and the ~200 MW residual the BM was
carrying when the trips landed - which is precisely what the replay's
equilibrium start represents.

**Data traps found while measuring (worth recording):** the
EAC buy-orders archive double-counts across overlapping weekly copies (use the
results-summary `clearedVolume`); raw B1610 `quantity` is MWh per half-hour
(x2 for MW).

## 3. What reproducing 2023 required of the engine

Landed with this event, all engine-side and all parameterised by record or
published spec - none fitted:

1. **Recorded response services** (`ResponseService` -> swing-model
   integration in the scalar, fleet AND multi-domain models): the DC/DM/DR
   volumes the system held, on their **published delivery curves** - DC's 5%
   knee at 0.2 Hz then full at 0.5 Hz; DM/DR as ramps from their deadbands to
   0.2 Hz. Baseline and counterfactuals both carry them: a device is
   evaluated on top of what the system had.
2. **Governor completeness** (`ResponseServicesComplete`): post-EAC-go-live
   the auctions are the sole response procurement route, so the generic
   conventional-plant governor term is zeroed rather than double-counted.
3. **RoCoF measurement basis** (`RocofMeasurementWindowS`): the recorded
   -0.102 Hz/s is a max consecutive-sample delta from a 1 Hz logger; the sim
   is scored on the same sampling grid (phase-median), not a sliding-window
   maximum that is biased steep by construction.
4. **The recorded operating point** (`PreEventFrequencyHz`): the replay
   starts in equilibrium at 49.879 Hz - service states pre-loaded per the
   published curves, the residual standing imbalance carried as a constant -
   and the **BM recovery enters as recorded actions** (the BOALF ramp).
5. **The declared-uncertainty envelope**: the gate now also replays the
   Best/Worst corners (the scenario engine's documented error bars on the
   unfitted constants) and reports whether the recorded nadir falls inside.
   Reporting only - the verdict stays nominal.

## 4. The gate result

```
RoCoF:  sim  -0.123   recorded  -0.102 Hz/s   (20 % error)
Nadir:  sim   48.98   recorded   49.27 Hz     (0.30 Hz error)
Nadir envelope (declared-uncertainty corners): [48.65, 49.20] Hz - recorded OUTSIDE
RMSE vs trace: 0.281 Hz
VERDICT: MARGINAL
```

Counterfactuals stay locked on this event; `gb-2019-08-09` (REPRODUCED,
recorded nadir INSIDE its envelope) remains the counterfactual baseline.

## 5. The finding: the public inertia figure is a floor, not the total

Everything recorded is now in the replay, and the recorded nadir still sits
**above even the maximum-damping corner**. The corners scale every unfitted
arrest constant; the one physical quantity they deliberately do not touch is
inertia. Two independent instruments now point at it:

- The recorded 1-s RoCoF inverts to ~245 GVA.s naively, or ~180-200 GVA.s
  after correcting for in-window response delivery - versus the 161 GVA.s
  NESO outturn.
- The recorded arrest at 49.275 Hz is shallower than the model can produce at
  161 GVA.s under *any* damping within its declared range.

Both discrepancies close together if the effective system inertia at the
event exceeded the outturn figure by roughly 20-40 GVA.s - the scale of the
embedded/demand-side synchronous contribution that a market-measured inertia
figure does not see. For the 2019 event the same feed matched Ofgem's total
and the gate closed cleanly; at this 2023 winter-lunchtime operating point,
with recorded inertia near historic lows, the unmeasured contribution is
proportionally material.

We do **not** fit an inertia value to close the gate - the event record
carries the recorded outturn, the gate reports MARGINAL against it, and this
section documents the basis gap. The constructive statement for the evidence
case: at low recorded inertia the public figure understates what the system
actually had, which means analyses built on the outturn feed (including
NESO's own risk modelling and this catalogue's replay) carry a conservative
bias whose size this event measures at ~20-40 GVA.s. A future granular-data
pass (PMU regional traces, unit-level response metering) can turn that from a
residual into a measured term.

## 6. Reproduce

```
dotnet run --project src/GridSim.Cli -c Release -- replay-event gb-2023-12-22
dotnet run --project src/GridSim.Cli -c Release -- replay-event gb-2023-12-22 \
    --trace data/neso/gb-2023-12-22-frequency.csv
```

Lake-side: the day's measurements and estimates were built by
`NetworkModel/build_gsp_measurements.py` + `build_estimated_state.py`
(48/48 chi-square, zero removals); the loss inventory comes from
`Derived/gsp_ic_flows` + `DataSources/Elexon/Parquet/b1610`; the response
holdings from `enduring_auction_capability_eac_auction_results`; the BM
recovery from `DataSources/Elexon/Parquet/boalf`; the trace CSV from
`Derived/unified/frequency_all_srcs` (source `neso_1s`).
