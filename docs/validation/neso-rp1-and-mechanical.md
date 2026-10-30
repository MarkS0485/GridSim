# NESO RP1 + MECHANICAL panels, drill-down, and the bridge map fix

Two situational-awareness control-room tabs added to GridSim (2026-07-09), streamed frame-by-frame
from the GDA lake over the bridge, plus a reusable drill-down inspector and a fix for the
bridge/replay map geolocation. Both panels are grounded in the TSGB corpus
(`W:\Code\TSGB-2026\Proposals\MIF`, `Policy/0021 - The 1,800 Megawatt Fiction.md`).

## NESO RP1 - "where am I now / where am I going / how long do I have"

A situational-awareness page, not a dashboard. Hero quantity is **purchased time** - seconds of
frequency ride-through on *real intrinsic* mass.

- **Physics** - new pure `src/GridSim.Core/Dynamics/StabilityMetrics.cs`:
  - `PurchasedTimeSeconds(intrinsicGvaS, credibleLossMw, currentHz, thresholdHz=49.2)` - `t = deltaf/RoCoF`,
    `RoCoF = f0*deltaP/(2E)`. Threshold **49.2 Hz** (deltaf 0.8 Hz) is the corpus working set-point (Policy 0021 sec. 6).
  - `DynamicMifGvaS(loss, rocofLimit)` = `f0*deltaP/(2*RoCoF_limit)` (S_base cancels).
  - `RocofFor(...)` wraps `InertiaEstimator.PredictRocof`; `LargestCorrelatedGroup(groups, flows)`.
  - Tests (`tests/GridSim.Tests/StabilityMetricsTests.cs`) reproduce the corpus tables:
    3,600 MW -> 720->6.4 s, 360->3.2 s, 155->1.4 s, 102->0.9 s; and the MIF 1,800 MW table.
- **Inertia split (fuel gauge, MIF 0001 sec. 10)** - GVA.s **total = (i) + (p)** (additive, never petrol>tank).
  (i) intrinsic = synchronous mass (lake `synchronous_inertia_gvas`, else computed from the model's
  synchronous generators `sum H*MBase/1000`); (p) = NESO "Market Provided" (labelled *market/contracted*,
  not pure proxy - per MIF 0004 sec. 8 it can include real sync condensers).
