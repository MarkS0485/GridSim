### trip

`gridsim trip [<case>] [<gen>] [--dir <path>] [--export <csv>] [--gb-services[=<scale>]] [--band[=<fraction>]] [--envelope <class>:<param>=<low>..<high>[,...]] [--dyn-data <file>]`

Trip a generator and report the peak RoCoF (`Peak RoCoF`, the largest |df/dt| over the run), the nadir and the settling
frequency, plus the same trip at half inertia. `<gen>` is a case-insensitive substring of a generator name; no match
lists the available names and exits 2. With two free tokens the first is the case and the second the generator. A
single free token is the case when it names one (built-in name, `.m` file, case folder) and the generator name
otherwise; it is found wherever it sits, so `trip --dir data/uk Torness` trips Torness. More than two free tokens exit
2. Without `<gen>` the largest non-slack unit with inertia is tripped (exit 2 if there is none). `trip` is the only
verb that integrates `--inertia-model distributed`; it also honours `--asset-dynamics` and `--forming`. The
half-inertia comparison runs the lumped model. The run is integrated at the case's own nominal frequency (D2, October
2026): RoCoF = f0 * deltaP / (2E), so the 60 Hz `case9` reports 1.2 times the RoCoF a 50 Hz run of the same loss and
inertia would, and its trajectory starts at 60 Hz (before D2 every case ran at 50 Hz).

The system inertia on the `System inertia:` line is the rotating mass only (N314, October 2026; the N289 rule the
lumped total, the distributed model and the asset fleet already use): a unit whose type is a known converter technology
(wind, solar, storage, interconnector, ...) adds nothing whatever H its data carries, and the tripped unit takes out only
what it put in. It used to sum H x MBase over every unit. No bundled case has a converter-typed unit with H above 0, so
every bundled trip prints the same numbers.

With `--inertia-model distributed` the loss is placed at the tripped unit's bus (R060, October 2026): it lands on the
speed domain whose machines remain at that bus, or, when the tripped unit was the only machine there, it is shared among
the domains by their synchronising coupling to that bus (the distribution of a power impact). A `Loss placed:` line
under `Inertia model:` states the rule that placed it. Before R060 every loss landed on the domain with the largest total
MVA, whichever unit tripped; the per-domain nadirs and RoCoFs depend on the placement, the centre-of-inertia figures do
not. When the inter-domain coupling cannot be derived from the network (no branch reactance, a failed reduction, or
negligible coupling between the domains) the run uses the uniform 0.5 Hz target-mode stiffness and a `Coupling:` line
says so and why (R025; this used to be silent). Buses that no machine is connected to, such as one isolated by a
topology edit, are left out of the reduction instead of making it fail.

`--asset-dynamics` needs a case with an asset bundle: on one without (`data/uk`, the IEEE cases) it exits 1 instead of being ignored. The name `gb-synthetic`
is the shipped `data/gb-synthetic` folder, with its asset bundle (N238 for `trip`, N316 for every verb, October 2026; it
used to build the bundle-less network in memory). It cannot be combined with `--inertia-model distributed`
(exit 2): the multi-domain model carries no asset fleet yet, so one of the two would be dropped. With asset dynamics on,
the `Asset dynamics: on` line splits the infeed lost into inverter trips (Loss-of-Mains RoCoF or vector shift, or
under-voltage ride-through) and protection-device operations; a `note:` line counts the assets whose voltage or loading
stages this lumped run does not evaluate (it solves no network: they are neither operated nor held).

