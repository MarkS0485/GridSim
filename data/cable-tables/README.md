# Cable-sizing table files (R4-20d)

These files hold the numbers that `gridsim cable-size` needs from IEC 60364-5-52 and BS 7671: current-carrying capacity,
correction factors, voltage-drop figures and the adiabatic constant k. Those tables are in paid standards, so nothing here is
filled. The two shipped files are EMPTY on purpose: a quantity whose table is empty makes `cable-size` answer OUT OF SCOPE for it.

To use the verb, copy an empty file, fill the arrays you own the right to use, and fill `provenance` (source, edition, who
filled it, date). A file with data and no provenance is refused. Schema `gridsim-cable-tables/1`:

- `ampacity`: `{method, conductor, insulation, loadedConductors, sizesMm2[], amps[]}`; `method` is your installation-method label.
- `factors`: `{kind: "ambient" | "grouping", method, insulation, keys[], factors[]}`; keys are ambient temperature (C) or number of
  circuits; only exact keys are used, there is no interpolation.
- `voltDrop`: `{conductor, insulation, phases: "1ph" | "3ph", sizesMm2[], mvPerAPerM[]}`.
- `impedance`: `{conductor, insulation, sizesMm2[], rOhmPerKm[], xOhmPerKm[]}` (used when no voltDrop table is held).
- `adiabaticK`: `{conductor, insulation, k}`.

Do not commit paid text to a public repository: keep filled files outside it.
