From GridSim technical ch. 24 Standard dynamic-model library: the models (headings of the Model section), then how the CLI exposes them.

- Block library
- Rotor equations shared by every machine
- Classical machine
- One-axis machine (flux decay), SMIB
- Two-axis machine, SMIB
- Subtransient machine (GENROU-style), SMIB
- Multi-machine network interface
- Exciters
- Power-system stabiliser PSS1A
- Extended library: GENCLS, GENTPJ, ST1, SCRX, DC1A, IEEEST, PSS2A, PSS2B
- More controllers: EXDC2, IEEEX1, ESAC8B, ESST3A, PSS2C, IEEEG3
- Turbine-governors
- Dynamic loads
- Converter plant in the RMS engine
- WECC second-generation renewable models
- IEC 61400-27-1 style Type 4 wind turbines

### How the CLI exposes the library

`gridsim stability <case> --rms` runs `RunRmsStability` (`src/GridSim.Cli/Program.cs`); the verb's flags are in
[the CLI reference](<../manual/0019 CLI reference.md#stability>). Without `--dyn-data` (a `gridsim-dynamics/2` machine data
file bound to the case's generators) the CLI builds the machine data from `X'd`: Anderson-Fouad `X'd` for case9 (`0.0608`, `0.1198`, `0.1813` pu on the machine base), the
fixed `0.30` pu (`CliDefaults.SyntheticXdpPu`) for every other case, with the ratios of `CliDefaults`:

```
Xd = 3.0*X'd    X'q = 1.5*X'd    Xq = 2.5*X'd    X''d = X''q = 0.8*X'd
T'do = 8.0 s    T''do = 0.03 s   T'qo = 0.4 s    T''qo = 0.05 s      D = 0      no saturation
```

`H` is the case's own `H`. The flags that select library models:

| Flag | Effect |
|---|---|
| `--rms` | Selects the six-state engine; without it the classical swing runs. |
| `--avr none` (default) | Field held; the study is the held-field study. |
| `--avr sexs` | One SEXS per synchronous machine: `Ta = 0.1`, `Tb = 1.0`, `K = 100`, `Te = 0.05`, `[Emin, Emax] = [0, 8]`. |
| `--avr ieeet1` | One IEEET1 per synchronous machine: `Ka = 50`, `Ta = 0.02`, `Ke = 1.0`, `Te = 0.2`, `Kf = 0.05`, `Tf = 1.0`, `[Vrmin, Vrmax] = [-10, 10]`, no saturation. |
| `--pss` | Adds one PSS1A to each exciter: `Ks = 20`, `Tw = 10`, `T1 = T3 = 0.15`, `T2 = T4 = 0.05`, `[Vsmin, Vsmax] = [-0.1, 0.1]`. |

A machine receives an exciter when it is not power-flow-only, not converter-interfaced and has `H > 0`; all other
entries are null. `--avr` accepts `none`, `sexs` or `ieeet1` (case-insensitive) and exits 2 with a message for any other value;
`--avr` or `--pss` without `--rms` exits 2; `--pss` without an AVR exits 2. A non-converged base power flow exits 3. A
study error (no machines, an exciter the initial field cannot satisfy, a singular converter block) is printed as
`stability: <message>` and exits 2.

The single-study form (`--clear <ms>`) uses a fault onset of `0.1` s, step `0.005` s and a duration of `3.0` s
(`CliDefaults.FaultOnsetS`, `RmsStepS`, `RmsDurationS`); the clearing-time forms use the `CctSearch` defaults above.
The default fault bus is 7 for case9, else the first non-generator bus. Every case runs on the sparse network. The
RMS study integrates at the case's own nominal frequency (`GridModel.FrequencyHz`: 60 Hz for case9, 50 Hz for the GB
cases); the library's `f0` argument overrides it.
