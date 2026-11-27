### sc-dc

`gridsim sc-dc <network.json> [--time <s>[,<s>...]] [--step <s>] [--end-time <s>] [--breaker making_A]`

An IEC 61660-style short circuit of a DC auxiliary installation: batteries, rectifiers, charged capacitors and DC motors feeding a fault
through a resistive-inductive network (RC4 R4-20c; `Protection/ShortCircuit/Dc`). The case format carries no DC data, so the network is
its own study file and this verb takes that file, not a case. **Verification status: implemented from public documentation, equations
only; not verified against the standard.** IEC 61660-1 is a paid text and was not read; every run prints the list of items marked
AWAITING STANDARD TEXT.

What is computed. The network is solved as a circuit rather than through the standard's equivalent-circuit coefficients (which are
AWAITING): modified nodal analysis with the trapezoidal rule (one backward-Euler start step, so that the voltage jump at fault
inception does not enter the first step), a fixed step, a matrix factored once with partial-pivot LU, and no transcendental function (so
results are bit-identical between runs). The prefault state is the DC steady state with the fault open (inductors short, capacitors
charged, motors at their EMF). Each source is a voltage behind R and L: a battery or rectifier holds a constant EMF, giving
`i = (E/R)(1 - e^(-R t/L))` alone; a capacitor has `C dU/dt = -i`, giving the second-order discharge with `L C s^2 + R C s + 1 = 0`; a
motor has back EMF `k w` with `J dw/dt = -k i` (load torque and field decay neglected), giving `L J s^2 + R J s + k^2 = 0`. The report
gives the fault current peak and its time, the current at the end time and at each `--time`, and each source's peak, time and end value.
The step defaults to the shortest time constant divided by 400 and the end time to 8 times the longest (L over R, R C, J R over k^2 of
the summed loop); if either cannot be chosen, or more than 5 million steps would be needed, the run is INCOMPLETE and says what to give.
Both conductors are lumped into each branch's R and L; the return is the node named `"0"`.

`network.json` is `gridsim-dc-network/1` and nothing is defaulted: `{"schema": "gridsim-dc-network/1", "branches": [{"name": "cab",
"from": "1", "to": "2", "rOhm": 0.03, "lH": 0.002}], "sources": [{"name": "B1", "kind": "battery", "node": "1", "eV": 110, "rOhm": 0.02,
"lH": 0.001}, {"name": "C1", "kind": "capacitor", "node": "1", "uV": 110, "rOhm": 0.05, "lH": 0.0005, "cF": 0.01}, {"name": "M1", "kind":
"motor", "node": "1", "eV": 200, "rOhm": 0.1, "lH": 0.02, "jKgM2": 0.5, "kVsPerRad": 2}], "fault": {"node": "2", "rOhm": 0}}`. Source kinds:
`battery`, `rectifier` (a constant EMF here; its commutation and AC-side behaviour are AWAITING), `capacitor` (`uV`, `cF`, needs `lH` above
0) and `motor` (`eV` the back EMF at the fault instant, `jKgM2`, `kVsPerRad`, needs `lH` above 0). A node with no path to a source makes the
prefault matrix singular and the run INCOMPLETE. Exit 0 done; 1 unreadable or invalid network file; 2 usage; 3 INCOMPLETE or a making
capacity exceeded.

Public sources used (none is the standard): the scope of IEC 61660-1 (auxiliary DC installations in power plants and substations;
secondary descriptions only) and standard circuit theory (series R-L charging, series R-L-C discharge, a separately excited DC machine
as generator behind its armature R and L; for example Fitzgerald, Kingsley and Umans, Electric Machinery, chapter on DC machines).

| Flag | Default | Meaning |
|---|---|---|
| `--time` | none | Comma list of times in seconds (0 or more) at which the fault current and each source current are reported. |
| `--step` | shortest time constant / 400 | Fixed time step in seconds; above 0. |
| `--end-time` | 8 times the longest time constant | End of the simulated window in seconds; above 0. |
| `--breaker` | - | Making capacity in amperes compared with the fault current peak; above 0. Exit 3 if exceeded. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result (tables `sources`, `times`;
[Result files](#result-files)).
