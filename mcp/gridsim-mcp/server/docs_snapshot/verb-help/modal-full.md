### modal-full

`gridsim modal-full <case|case.m|case-dir> --models <dir> [--region re,im,re,im] [--count N] [--method auto|dense|arnoldi] [--dense-limit N] [--residual-gate x]`

Full-model small-signal (modal) analysis (RC4 R4-12; `GridSim.Core.Dynamics.SmallSignal.Full`). Where `modal` linearises the
classical swing model, `modal-full` adds the machine field flux and the controllers: each machine is either classical (delta, omega)
or the one-axis flux-decay machine of Heffron and Phillips (delta, omega, E'q, with saliency Xq different from X'd and armature
resistance neglected), the network algebra is eliminated by the same Kron reduction `modal` uses (the internal node is placed behind
Xq, and the loop `E_Q = E'q + (Xq - X'd) id` between the internal voltage and the d axis current is solved in closed form), and the
exciter, governor and stabiliser declarative models (`gridsim-model/1`, [`user-model`](#user-model)) are linearised block by block
about the initial state with analytic Jacobians (no finite differences). The classical `modal` verb and every RMS integrator are
unchanged. With no flux machine and no controller the matrix is `modal`'s own, bit for bit.

`--models` names a directory holding `machines.json` (`gridsim-modal-machines/1`) and any `<id>.model.json` documents it names (a
name not found there is looked up in the shipped library). Nothing is defaulted: `{"format": "gridsim-modal-machines/1", "machines":
[{"bus": 2, "unit": 0, "model": "flux-decay", "xdp": 0.32, "xd": 1.6, "xq": 1.55, "tdop": 6.0, "damping": 2.0, "exciter": {"model":
"SEXS", "parameters": {"K": 50}}, "governor": {"model": "TGOV1"}, "stabiliser": {"model": "PSS1A"}, "coded": ["Esst4b"]}]}`.
Reactances are per unit on the machine base; damping is on the system base; `infiniteBus: true` makes a unit an ideal voltage source
(a single-machine study). A machine the file does not name is classical with X'd = 0.30 pu, said in the notes. The exciter reads
`Vref - Vt + Vs` and the speed deviation, the stabiliser (which needs an exciter) reads the speed deviation, the governor reads the
speed deviation and its output is scaled from the machine base to the system base. Loads are constant impedance as in `modal`;
magnetic saturation of the machine, sub-transient dynamics and converter plant are not in this model.

What is linearised and what is not. A controller that the block catalogue cannot express (a `coded` name such as AcExciter, Esst4b or
IEEEG1 with rate limits) or that cannot be linearised (a fixed delay has no finite-order state space; a state that is not at
equilibrium at the operating inputs) is reported under NOT LINEARISED with the reason and held at its operating output; it is never
approximated, and the result is INCOMPLETE. A limit that is active at the operating point freezes the limited state and a rate limiter
is taken as inactive; both are printed in the notes.

How it solves. The order is: base case solve, initialise the models, linearise the blocks in compiled order, assemble A, then the
dense `GeneralEigen` up to `--dense-limit` states (default 200) and a deterministic shift-invert implicitly restarted Arnoldi above it,
a residual check `||A v - lambda v|| / ||A||_1` of every returned pair against `--residual-gate`, participation of every state (machine,
exciter, governor, stabiliser) and the report. The Arnoldi start vector is drawn from the seeded R4-02 stream (seed and domain are
recorded in the result), the restart rule is fixed, and the result is bit-identical across runs and thread counts. Above the dense limit
only the eigenvalues nearest the centre of `--region` are computed (the electromechanical band when no region is given): the output says
how many were found, within what distance of the centre the list is complete, and whether that covers the region; raise `--count` if it
does not. A pair that fails the residual gate, an Arnoldi run that does not converge, or a controller that is not linearised makes the
result INCOMPLETE and the reasons are printed. PowerFactory modal analysis comparison is COMPARATOR-UNVERIFIED.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` | required | [Case argument](#case-arguments); the base case is solved first. |
| `--models` | required | Directory with `machines.json` (`gridsim-modal-machines/1`) and optional `<id>.model.json` declarative models. |
| `--region` | all (dense), electromechanical band (Arnoldi) | Two opposite corners of a rectangle of the s-plane, `re,im,re,im` (1/s, rad/s). The Arnoldi search is centred on it and only eigenvalues inside it are listed. |
| `--count` | 20 | Eigenvalues the Arnoldi path computes (the nearest the centre). |
| `--method` | auto | `auto` (dense up to `--dense-limit`, Arnoldi above), `dense` or `arnoldi`. |
| `--dense-limit` | 200 | Largest state order handled by the dense solver in `auto`. |
| `--residual-gate` | 1e-9 | Relative residual every returned eigenpair must meet. |

Exit 0 COMPLETE with no right-half-plane eigenvalue listed; 1 the case, the bindings or a model cannot be loaded or are invalid, or the
base solve did not converge; 2 usage; 3 INCOMPLETE or at least one right-half-plane eigenvalue listed. Result files: `--json <file.json>`,
`--csv <file.csv>` or `--out <file.json|file.csv>` write the result (tables `modes`, `participation`, `models`, `not_linearised`;
[Result files](#result-files)).
