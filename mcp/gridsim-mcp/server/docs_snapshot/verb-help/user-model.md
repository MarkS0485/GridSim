### user-model

`gridsim user-model list`

`gridsim user-model check <model.json | LIBRARY-ID>`

`gridsim user-model check-plugin <plugins.json>`

Declarative dynamic models (RC4 R4-08, Core `GridSim.Core.Dynamics.UserModels`). A user-defined controller is a
`gridsim-model/1` JSON document describing a block diagram, not code: ports (inputs and outputs), parameters with units and
bounds, blocks from a closed catalogue, wires, and an initialisation block. It is interpreted by the same step operators
the coded controllers use (`ControlBlocks.FirstOrderLag`, `LeadLag`, `Washout`, `Integrator`, `PiController`), inside the
same integration machinery, so a declarative model is inside GridSim's bit-identity guarantee: a model that re-expresses a
coded one (TGOV1, SEXS, PSS1A, GAST, IEEET1, IEEEG1 without valve rate limits, SEXS with PSS1A) gives the coded model's
output bit for bit (every sample of 10^5 steps at three step sizes, and whole studies on IEEE 9 and the 14-bus dynamics
fixture). Vendor binaries (FMU, DLL) are not part of this format; GridSim's own .NET plug-in interface is a separate route,
described under "Model plug-ins" below.

`list` prints the shipped library, one model per line: id, role, document SHA-256 and title. `check` reads a document (a
file, or a shipped id) and validates it against the closed vocabulary, compiles it, and prints what the model coverage
report records for it: the id, role, source citation, the document SHA-256 (the hash of the exact bytes, so any edit or
block reordering changes it), the compiled-order digest, the coverage status `SUPPORTED (USER MODEL, sha256:<hash>)`, the
ports, the parameters with units and bounds, and the blocks in evaluation order. Exit 0 valid; exit 1 the file or id does
not exist or the document is refused (every refusal is printed with its code and the object it names, for example
`[algebraic-loop]`, `[unknown-block]`, `[unit-mismatch]`, `[undeclared-port]`, `[out-of-bounds]`); exit 2 usage (a missing
argument, an unknown subcommand, any flag: the verb takes none).

`check-plugin` loads a `gridsim-plugins/1` configuration under every loading rule below, binds each listed model at its
declared defaults (which runs the replay check) and prints, per model, the id, role, assembly, assembly SHA-256, the status
`SUPPORTED (PLUGIN, sha256:<hash>, OUTSIDE THE VERIFIED CONTRACT)` and the parameters. Exit 0 every model passed; exit 1 a
refusal, printed with the entry it names.

The format. A document has `format` (`gridsim-model/1`), `id`, `role` (`exciter`, `governor`, `stabiliser` or `generic`),
`source` (the public source of the structure, required; the roles `injection`, `load` and `relay` are described below), `parameters` (name, `unit` of `pu`, `s` or `1/s`, `value`, optional
`min`, `max`, `minExclusive`, `maxExclusive`, `allowInfinite`), optional `derived` constants (`add`, `sub`, `mul`, `div` of
parameters, evaluated in listed order so a coded constructor's arithmetic can be reproduced) and `constraints` (a parameter
sum), `ports` (`inputs`; `outputs` each bound with `from`), `blocks` and `initialisation`. A model role fixes its ports: an
exciter takes `verr` and `speed` and gives `efd` (initialised from `efd0`, and it names the initialisation value that is its
steady regulator error); a governor takes `speed` and gives `pm` (from `pm0`); a stabiliser takes `u` and gives `vs` (from
`u0`). Unknown or duplicate members are refused with their JSON path.

The block catalogue is closed: `gain`, `div`, `sum` (with a `signs` text), `product`, `lag`, `leadlag`, `washout`,
`integrator` (windup-limited when given limits), `pi`, `limit`, `ratelimit`, `deadband`, `table1d`, `expsat`, `min`, `max`,
`switch`, `const`, `hold`, `delay` (whole steps), `satpole` (the IEEE type 1 saturated exciter pole), `expr` (below), and
`ontimer` and `latch` (below, with the relay role). A block outside it is refused naming the block; it is never approximated. A wire is a port name or a block id; `block.prev` reads a stateful
block's output as it stood at the start of the step, and is the only way to close a loop: an algebraic loop without it is
refused naming the blocks. A `washout` with a second input updates its state from that value after the step's outputs are
evaluated (the coded `OutputFor` then `Step` split used by the rate-feedback loops). Units are checked: time constants are
`s`, an integrator output is the input times `s`, the operands of a sum, min, max or switch must agree, and a limit must be in
the unit of its signal.

