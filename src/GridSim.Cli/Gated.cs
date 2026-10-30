using System.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Ucte;
using GridSim.Core.Licensing;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// The CLI's single licence choke point for loading user network models. Every user-facing model load
/// in <c>Program.cs</c> goes through <see cref="Dir"/> or <see cref="Matpower"/> instead of calling the
/// Core loaders directly, so the tier's bus limit (LICENSE.md §2) is enforced in exactly one place.
///
/// Bundled Networks (LICENSE.md §1 - the shipped IEEE/CIGRE MATPOWER library and the synthetic/reduced
/// GB cases) are exempt and never counted; a load is "bundled" when its path sits under the repo's
/// <c>data/</c> tree. Built-in cases (<c>case9</c> …) never touch these loaders and so are exempt too.
/// </summary>
internal static class Gated
{
    private static LicenceGate _gate = new(Entitlement.FreeDefault);

    // The install folder (AppContext.BaseDirectory), whose data/matpower tree is searched for a bare .m name.
    // Null until Init runs; a test harness that never calls Init simply gets no library search.
    private static string? _baseDir;

    /// <summary>Wire in the resolved gate once at start-up. <paramref name="baseDir"/> locates the shipped
    /// <c>data/matpower</c> library that <see cref="Matpower"/> searches for a bare file name (bundled detection
    /// itself stays path-shape based).</summary>
    public static void Init(LicenceGate gate, string baseDir)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(baseDir);
        _gate = gate;
        _baseDir = baseDir;
    }

    /// <summary>The active gate, for the <c>licence</c> verb and any status reporting.</summary>
    public static LicenceGate Gate => _gate;

    // N187 (D2 nominal frequency, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the nominal frequency
    // declared on the command line with --frequency-hz, or null. A MATPOWER file carries no system frequency, so without
    // the flag a .m case is assumed to be 50 Hz (MatpowerParser.AssumedFrequencyHz, stated in a parse note); a 60 Hz case
    // (the IEEE and WSCC test systems, most of the MATPOWER library) needs the flag for its dynamic studies (modal, EMT,
    // stability, impedance, IEC 60909) to run at the right f0. It applies to every .m file this run loads through
    // Matpower / MatpowerDetailed, and to nothing else: a JSON case folder carries its own frequencyHz and the built-in
    // case names carry theirs. Whether it was applied is recorded so the CLI can say so when it was not (see Program.cs).
    private static double? _declaredFrequencyHz;

    /// <summary>N187: the nominal frequency (Hz) declared with <c>--frequency-hz</c>, applied to every MATPOWER file this
    /// run loads; null when the flag was not given. Set once at start-up by Program.cs, which has already refused a value
    /// that is not a finite number above 0 Hz.</summary>
    public static double? DeclaredFrequencyHz
    {
        get => _declaredFrequencyHz;
        set
        {
            if (value is double f && !(f > 0.0 && double.IsFinite(f)))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The declared nominal frequency must be a positive, finite number of hertz.");
            _declaredFrequencyHz = value;
        }
    }

    /// <summary>N187: true once a MATPOWER file has been loaded with the declared frequency (false while none has).</summary>
    public static bool DeclaredFrequencyApplied { get; private set; }

    /// <summary>Load a JSON case directory, enforcing the licence unless it is a bundled network.</summary>
    public static GridModel Dir(string path)
    {
        // A missing folder is an input error with a clear message naming the full path. It is THROWN, not exited
        // on, so a batch verb that loads many cases can still report this one as LOAD FAIL and carry on; the
        // process-wide handler at the top of Program.cs turns an uncaught one into "error: ..." and exit 1.
        RequireDirectory(path);
        GridModel model = Unloadable(path, "case folder", () => JsonGridLoader.LoadDirectory(path));
        Enforce(path, model);
        // N113 (October 2026): three of the shipped intl maps have no AC power-flow solution as shipped (data, diagnosed in
        // data/intl/README.md). Say so on stderr at load, so a non-convergence that follows is not mistaken for a solver
        // fault; stdout stays the verb's own report.
        if (NoSolutionNote(path) is { } note) Console.Error.WriteLine(note);
        return model;
    }

    /// <summary>
    /// N113 (October 2026): the shipped intl maps that have no AC power-flow solution as shipped, each with the reason in
    /// one line (the evidence is in data/intl/README.md). Keyed by folder name under data/intl.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> IntlNoSolution = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["iberia"] = "225 of its 227 units are fixed (pq) injections, so only the two island slacks hold voltage against 22,811 MVAr of line charging",
        ["france"] = "293 of its 295 units are fixed (pq) injections, so only the two island slacks hold voltage against 34,473 MVAr of line charging, and its generation is far from its synthetic demand",
        ["continental-europe"] = "1,604 of its 1,608 units are fixed (pq) injections, so only the four island slacks hold voltage against 140,717 MVAr of line charging, and its generation is far from its synthetic demand",
    };

    /// <summary>N113: the stderr note for a bundled intl map with no AC solution, or null for any other folder.</summary>
    internal static string? NoSolutionNote(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string name = Path.GetFileName(full);
        string? parent = Path.GetFileName(Path.GetDirectoryName(full));
        if (parent is null || !parent.Equals("intl", StringComparison.OrdinalIgnoreCase) || !IsBundledPath(path)) return null;
        return IntlNoSolution.TryGetValue(name, out string? why)
            ? $"note: data/intl/{name}: this indicative map has no AC power-flow solution as shipped: {why} (data, not the solver; see data/intl/README.md). A verb that solves it reports the non-convergence."
            : null;
    }

    /// <summary>
    /// Parse a MATPOWER <c>.m</c> file, repair raw engineering units, then enforce the licence on the final model
    /// unless it is a bundled case. This is the ONE place the CLI loads a <c>.m</c> file.
    ///
    /// R021 (October 2026 review): the unit repair (<see cref="MatpowerUnitNormalizer.Normalize"/>, which turns a raw
    /// ohm / kW distribution feeder such as <c>case33bw</c> into per-unit / MW) used to be wrapped around some call
    /// sites and not others, so the same file was solved normalised by the bare-case path but in ohms-as-pu by
    /// crossval, stress, chain, nminus1-sweep, cycle, losses and bench. Normalising here, once, makes every verb solve
    /// the same network. A file already in per-unit is returned unchanged (Normalize is a no-op on it), so per-unit
    /// cases are bit-identical to before.
    ///
    /// N093 (October 2026): the parse notes (the assumed nominal frequency, DC lines not modelled, dropped isolated buses,
    /// out-of-service units kept, reference buses with no unit, PV buses solved as PQ, ...) are printed on stderr for
    /// every verb that loads a .m file, one <c>note: &lt;file&gt;: &lt;text&gt;</c> line each; they used to be discarded
    /// by every verb except <c>convert</c>'s DC-line warning. stdout is unchanged. The model is still built by the one
    /// normalising load below, so every number is unchanged; the notes come from a second, diagnostic parse of the same
    /// text (ParseFile is ParseWithDiagnostics(text, file stem).Model, so the notes are this model's own).
    /// </summary>
    public static GridModel Matpower(string path)
    {
        // A name that is not a file where it was given may be one of the shipped library cases (see ResolveMatpower).
        path = ResolveMatpower(path);
        // Same rule as Dir: a missing .m file throws FileNotFoundException with a clear message (exit 1 if uncaught).
        RequireFile(path, "MATPOWER case file");
        // N187: the declared --frequency-hz (null = the parser's documented 50 Hz assumption, noted below).
        double? frequencyHz = _declaredFrequencyHz;
        GridModel model = Unloadable(path, "MATPOWER case file",
            () => MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(path, frequencyHz: frequencyHz)));
        Enforce(path, model);
        // The diagnostic parse takes the same frequency, so the "nominal frequency not given ... assumed 50 Hz" note is
        // printed only when no frequency was declared.
        ReportParseNotes(path, MatpowerParser.ParseWithDiagnostics(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), frequencyHz));
        if (frequencyHz is not null) DeclaredFrequencyApplied = true;
        return model;
    }

    /// <summary>N093: print a MATPOWER parse's notes on stderr, one <c>note: &lt;file&gt;: &lt;text&gt;</c> line each, plus
    /// the DC-line warning when the case has DC lines (no GridSim solver models them).</summary>
    private static void ReportParseNotes(string path, MatpowerParseResult parsed)
    {
        string file = Path.GetFileName(path);
        foreach (string note in parsed.Notes) Console.Error.WriteLine($"note: {file}: {note}");
        if (parsed.DcLines.Count > 0)
            Console.Error.WriteLine($"warning: '{path}' has {parsed.DcLines.Count} DC line(s) (mpc.dcline); GridSim does not model them, so its power flow differs from MATPOWER's for this case.");
    }

    /// <summary>Tranche A: set by <c>--allow-unmodelled</c>; every PSS/E RAW file this run loads is read with
    /// <see cref="PsseReadOptions.AllowUnmodelled"/>.</summary>
    public static bool AllowUnmodelled { get; set; }

    /// <summary>Tranche A: set by <c>--tap-control</c>; every PSS/E RAW file this run loads is read with
    /// <see cref="PsseReadOptions.ApplyTapControl"/>, and the steady-state solvers get a ramp start of
    /// <see cref="TapControlRampStages"/> stages.</summary>
    public static bool ApplyTapControl { get; set; }

    /// <summary>Tranche A: set by <c>--shunt-control</c>; every PSS/E RAW file this run loads is read with
    /// <see cref="PsseReadOptions.ApplyShuntControl"/>, and its regulating switched shunts are kept for the solvers
    /// (<see cref="ShuntsOf"/>).</summary>
    public static bool ApplyShuntControl { get; set; }

    // Tranche A: the regulating switched shunts of each model a .raw file gave (--shunt-control), and the shunt loop each
    // case solver drives, so the report can say how the banks ended. Weak tables: they never keep a model alive.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridModel, IReadOnlyList<GridSim.Core.PowerFlow.SwitchedShunt>> _shunts = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridSim.Core.PowerFlow.SteadyStateSolver, GridSim.Core.PowerFlow.SwitchedShuntPowerFlow> _shuntLoops = new();

    /// <summary>Tranche A: the regulating switched shunts the .raw file of <paramref name="m"/> gave (with
    /// <c>--shunt-control</c>); null for any other model.</summary>
    public static IReadOnlyList<GridSim.Core.PowerFlow.SwitchedShunt>? ShuntsOf(GridModel m) => _shunts.TryGetValue(m, out var s) ? s : null;

    /// <summary>Tranche A: remember the shunt loop <paramref name="solver"/> drives.</summary>
    public static void RememberShuntLoop(GridSim.Core.PowerFlow.SteadyStateSolver solver, GridSim.Core.PowerFlow.SwitchedShuntPowerFlow loop) => _shuntLoops.AddOrUpdate(solver, loop);

    /// <summary>Tranche A: the shunt loop <paramref name="solver"/> drives, or null.</summary>
    public static GridSim.Core.PowerFlow.SwitchedShuntPowerFlow? ShuntLoopOf(GridSim.Core.PowerFlow.SteadyStateSolver solver) => _shuntLoops.TryGetValue(solver, out var l) ? l : null;

    /// <summary>Tranche A: the ramp-start stage count <c>--tap-control</c> gives the steady-state solvers
    /// (<see cref="GridSim.Core.PowerFlow.SteadyStateSolver.LoadRampStages"/>): 10 % of demand and generation per stage.</summary>
    public const int TapControlRampStages = 10;

    // Tranche A: the last PSS/E RAW file this run loaded, whose machine keys a .dyr file is mapped through.
    private static PsseParseResult? _lastPsse;

    /// <summary>
    /// Tranche A (October 2026): a <c>--dyn-data</c> PSS/E DYR file. Its records are mapped onto the machines of the PSS/E RAW
    /// case this run loaded (<see cref="PsseDyrReader"/>, with <c>--allow-unmodelled</c> passed on), and the
    /// <c>gridsim-dynamics/2</c> document that gives is checked and bound by <see cref="MachineDynamicsLoader"/> exactly as
    /// a dynamics file is, from a temporary copy named <c>&lt;file&gt;.dynamics.json</c> that is removed after loading. The
    /// mapping notes are printed on stderr (<c>note: &lt;file&gt;: ...</c>). A run whose case is not a RAW file, or is not
    /// the network the RAW file gave (a different generator fleet), is refused: the DYR names machines by RAW bus and ID.
    /// </summary>
    public static MachineDynamicsSet PsseDynamics(string path, string schemasDir, GridModel model)
    {
        RequireFile(path, "PSS/E DYR file");
        PsseParseResult? raw = _lastPsse;
        if (raw is null || !SameFleet(raw.Model, model))
            throw new InvalidDataException($"'{path}' is a PSS/E DYR file, which names machines by the bus and ID of a PSS/E .raw case; this run's case is not the .raw file's network.");
        string file = Path.GetFileName(path);
        PsseDyrResult dyr = PsseDyrReader.Read(File.ReadAllText(path), raw, Path.GetFileNameWithoutExtension(path),
            new PsseDyrOptions { AllowUnmodelled = AllowUnmodelled, SourceName = file });
        foreach (string note in dyr.Notes) Console.Error.WriteLine($"note: {file}: {note}");
        // WECC (expansion): a DYR that gives only renewable units has no machine document to load; its renewable chains go to
        // the RMS study with the set (MachineDynamicsSet.Renewables) either way.
        if (dyr.Machines == 0) return MachineDynamicsSet.Empty("PSS/E DYR " + Path.GetFileNameWithoutExtension(path), path).WithRenewables(dyr.Renewables);
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-dyr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string json = Path.Combine(dir, file + ".dynamics.json");
            File.WriteAllText(json, dyr.Json);
            return MachineDynamicsLoader.Load(json, schemasDir, model).WithRenewables(dyr.Renewables);
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // a temporary copy left behind is harmless
        }
    }

    // The same generator fleet, unit for unit (bus and service state), so RAW machine keys still line up with the model.
    private static bool SameFleet(GridModel a, GridModel b)
    {
        if (a.AllGenerators.Count != b.AllGenerators.Count) return false;
        for (int i = 0; i < a.AllGenerators.Count; i++)
            if (a.AllGenerators[i].Bus != b.AllGenerators[i].Bus || a.AllGenerators[i].InService != b.AllGenerators[i].InService) return false;
        return true;
    }

    /// <summary>Tranche A: true once a PSS/E RAW file has been loaded (so the CLI can say when --allow-unmodelled reached nothing).</summary>
    public static bool PsseLoaded { get; private set; }

    /// <summary>Tranche A: true once a UCTE-DEF file has been loaded (--allow-unmodelled also reaches its ##TT tables).</summary>
    public static bool UcteLoaded { get; private set; }

    /// <summary>
    /// Tranche A (October 2026): read a UCTE-DEF file (<see cref="UcteParser"/>), then enforce the licence on the model, as
    /// <see cref="Psse"/> does for a .raw file. This is the ONE place the CLI loads a .uct file. The declared
    /// <c>--frequency-hz</c> is the frequency (a UCTE-DEF file carries none; otherwise 50 Hz is assumed and noted);
    /// <c>--allow-unmodelled</c> reads a file whose transformers in operation have special tables (##TT). Every parse note is
    /// printed on stderr as <c>note: &lt;file&gt;: &lt;text&gt;</c>.
    /// </summary>
    public static GridModel Ucte(string path)
    {
        RequireFile(path, "UCTE-DEF case file");
        double? frequencyHz = _declaredFrequencyHz;
        UcteParseResult? parsed = null;
        GridModel model = Unloadable(path, "UCTE-DEF case file", () => (parsed = UcteParser.ParseFile(path,
            new UcteReadOptions { FrequencyHz = frequencyHz, AllowUnmodelled = AllowUnmodelled })).Model);
        Enforce(path, model);
        string file = Path.GetFileName(path);
        foreach (string note in parsed!.Notes) Console.Error.WriteLine($"note: {file}: {note}");
        UcteLoaded = true;
        if (frequencyHz is not null) DeclaredFrequencyApplied = true;
        return model;
    }

    /// <summary>Tranche A: set by <c>--psse-revision</c>; the revision a PSS/E RAW file is read as when its first line does not
    /// say (no REV field and no PSS/E-NN comment, as in the UKGDS files). A file that does say, and differs, is refused.</summary>
    public static int? PsseRevision { get; set; }

    /// <summary>Tranche A: the revisions the RAW reader supports, for the <c>--psse-revision</c> check (Program.cs reaches the
    /// reader only through this class).</summary>
    public static IReadOnlyList<int> PsseRevisions => PsseRawParser.SupportedRevisions;

    /// <summary>Tranche A: set by <c>--seq-data</c>; a PSS/E SEQ file applied (<see cref="PsseSeqReader"/>) to every PSS/E RAW
    /// file this run loads, so the fault and IEC 60909 studies get its sequence data.</summary>
    public static string? SeqDataPath { get; set; }

    /// <summary>
    /// Tranche A (October 2026): read a PSS/E RAW file (revisions 30, 33, 34, 35), then enforce the licence on the model, as
    /// <see cref="Matpower"/> does for a .m file. This is the ONE place the CLI loads a .raw file. The declared
    /// <c>--frequency-hz</c> is the frequency when the file's BASFRQ is 0 (a file that states a different BASFRQ is refused);
    /// <c>--allow-unmodelled</c> reads a file holding in-service devices GridSim does not model. Every parse note is printed
    /// on stderr as <c>note: &lt;file&gt;: &lt;text&gt;</c> (the N093 convention), so stdout stays the verb's own report.
    /// <c>--psse-revision</c> gives the revision of a file that does not state it. <c>--seq-data</c> applies a SEQ file's
    /// sequence data to the model (its notes printed the same way, naming the SEQ file); the DYR mapping still uses the
    /// RAW parse, whose machines the SEQ data does not change in number or order.
    /// </summary>
    public static GridModel Psse(string path)
    {
        RequireFile(path, "PSS/E RAW case file");
        double? frequencyHz = _declaredFrequencyHz;
        string? seqPath = SeqDataPath;
        if (seqPath is not null) RequireFile(seqPath, "PSS/E SEQ file");
        PsseParseResult? parsed = null;
        PsseSeqResult? seq = null;
        GridModel model = Unloadable(path, "PSS/E RAW case file", () =>
        {
            parsed = PsseRawParser.ParseFile(path, new PsseReadOptions { FrequencyHz = frequencyHz, AllowUnmodelled = AllowUnmodelled, Revision = PsseRevision, ApplyTapControl = ApplyTapControl, ApplyShuntControl = ApplyShuntControl });
            if (seqPath is null) return parsed.Model;
            seq = PsseSeqReader.ReadFile(seqPath, parsed, new PsseSeqOptions { AllowUnmodelled = AllowUnmodelled });
            return seq.Model;
        });
        Enforce(path, model);
        string file = Path.GetFileName(path);
        foreach (string note in parsed!.Notes) Console.Error.WriteLine($"note: {file}: {note}");
        if (seq is not null)
        {
            string seqFile = Path.GetFileName(seqPath!);
            foreach (string note in seq.Notes) Console.Error.WriteLine($"note: {seqFile}: {note}");
            Console.Error.WriteLine($"note: {seqFile}: applied sequence data: {seq.PositiveSequenceMachines} machine X''d, {seq.NegativeSequenceMachines} machine X2, " +
                $"{seq.ZeroSequenceLines} line Z0, {seq.ZeroSequenceTransformers} transformer Z0 and winding connections.");
            _lastPsse = parsed with { Model = model };
        }
        else _lastPsse = parsed;
        PsseLoaded = true;
        if (parsed!.SwitchedShunts.Count > 0) _shunts.AddOrUpdate(model, parsed.SwitchedShunts);
        if (frequencyHz is not null) DeclaredFrequencyApplied = true;
        return model;
    }

    /// <summary>
    /// <see cref="Matpower"/> with the whole parse result, for the <c>convert</c> verb (F24): the same normalised,
    /// licence-checked model (ParseFile is ParseWithDiagnostics(text, file stem).Model, so the model is built exactly as
    /// <see cref="Matpower"/> builds it) plus the parse notes and the DC lines (<c>mpc.dcline</c>), which convert writes
    /// back out. Every parse note is printed on stderr (N093), and a case with DC lines also gets the warning line,
    /// because no GridSim solver models them (MATPOWER adds their flows as injections).
    /// </summary>
    public static MatpowerParseResult MatpowerDetailed(string path)
    {
        // A name that is not a file where it was given may be one of the shipped library cases (see ResolveMatpower).
        path = ResolveMatpower(path);
        // Same rule as Dir: a missing .m file throws FileNotFoundException with a clear message (exit 1 if uncaught).
        RequireFile(path, "MATPOWER case file");
        MatpowerParseResult? parsed = null;
        double? frequencyHz = _declaredFrequencyHz;   // N187: the declared --frequency-hz, as in Matpower
        GridModel model = Unloadable(path, "MATPOWER case file", () =>
        {
            parsed = MatpowerParser.ParseWithDiagnostics(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), frequencyHz);
            return MatpowerUnitNormalizer.Normalize(parsed.Model);
        });
        Enforce(path, model);
        // N093: every parse note, on stderr (stdout stays the verb's own report).
        ReportParseNotes(path, parsed!);
        if (frequencyHz is not null) DeclaredFrequencyApplied = true;
        return parsed! with { Model = model };
    }

    /// <summary>
    /// Read back a MATPOWER file the <c>convert</c> verb has just written, to prove the round trip (F24): parsed and
    /// unit-normalised exactly as <see cref="Matpower"/> does, with the source model's nominal frequency, but NOT counted
    /// against the licence (it is an internal check of the verb's own output, not a user load).
    /// </summary>
    /// <summary>Export pack: read back a RAW file <c>convert</c> has just written, with tap and shunt control applied so the
    /// written tap changers, flow controls and static var compensators are rebuilt (an internal check: no licence count).</summary>
    public static GridModel ReadBackPsse(string path)
        => PsseRawParser.ParseFile(path, new PsseReadOptions { ApplyTapControl = true, ApplyShuntControl = true }).Model;

    /// <summary>Export pack: read back a CGMES file <c>convert</c> has just written, with the SV starting voltages and the
    /// ratings, on the source model's system base (CGMES carries none). An internal check: no licence count.</summary>
    public static GridModel ReadBackCgmes(string path, double baseMva)
        => GridSim.Core.IO.Cim.CgmesWriter.ReadBack(File.ReadAllText(path), baseMva, Path.GetFileNameWithoutExtension(path));

    public static GridModel ReadBackMatpower(string path, double frequencyHz)
        => MatpowerUnitNormalizer.Normalize(MatpowerParser.Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), frequencyHz));

    /// <summary>
    /// S2 / P02: load the PyPSA-GB reduced network (<c>pypsa-gb-reduced</c>) from the shipped data folder. Its demand and
    /// generation come from GBreducednetwork.m (PyPSA-GB's network files carry none), which is loaded through
    /// <see cref="Matpower"/>, so the licence rule is the same as for any shipped .m file (bundled, never counted).
    /// The import notes (how PyPSA-GB's lines differ from the .m file's) are printed on stderr, so stdout is the verb's
    /// own report.
    /// </summary>
    public static GridModel PypsaGbReduced(string root)
    {
        string data = Path.Combine(root, "data");
        GridModel op = Matpower(Path.Combine(data, GridSim.Core.IO.PyPsa.GbReferenceModels.Gb29RelativePath));
        string folder = Path.Combine(data, GridSim.Core.IO.PyPsa.GbReferenceModels.PypsaReducedRelativeDir);
        RequireDirectory(folder);
        GridSim.Core.IO.PyPsa.PypsaGbReducedNetwork? net = null;
        GridModel model = Unloadable(folder, "PyPSA-GB reduced network",
            () => (net = GridSim.Core.IO.PyPsa.PypsaGbReduced.Load(folder, op)).Model);
        Enforce(folder, model);
        foreach (string note in net!.Notes) Console.Error.WriteLine($"note: {GridSim.Core.IO.PyPsa.GbReferenceModels.PypsaReducedId}: {note}");
        return model;
    }

    /// <summary>
    /// N065 (October 2026, JSON v2 Phase 2): load a <c>gridsim-case/2</c> manifest (a <c>.json</c> file) with its network
    /// files and library imports, enforcing the licence like any other load (bundled detection is the same path rule).
    /// The v2 schemas are found as the <c>validate</c> verb finds them (<see cref="FindV2Schemas"/>); library imports are
    /// relative to the shipped <c>data</c> folder beside the program (<paramref name="root"/>/data). A content failure is
    /// a <see cref="CaseLoadException"/> (exit 1 if uncaught, a LOAD FAIL row in a batch verb). The load's notes (for
    /// example which revision an unpinned import resolved to) are printed on stderr as <c>note: &lt;file&gt;: ...</c>,
    /// the parse-note convention of N093, so stdout stays the verb's own report.
    /// </summary>
    public static GridModel V2Case(string path, string root)
    {
        RequireFile(path, "JSON v2 case manifest");
        string schemas = FindV2Schemas(root)
            ?? throw new CaseLoadException($"cannot load JSON v2 case '{path}': the v2 schemas were not found (looked for schemas/v2 beside the program and above it, and above the current folder).",
                new DirectoryNotFoundException("schemas/v2"));
        GridSim.Core.IO.Json.V2CaseLoadResult? loaded = null;
        GridModel model = Unloadable(path, "JSON v2 case",
            () => (loaded = GridSim.Core.IO.Json.V2CaseLoader.LoadWithNotes(path, schemas, Path.Combine(root, "data"))).Model);
        Enforce(path, model);
        foreach (string note in loaded!.Notes) Console.Error.WriteLine($"note: {Path.GetFileName(path)}: {note}");
        return model;
    }

    /// <summary>
    /// The v2 schema folder: schemas/v2 beside the program, else the nearest one above the program folder or the current
    /// folder; null when there is none. Shared by the <c>validate</c> verb and <see cref="V2Case"/> (N065), so both read
    /// the same schemas.
    /// </summary>
    public static string? FindV2Schemas(string programDir)
    {
        foreach (string start in new[] { programDir, Directory.GetCurrentDirectory() })
        {
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "schemas", "v2");
                if (File.Exists(Path.Combine(candidate, "case.schema.json"))) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// S2 / P02: the refusal for an AC verb given the PyPSA-GB zonal model. A <see cref="CaseLoadException"/> (an input
    /// error), so every verb reports it the way it reports a case it cannot load: the process-wide handler and most
    /// verbs exit 1 with the message, and the batch verbs write a LOAD FAIL row. Never a crash, never a silent solve.
    /// </summary>
    public static CaseLoadException NotAcSolvable(string name) => new(
        $"case '{name}' cannot be solved: {GridSim.Core.IO.PyPsa.PypsaGbZonalNetwork.NotAcSolvableReason}. " +
        $"Show it with 'gridsim zonal {name}'.",
        new NotSupportedException(GridSim.Core.IO.PyPsa.PypsaGbZonalNetwork.NotAcSolvableReason));

    /// <summary>
    /// R059: load a three-phase distribution feeder (<c>gridsim-dist3ph/1</c>, or since N339 <c>gridsim-dist3ph/2</c>) and apply the same tier bus limit as a
    /// positive-sequence model, counting one bus per feeder node (not per phase). Bundled detection is the same
    /// path rule as every other load.
    /// </summary>
    public static GridSim.Core.Distribution.MultiPhaseFeeder Dist3ph(string path)
    {
        RequireFile(path, "three-phase feeder file");
        // N339: a gridsim-dist3ph/2 file goes to the v2 reader; every other file to the v1 reader exactly as before
        // (LoadAny hands it the same text, so its results and messages are unchanged).
        GridSim.Core.Distribution.MultiPhaseFeeder feeder = GridSim.Core.Distribution.MultiPhaseJsonLoader.LoadAny(path);
        GateResult r = _gate.Check(feeder.Model.Buses.Count, IsBundledPath(path));
        if (!r.Allowed)
        {
            Console.Error.WriteLine("error: " + r.Message);
            Environment.Exit(2);
        }
        return feeder;
    }

    /// <summary>
    /// Run a model loader and turn a content failure (a folder with no buses.json, a malformed .m or .json file:
    /// <see cref="InvalidDataException"/> or <see cref="System.Text.Json.JsonException"/>) into a
    /// <see cref="CaseLoadException"/> naming the path. CaseLoadException derives from <see cref="IOException"/>, so
    /// every existing caller that catches IOException or Exception (the batch verbs' LOAD FAIL rows) behaves as
    /// before, and the process-wide handler in Program.cs turns an uncaught one into "error: ..." and exit 1, the
    /// documented code for an unloadable case (user manual ch. 19). It used to crash with a stack trace.
    /// </summary>
    private static GridModel Unloadable(string path, string what, Func<GridModel> load)
    {
        try { return load(); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            throw new CaseLoadException($"cannot load {what} '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Turn a MATPOWER argument into the file to load. In order:
    /// <list type="number">
    /// <item>the path as given, when that file exists (so a user's own file always wins);</item>
    /// <item>a relative path under the shipped <c>data/matpower</c> folder, such as <c>pegase/case1354pegase.m</c>;</item>
    /// <item>a bare file name (no folder part), searched for through every subfolder of <c>data/matpower</c>. The
    /// matches are sorted by their relative path (ordinal), so the result never depends on the file system's
    /// enumeration order. Exactly one match loads; two or more is ambiguous and is refused with exit 2, listing every
    /// candidate, because silently picking one would analyse a network the caller may not have meant.</item>
    /// </list>
    /// When nothing matches, the argument is returned unchanged so <see cref="RequireFile"/> reports it as not found.
    /// Docs-programme finding: every shipped .m file sits in a subfolder (dist/, lib/, pegase/, ...), and the old
    /// fallback looked only at the top level, so a bare name such as <c>case33bw.m</c> never loaded.
    /// </summary>
    internal static string ResolveMatpower(string path)
    {
        if (File.Exists(path) || _baseDir is null) return path;
        string library = Path.Combine(_baseDir, "data", "matpower");
        if (!Directory.Exists(library) || Path.IsPathRooted(path)) return path;

        string underLibrary = Path.Combine(library, path);
        if (File.Exists(underLibrary)) return underLibrary;

        // Only a bare file name is searched for; "dist/nosuch.m" that is not under the library is simply not found.
        if (Path.GetFileName(path) != path) return path;
        List<string> matches = Directory.EnumerateFiles(library, path, SearchOption.AllDirectories)
            // EnumerateFiles' pattern matching has legacy 8.3 quirks on Windows; keep only exact name matches.
            .Where(f => string.Equals(Path.GetFileName(f), path, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetRelativePath(library, f), StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"error: MATPOWER case name '{path}' is ambiguous: it matches {matches.Count} files under data/matpower: " +
                string.Join(", ", matches.Select(f => Path.GetRelativePath(library, f).Replace('\\', '/'))) +
                ". Give the relative path (for example dist/case33bw.m).");
            Environment.Exit(2);
        }
        return path;
    }

    /// <summary>Throw <see cref="DirectoryNotFoundException"/> naming the full path when a case folder is missing.</summary>
    private static void RequireDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"case folder not found: '{path}' (full path {Path.GetFullPath(path)}).");
    }

    /// <summary>Throw <see cref="FileNotFoundException"/> naming the full path when an input file is missing.</summary>
    private static void RequireFile(string path, string what)
    {
        if (!File.Exists(path))
        {
            // D-DOC-217 (October 2026): the full path is added only when it says something the given path does not (a
            // relative path, resolved against the working directory). An absolute path used to be printed twice, and a
            // relative one is still shown as typed first, so a message quoted in the docs reads the same on every machine
            // up to the bracket.
            string full = Path.GetFullPath(path);
            string where = string.Equals(full, path, StringComparison.Ordinal) ? "" : $" (full path {full})";
            throw new FileNotFoundException($"{what} not found: '{path}'{where}.", path);
        }
    }

    private static void Enforce(string path, GridModel model)
    {
        GateResult r = _gate.Check(model.BusCount, IsBundledPath(path));
        if (r.Allowed) return;
        Console.Error.WriteLine("error: " + r.Message);
        Environment.Exit(2);
    }

    /// <summary>
    /// True when a path points at a Bundled Network shipped with the Software. Delegates to the one shared list
    /// in <see cref="BundledNetworks"/> (R002: this class kept its own list, which lacked <c>data/gb-full</c>, so
    /// the free tier refused the shipped GB case here while the Web and WPF apps loaded it). User models loaded
    /// from anywhere else (a working dir, a <c>gda-out/</c> tree) are licensed.
    /// </summary>
    internal static bool IsBundledPath(string path) => BundledNetworks.IsBundledPath(path);
}

/// <summary>
/// The built-in case names that <c>LoadNamed</c> in Program.cs resolves (lower case). Kept as a set so a verb can ask
/// "does this free token name a case?" without loading anything (trip and export use it to tell a case from a
/// generator name or an output path). <c>CliInputHandlingTests</c> checks that this set and LoadNamed's switch agree.
/// </summary>
internal static class BuiltInCases
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "case9", "ieee9", "case14", "case30", "case57", "case118", "case300", "case145", "case24", "rts", "cigre",
        "uk", "gb", "gb-reduced", "gb-full", "gb-synthetic", "synthetic",
        // S2 (October 2026): the published GB reference networks (data/matpower/gb). pypsa-gb-zonal is a name every verb
        // recognises as a case, and refuses (it is a transport model with no power flow; the zonal verb shows it).
        "gb-reduced-29", "pypsa-gb-reduced", "gb-network-2224", "pypsa-gb-zonal",
    };
}

/// <summary>
/// A user case that exists but cannot be loaded (bad content). An <see cref="IOException"/> so existing input-error
/// handling catches it; the CLI's process-wide handler maps an uncaught one to exit 1.
/// </summary>
internal sealed class CaseLoadException : IOException
{
    public CaseLoadException(string message, Exception inner) : base(message, inner) { }
}
