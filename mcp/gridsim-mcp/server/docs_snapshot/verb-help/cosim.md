### cosim

`gridsim cosim <study.json> [--out <file.json>] [--boundary-csv <file.csv>]`

RMS-EMT co-simulation through the Core interface (`RmsEmtInterface`). `<study.json>` is a strict `gridsim-cosim/1` document:

- `emt3`: an inline `gridsim-emt3/1` study (the detailed part, exactly as [`emt3`](#emt3) reads it);
- `boundary_bus`: the bus of that study where the RMS side connects;
- `form`: `thevenin` (the RMS side hands over an EMF behind a series R-L and takes back the boundary current) or `norton`
  (a current source with a shunt R and L, taking back the boundary voltage);
- `exchange_every_steps`: EMT steps between exchanges (1 or more);
- `rms`: the RMS side, a source phasor `source_re_v`, `source_im_v` (peak volts, cosine convention: phase a is
  `Re{E e^(j w t)}`) behind either `line` (`surge_ohm`, `travel_s`: a lossless line, shorter than a quarter wave) or
  `series` (`r_ohm`, `x_ohm`), with optional `source_changes` (`at_s`, `re_v`, `im_v`), each applied at the first exchange
  at or after its time.

The RMS side is the Core phasor side, not the RMS transient kernel: that kernel runs a whole case and has no entry to
exchange a boundary quantity part-way through. The interface is added after every element of the EMT study. One fundamental
cycle must be a whole number (at least 8) of EMT steps, and the equivalent impedance must be inductive. The result is
bit-repeatable with itself and is not bit-comparable with an all-EMT run (the equivalent is fitted at the fundamental only).
See [ch. 16](<0016 Electromagnetic transients (EMT).md>).

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Write the EMT traces and the exchange log as JSON (`gridsim-cosim/1`); an existing file is never overwritten. |
| `--boundary-csv` | none | Write the exchange log as CSV: `time_s`, `valid`, the voltage and current phasors the EMT side measured, the voltage the RMS side computed, and the EMF and impedance it handed over (peak values, exact round-trip numbers). |

The verb is not on the shared `--json`/`--csv` writer.

Exit 0 when the run completed. Exit 3 when the EMT run ended with a named status other than completed. Exit 1 for a missing
or invalid file, or a network or interface the Core refuses. Exit 2 for usage.