- **Credible loss** - shows **both**: the live largest-possible-now (biggest online synchronous unit +
  largest correlated HVDC group: Sellindge IFA+ElecLink, North-Sea NSL+Viking; the corridor table and the
  link-name matching live in `GridSim.Core.Presentation.InterconnectorCorridors`, shared by web and WPF, and
  recognise every source's spelling - gb-full's "IFA Interconnector", "NS Link interconnector", "Viking Link
  Denmark Interconnector", the reduced cases' "North Sea Link" / "Viking Link", and the bridge raw ids) **and** the hard
  **3,600 MW Dogger Bank** design basis, with the **1,800 MW convention struck through** (Policy 0021).
- Also: inertia-by-source (seconds), 30-min stability trend, auto-generated handover **narrative**,
  **BM Stability Delta** (each acceptance green/red by inertia direction - needs the lake's BMU->fuel tag),
  interconnector summary with correlated-trip dots, **LFDD traffic light** (49.5/49.2/48.8 ladder from
  `find_lfdd_events.py`), and coherence-cost **GBP/h by constraint** (RIGHT column; gb-full has no boundary
  model so boundary bars are flagged absent, cost still shown).

## MECHANICAL / Plant Engineer - "what the MACHINES are doing"

Per-machine, asset-level (`Views/MachineEngineerVm.cs` + `MachineEngineerView.xaml` +
`Visuals/PqDiagram.cs`, a custom P-Q capability control). Five sections: per-machine state
(risk-sorted by loading), shaft/torsional + thermal, protection envelope (P-Q dot + **dual RoCoF-relay
margin**: current vs the original 0.125 AND relaxed 1.0 limit - Policy 0021 sec. 10), inertia-per-unit
(sum asset KE vs reported (i), synthetic gap), fleet trajectory (MW/min + projected inertia +15 min).

**Ramp identity and period:** each machine's ramp is its own frame-to-frame change, tracked by generator
index (checked against bus and name, `GridSim.Core.Presentation.UnitRampHistory`) because gb-full unit names
are not unique; it is divided by the frame interval - the WPF view derives it from the frame clock, the web
view follows the transport STEP picker (`SimSession.SetStepMinutes`).

**Provenance is explicit and honest:** MW / loading / inertia (H*MBase) / ramp / RoCoF-margin are REAL.
MVAr, power factor, excitation, AVR/governor mode, shaft torsional stress, stator/rotor temps,
certified-J + type-test dates, committed starts are shown "-" or flagged synthetic until a plant feed
is wired. **Do NOT reconstruct MVAr from per-bus net QInj** - it misattributes shunt/line-charging
reactive to a small co-located generator (an 11 MW CHP reads +368 MVAr). Filters to synchronous units
>=10 MW to drop sub-MW embedded plant noise.

## Drill-down (click a paused frame to explore it)

`Views/InspectorVm.cs` + `InspectorView.xaml` - a reusable overlay: headline stat tiles + constituent
rows + a back-stack (`< back`) + `>` chevron (shown when a row is drillable) + `x` close.

- RP1: dispatch-stack / inertia-by-source rows -> **fuel breakdown** (constituent units + combined
  totals/averages); BM row -> acceptance detail; interconnector row -> flow + correlated-trip corridor
  with the co-members drillable. Nested: a fuel's unit rows drill to a unit detail (whose Fuel row
  drills back up); interconnector members hop between each other.
- MECHANICAL: machine row -> full per-machine card.
- **Wiring idiom** (important): wrap the `ItemsControl` row DataTemplate in
  `<Button Style="{StaticResource RowLinkBtn}" Command="{Binding DataContext.InspectXCommand,
  RelativeSource={RelativeSource AncestorType=ItemsControl}}" CommandParameter="{Binding [Fuel|/row]}">`.
  A bare `MouseBinding` on the row Grid does **not** fire reliably inside a `ScrollViewer`. Per-row
  nested drill uses `InspectRow.Drill` (an `ICommand` set per row via `new RelayCommand(() => ...)`).

## Bridge / lake wiring

- Protocol (`Core/IO/BridgeProtocol.cs`): `OperatorBlock` gained `SynchronousInertiaGvaS`,
  `OutturnInertiaGvaS`, `MarketInertiaGvaS`; `BalancingActionDto` gained `FuelType`, `Synchronous`;
  `BusDto` gained `X`, `Y`. Fed via the existing `MarketDataReceived` side-channel + per-frame `Update`.
- GDA builder (`NetworkModel/build_operator_market_frames.py`): enriches `balancing_actions_json` from
  `DataSources/Elexon/Reference/bmunits_all.json` (elexonBmUnit->fuelType; synchronous = {CCGT,OCGT,COAL,
  BIOMASS,NUCLEAR,GAS,OIL,PS,NPSHYD}) and as-of joins the inertia columns. **The server's `opmkt`
  `_load_scalar_table` reads an EXPLICIT column list (`bridge_server.py` ~line 340) - new columns must
  be added there.** Rebuild the table after editing the builder.

## Map fix (bridge/replay geolocation)

Symptom: under the live bridge, gb-full stacked every node at the Cornwall corner. Cause: the
presolved `topology.json` carried no per-bus x/y (`gridsim_export.to_solve_export` dropped the
geolocated coords that `out/<case>/buses.json` already holds). Fix: `to_solve_export` now emits x/y;
C# `BusDto`/`BuildModel` read them; existing `topology.json` for all 7 cases were patched in place by
merging x/y from `buses.json` (no re-solve). Restart the bridge to reload `topology.json`. NB the
bridge bus names are LTDS tower ids ("Tower 48CM13"), so C#-side `GeoLocator.Apply` name-matching is a
dead end (0% match) - the coords must ride in the stream.

## Run it

```
python W:\Code\GDA\v1\Applications\GridSim-Bridge\bridge_server.py          # :8770; restart to reload topology
dotnet build src/GridSim.Wpf/GridSim.Wpf.csproj -c Release                  # close the app first (file locks)
dotnet run --project src/GridSim.Wpf -c Release -- bridge --zone gb-full --back 30 --tab "NESO RP1"
```
- `--zone` = gb-full or a DNO zone; `--back 30` lands in the presolved 2026-06-10 window; `--tab` boots a tab.
- Tests: `dotnet test tests/GridSim.Tests` (451). Bridge acceptance:
  `python W:\Code\GDA\v1\Applications\GridSim-Bridge\test_bridge_stream.py gb-full` (asserts the inertia
  split + BM fuel tags).
- **Screenshot to iterate** - capture with PowerShell `PrintWindow` (flag 2, PW_RENDERFULLCONTENT); it
  grabs the app window without stealing focus. Do NOT script `mouse_event` *clicks* while the user is at
  the keyboard - clicks need real foreground and will fight them (and miss).

## Known follow-ups

- Asset-sum-vs-reported "synthetic gap" (MECHANICAL sec. 4) shows 0 when the lake intrinsic side-channel is
  null for that period (SetIntrinsic timing) - wire more robustly.
- Wire real plant SCADA to fill the flagged MVAr/PF/excitation/torsional/temps/certified-J fields.
- RP1 boundary bars are empty for gb-full (no boundary model in the streamed case) - carry ETYS
  boundaries in the model or compute them.