Evaluation order is part of the contract: parse, validate vocabulary, units and ports, compile (blocks are taken in document
order among those whose inputs are ready, so the compiled order is a function of the document), initialise from the solved
operating point (the initialisation steps run in their listed order, with the equilibrium-within-limits checks the coded
models make), then per step: write inputs, snapshot the `.prev` values, evaluate the compiled operation list, apply deferred
washout updates, read outputs. There is no scripting language, no user code, no reflection and no file access at run time.

Core use: `UserModelLibrary.Load(id, parameters)` or `UserModel.FromDocument(...)`, then
`UserModelGovernorData` (the `SubtransientInit.MachineData.Governor` of a machine), `RmsExciter.FromUser(new
UserModelFieldController(model), ...)` (the RMS studies' exciter list), or `DynamicsStudyRequest.UserModels` (a list of
`UserModelAssignment`: one governor or exciter model per machine, by bus and id, in place of the DYR record's controller).
The study's model coverage report then lists the record as `SUPPORTED (USER MODEL, sha256:<hash>)` and its notes carry the
model id, hash, order digest and bound parameter values. The dynamics-study JSON request (`schemaVersion` 1) is unchanged:
binding user models from a request file is not offered by this verb; the sidecar `--model-assignments` file is (see
[`dynamics-study`](#dynamics-study)). Equivalence with PowerFactory DSL models is not claimed (COMPARATOR-UNVERIFIED): the
claim is bit identity with GridSim's own coded models.

**The `expr` block (RC5).** `{ "id": "cmd", "type": "expr", "inputs": ["pref", "speed"], "expr": "in0 - in1 / @R", "unit": "pu" }`.
The text is numbers (no exponent), the block's inputs `in0` to `in7`, declared parameters `@Name`, `+ - * /` (left to right,
`*` and `/` before `+` and `-`), unary minus, brackets, and the functions `min` and `max` (two or more arguments, taken left to
right), `abs`, `sqrt`, `exp` (the deterministic `DetMath.Exp`) and `clamp(x, lo, hi)`. It is compiled once into a fixed
evaluation order that is the text read left to right, so `in0 - in1 / @R` does exactly the coded TGOV1's `Pref - dw / R`.
Nesting and the evaluation stack are capped at 64. The output unit is declared with `unit` and not inferred. A non-finite
result (a division by zero, the square root of a negative number) refuses the step with the code `non-finite`, naming the
block. An `expr` TGOV1 gives the coded TGOV1's output bit for bit (tested).

**Injection and load roles (RC5).** An `injection` model takes `vm` (the terminal |V|) and gives `ip` and `iq`, the active
and reactive current exporting positive on the unit's MBASE; the current is `(ip - j iq) v / |v|` times MBASE / Sb. A `load`
model takes `vm` and gives `p` and `q`, the drawn active and reactive power as multiples of the load's P and Q; the current
drawn is `conj(S / v)`. Both are initialised from `p0`, `q0` (on the MBASE for an injection, system per unit for a load) and
`v0`. Inside the network iteration the model is evaluated with every state held (`ModelInstance.EvaluateHeld`), so its
current is a pure function of the voltage; the states advance once per step. A load model's Norton admittance is the one the
constant-admittance bus load would have had, so `p = q = (vm / v0)^2` leaves the network exactly as the static load did
(tested to 1e-12 pu through a fault). They reach the RMS study through the API: `SubtransientInit.MachineData.UserInjection`
(a `UserDeviceModel`) on a generator, and `UserDynamicLoadData` in `RmsLoadedMachineData.Loads` for a bus load (which may be
purely reactive). At its start each must reproduce the power-flow P + jQ to 1e-10 (1 + |S|) pu, or it is refused.

**Model plug-ins (RC5).** A plug-in is a .NET class library that references `GridSim.Core`, carries
`[assembly: GridSimModelPlugin(1)]`, and holds one public class per model implementing `IUserModelFactory`: an `Id` (1 to 16
of A-Z, 0-9 and underscore), a `Role` (exciter, governor, stabiliser, injection or load), the `Parameters` it takes (name,
unit, default, optional bounds) and `Create(parameters)`, which returns a fresh instance of the role interface:
`IUserExciter` (`Initialize(efd0)`, `EquilibriumErrorPu(efd0)` without touching the states, `Step(verr, speedDev, dt)`),
`IUserGovernor` (`Initialize(pm0)`, `Step(speedDev, dt)`), `IUserStabiliser`, `IUserInjection` or `IUserLoad` (a constant
Norton admittance, `Initialize` at the power-flow start, `Current(v)` with the states held, `Commit(v)`, `Step(t, v, dt)`).
Quantities are per unit: the machine base for the control roles, the system base for currents; phasors are (re, im) pairs.

Contract 2 (`[assembly: GridSimModelPlugin(2)]`) keeps every contract 1 interface and adds two roles. `machine`:
`IUserMachine`, a Norton source with a constant admittance on the system base (`NortonAdmittancePu`), `Initialise` at the
power-flow P0 + jQ0, V0 and MBASE, `NortonCurrentPu` from the present states, `Step(dt, inputs)` from the step's terminal
voltage, and the speed, rotor angle, field current and electrical power it reports. It runs on the injection route, keeps its
own rotor, field and mechanical power, and is not judged in the synchronism verdict. `relay`: `IUserRelay`, `Initialise()`
and `Evaluate(inputs)` once per step (step number and size, time, unit bus |V|, the machine's electrical frequency and its
rate of change, P, Q, speed and |I|), returning no action or a trip with a reason code; it trips its unit by the unit relays'
path. Bound to a branch instead, it reads |V| at one end and the |I|, P and Q flowing into the branch there (frequency, its
rate and speed are NaN: a branch has no machine), and its trip opens the branch from the next step.
A configuration entry may add `"contract": 1` or `2`, which must match the assembly. A contract 1 assembly cannot give the
machine or relay role: a role number outside the five contract 1 roles is refused when the model is bound, with the words
contract 1 always used, and an assembly that declares a contract other than 1 or 2 is refused with "this build implements
contract 1", as before. The sample `tests/GridSim.ModelPlugin.Contract2Sample` writes a classical machine and two
under-voltage relays.

**Relay role (RC5).** A `relay` document takes, in this order, `vm` (pu), `f` (Hz, declared `1`), `p`, `q` (system pu), `dfdt`
(Hz/s, declared `1/s`), `speed` (pu) and `i` (system pu), the fields a relay plug-in reads, and gives `trip`: 0 for no
action, otherwise the reason code (a whole number). It has no initialisation inputs; its stateful blocks are initialised with
literals. `ontimer` (parameter `time`, seconds) gives 1 once its input has been non-zero for `time`, counted in whole steps by
the built-in relay stages' rule (operation at the first step k with `(k - k_pickup) h + h 1e-6 >= time`, a zero step resets
it, one fixed step size, initialised with 0); `latch` gives 1 from the first step its input is non-zero to the end of the run
(initialised with 0 or 1). A relay document runs through `UserRelayBinding.ForUnit` or `ForBranch` with
`RmsTransientStability.StudyEventsWithUserRelays`, or as a `deviceModels` entry of role `relay` in a dynamics study (see
[`dynamics-study`](#dynamics-study)); bound to a branch it may not read `f`, `dfdt` or `speed`. Its records carry its id and
document hash, without the OUTSIDE THE VERIFIED CONTRACT label.

How to write one:

1. Make a class library for the same .NET version as GridSim, reference `GridSim.Core` (do not copy it beside the plug-in:
   the host's copy is the one the plug-in binds to), and add `[assembly: GridSimModelPlugin(1)]`.
2. Write the model as a class that keeps all its state in its fields, on the calling thread, using the public
   `ControlBlocks` operators (`FirstOrderLag`, `LeadLag`, `Washout`, `Integrator`, `PiController`) where they fit and
   `DetMath` for any transcendental function. The sample `tests/GridSim.ModelPlugin.Sample` writes TGOV1 and SEXS this way.
3. Write a factory class with a public parameterless constructor that returns a new instance from `Create`.
4. Build it, put the DLL under a folder you control, and write the configuration:
   `{ "schema": "gridsim-plugins/1", "root": "plugins", "assemblies": [ { "path": "MyModels.dll", "sha256": "<hex>", "models": ["MY_GOV"] } ] }`.
5. Run `gridsim user-model check-plugin plugins.json`, then use the model through `dynamics-study --model-plugins
   plugins.json --model-assignments assignments.json`.

The contract a plug-in must keep: no threads, tasks, async, timers or locks; no clock; no randomness and no `HashCode`; no
enumeration of `Dictionary`, `HashSet`, concurrent collections or `Hashtable` (use arrays, `List<T>` or `SortedDictionary`);
no file, network, console, process or environment access, no P/Invoke, no unsafe code, no reflection emit; no mutable
static state (an instance holds all its state, and instances share nothing); `DetMath` for transcendental functions and, of
`System.Math`, only Abs, Min, Max, Sqrt, Floor, Ceiling, Round (to even), Sign, Clamp and CopySign; no
`Math.FusedMultiplyAdd`, vectors, intrinsics, `float` or `decimal`; and `Current(v)` pure. What GridSim enforces: a lint of
the assembly's metadata before it is loaded (every member, type and signature it references, and single-precision opcodes),
which refuses a breach naming the member and the type that references it, and a replay check when a model is bound, which
refuses it as "not deterministic (replay differs at step k)". Every number a plug-in returns is checked finite; a
non-finite one refuses the step, naming the model and the time.

The lint refuses, by name, wherever the plug-in's IL, signatures or metadata reference them:

- GridSim.Core outside the namespaces `GridSim.Core.Dynamics.Models`, `GridSim.Core.Dynamics.Plugins` and
  `GridSim.Core.Numerics` (so no `Save(path)` or other file API of Core, and no Core table), and in those the loader's own
  types (`PluginConfig`, `PluginLoader`, `PluginSet`, `PluginVerifier`, `PluginReplay`, `PluginDenylist`) and the SIMD
  capability readings (`SimdCapabilities`, `SimdTier`).
- Writing a static field of another assembly, taking its address, or reading one that holds anything but a number, bool,
  char or string (an array or object there could be written through). A plug-in's own statics are its own business; the
  replay checks their effect.
- Members of `System.Double` other than the exact ones (`IsNaN`, `IsFinite` and the other classifications, comparisons,
  `Abs`, `Min`, `Max`, `Clamp`, `CopySign`, `Sign`, `Sqrt`, `Floor`, `Ceiling`, `Round`, `Truncate`, `BitIncrement`,
  `BitDecrement`, `GetHashCode`): so `double.Sin`, `double.Exp`, `double.FusedMultiplyAdd` and the rest of its maths, and
  parsing and formatting. `System.Half`. `Parse` and `TryParse` on the integer types, and `System.Convert`.
- `GetHashCode` on any type but the numbers, bool, char and enums (string, comparer, tuple and object hash codes are
  randomised per process or follow object identity).
- `IEnumerable`, `IEnumerable<T>`, `ICollection`, `ICollection<T>`, the dictionary and set interfaces, LINQ
  (`System.Linq`), `Dictionary` and `HashSet` enumeration (`GetEnumerator`, `Keys`, `Values`, their enumerator and key and
  value collection types, `HashSet.CopyTo`), `ImmutableHashSet`, `ImmutableDictionary`, the frozen, concurrent and
  specialised collections, and the JSON, XML and serialisation namespaces. Iterate arrays, `List<T>` and
  `SortedDictionary` directly (an `IReadOnlyList<T>` by index).
- Environment readings: `OperatingSystem`, the current culture (`CultureInfo.CurrentCulture` and its relatives,
  `NumberFormatInfo.CurrentInfo`, `RegionInfo.CurrentRegion`), `TimeZoneInfo`, `JitInfo`, `GCSettings`, `RuntimeFeature`,
  weak references.
- Reflection: `System.Type`, the `System.Reflection` namespace (its attribute types excepted: the SDK writes them on every
  assembly), expression trees (`System.Linq.Expressions`), `Delegate.DynamicInvoke`, `UnsafeAccessor`, an
  `IgnoresAccessChecksTo` attribute (defined or referenced), and any method without a body of its own (extern).
- Every framework namespace but the few a numerical model needs. A framework type (`System.*`, `Microsoft.*`) is allowed
  only in `System`, `System.Collections.Generic`, `System.Collections.Immutable`, `System.Collections.ObjectModel`,
  `System.Diagnostics.CodeAnalysis`, `System.Globalization`, `System.Runtime.CompilerServices`, `System.Runtime.Versioning`
  and `System.Text`, and the attribute types of `System.ComponentModel`, `System.Diagnostics` and `System.Reflection` (with
  the enums `EditorBrowsableState` and `DebuggerBrowsableState` they take);
  the lists above still apply inside them. So a file or network route in any other namespace is refused without being
  named: `System.Formats.Tar`, `System.Data` (`DataSet.WriteXml` and `ReadXml`), `System.Resources`,
  `TextWriterTraceListener`, `Trace` and `FileVersionInfo` in `System.Diagnostics`, and `System.Buffers` (`ArrayPool`, state
  shared with the host). A plug-in references only `GridSim.Core` and the framework assemblies that hold those namespaces
  (`System.Runtime`, `System.Collections`, `System.Collections.Immutable`, `System.Memory`, `System.ObjectModel`,
  `System.Runtime.Extensions`, `System.Private.CoreLib`, `System`, `mscorlib`, `netstandard`).

A plug-in is trusted code. The lint and the replay check catch accidents against the determinism contract; .NET has no
in-process sandbox, so load only plug-ins you would run as a program of your own. `breaking-change validate` never re-runs
a plug-in job, and `rpc --listen` never loads one.

What the lint does not see: it checks names, not data flow. Text formatted with the current culture without naming it
(string interpolation, `ToString()` without a provider, culture-sensitive string comparison) is not refused, nor a
framework method that takes `IReadOnlyCollection<T>` or `object` and enumerates a dictionary or set handed to it. A
record type is refused (its generated members use `System.Type` and `EqualityComparer` hash codes).

The replay check runs each model 200 steps from two operating points of its role (a 0.01 pu input step at step 100): one
instance alone from the first point, one alone from the second, then two fresh instances side by side, one from each
point, initialised one after the other and stepped in turn. Each paired instance must repeat its solo run bit for bit, and
an injection's current asked twice at the same voltage must give the same bits. A relay is replayed twice, with a unit's
inputs and with a branch's (no frequency, rate of change or speed), since it may be bound to either. Because the paired instances start at
different points, state shared through a static (a value one instance writes and the other reads, or a shared counter)
shows as a difference. It cannot prove determinism: sharing that changes no output in those 200 steps from those two
points passes.

Loading rules. A plug-in is loaded only from a `gridsim-plugins/1` configuration named on the command line: no default
folder, no environment variable, no discovery. Each path must resolve inside the configuration's `root` (default: the
configuration's folder): no `..` escape, no absolute path outside, no network or device path, no symbolic link or junction
on any component (the scripting `--confine` rule). The SHA-256 of the file must equal `sha256`; the bytes are read once and
the same bytes are checked and loaded. The assembly must carry the attribute and reference only the framework and
GridSim.Core, and may not take the name of an assembly GridSim grants its internals to (an `InternalsVisibleTo` target of a
GridSim assembly, such as GridSim.Tests; the names are read from the attributes, ignoring case). The listed ids must be unique and differ from every shipped model. Every rule is checked for every entry before
anything is loaded; the assemblies then load into one collectible context per configuration. `rpc` refuses the plug-in routes
over `--listen`, and the web app does not offer them.

What this is not. The lint and the replay are a determinism check, not a security boundary: .NET has no in-process sandbox,
so a plug-in runs with your rights. Load only assemblies you trust; the hash pin protects against a swapped file, not against
its author. Results with plug-ins are outside the verified contract: GridSim verifies the route (a plug-in that re-expresses
TGOV1 or SEXS gives the coded output bit for bit), not the plug-in's maths. Out of scope: the PSS/E USRMDL/CONEC/CONET
compiled-model interface, PowerFactory DSL import, the IEEE/Cigre real-code DLL interface for EMT, user machine (rotor and
flux) models and relay models. A plug-in stabiliser is attached through the API (`RmsExciter.WithLibraryStabiliser(new
PluginStabiliser(model))`); a study binds governor and exciter plug-ins. Injection and load plug-ins are reached through the
API (as for the declarative roles above), not yet from the `dynamics-study` file. `modal-full` takes declarative models
only, and a model with an `expr` block is reported NOT LINEARISED there (no linearisation is defined for it).