After the run, `trip` assesses the system frequency protection on the run's trajectory (`ProtectionAssessment.
AssessTrajectory`, D-DOC-220): `81U` against the run minimum, `81O` against the run maximum and `81R` against the peak
|df/dt|, with the `protection-check` defaults (G99 Issue 2 Table 10.1: 47.5 Hz, 52.0 Hz, 1.0 Hz/s). Each line says
`holds`, `WOULD OPERATE` or `not evaluated`; `78` (vector shift) is not evaluated, because the run is a
centre-of-inertia frequency trajectory with no bus voltage angle. On a case whose nominal frequency is not 50 Hz
(`case9`, 60 Hz) `81U` and `81O` are `not evaluated` too: the G99 pickups are settings for a 50 Hz system and do not
apply (D2). The lines are informational: the exit code does not change.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` | `data/uk` | Case to load ([Case arguments](#case-arguments)). |
| `<gen>` | largest unit | Generator-name substring to trip. |
| `--dir` | `data/uk` | JSON case directory. |
| `--export` | - | Write the frequency trajectory CSV (`dtm,f`), playable with `gridsim replay <file>`. A missing folder is created ([Output files](#output-files)). |
| `--gb-services[=<scale>]` | off | Add the GB Dynamic Containment / Moderation / Regulation arrest layer at the EAC auction volumes (950 / 520 / 490 MW, `GbResponseServices.Typical`), times `<scale>` (default 1; a finite number above 0, else exit 2), on top of the generic governor. A `Response:` line states the volumes. `--gb-services 0.5` also works. |
| `--band[=<fraction>]` | off | Add the nadir's uncertainty band over the swing constants that are literature-typical and not fitted to GB data (load damping, responsive fraction, governor time constant; `FrequencySensitivitySweep`, R079): each is moved by +/- `<fraction>` (default 0.20; strictly between 0 and 1, else exit 2) one at a time, most leverage first, and over every corner of the joint box. The band follows the half-inertia line; every line before it is unchanged. Lumped model only: with `--asset-dynamics` or `--inertia-model distributed` it exits 2. `--band 0.3` also works. |

With `--band` the output gains a block such as:

```
Uncertainty band (--band 0.2): the unfitted swing constants each +/- 20 % (literature-typical, not fitted to GB data):
  nadir over every corner:  <min> .. <max> Hz (width <w> Hz); peak RoCoF band <r> Hz/s (inertia and the loss set the RoCoF, not these constants)
  one at a time, most leverage first:
    load damping (%/Hz)              0.8 -> 1.2       nadir <a> -> <b> Hz  (swing <s> Hz)
    ...
```

The sweep's nominal run is the trip run itself (checked bit for bit). The constants can be fitted to a measured trace
with [`fit-frequency`](#fit-frequency).

`--envelope <class>:<param>=<low>..<high>[,...]` (with `--asset-dynamics`, R095) sweeps control parameters of the
case's asset classes (`asset-classes.json`) over their plausible ranges around this trip (`AssetEnvelope.Sweep`): every
corner of the ranges (2^k runs for k parameters, at most 12), then a tornado (each parameter low and high with the others
at their midpoints, most leverage first). It prints the nadir band (minimum, median, maximum), the peak RoCoF band,
whether a cascade is possible (some corners trip infeed and others do not: the data cannot say which, and that is the
finding) and the tornado, after the half-inertia line; every line before it is unchanged. The result is tagged
`UnvalidatedAtScale`. Example: `trip --dir data/gb-synthetic --asset-dynamics --envelope dg-alomcp:rocofTripHzPerS=0.5..2`.
The class must be in the case's library and the parameter one its kind is simulated with (the accepted keys of the
asset-class loader); low below high, both finite; no item twice. Without `--asset-dynamics` it exits 2; on a case whose
bundle holds protection devices only it exits 1; a bad item, or a swept value the asset-class checks refuse, exits 2
naming it.

| Flag | Default | Meaning |
|---|---|---|
| `--envelope` | off | With `--asset-dynamics`: the asset-class parameters to sweep, `<class>:<param>=<low>..<high>`, comma-separated. |
| `--dyn-data` | - | A machine dynamic data file (`gridsim-dynamics/2`, the file [`stability --rms`](#stability) reads). The trip integrates the system frequency swing, whose only per-machine number is the inertia H, so each generator the file binds takes the file's H (the case's own H where the template gives none); every engine of the run (lumped, `--inertia-model distributed`, `--asset-dynamics`) and the choice of the unit to trip see that inertia. A `Machine data:` line under the title names the file, counts the generators and how many H values differ from the case, and says that the file's GENROU reactances, exciters and governors are not used by this swing. A file whose H values equal the case's gives the same trip, bit for bit. A missing file or one that fails the v2 schema, kind-spec or binding checks exits 1 with every problem listed, before any run. |
