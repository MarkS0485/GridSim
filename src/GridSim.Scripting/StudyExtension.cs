using System.Collections.Immutable;
using System.Globalization;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Scripting;

// ---------------------------------------------------------------------------------------------------------------------
// RC5 WP11: the STUDIES extension of grammar 1. A script turns it on with the header GRAMMAR 1 WITH STUDIES (its first
// statement); without that header nothing here is reachable, and the parser, the session and the run record are exactly
// grammar 1's. With it, three studies can be run on the plant as edited, by the same Core entry points and default options
// the command line uses (nminus1, iec60909, acopf-full), and read back by name:
//
//   STUDY CONTINGENCY AS a                 N-1 branch screen     VIOLATIONS OF STUDY a (plain number), WORST LOADING OF STUDY a (PU)
//   STUDY SHORTCIRCUIT BUS "5" AS s        IEC 60909 3ph maximum IK OF BUS "5" IN STUDY s (KA)
//   STUDY OPF AS o                         full AC OPF           OBJECTIVE OF STUDY o (plain number), P OF GENERATOR "G1" IN STUDY o (MW)
//   STUDY SCOPF AS c                       DC preventive SCOPF   OBJECTIVE OF STUDY c, SECURED OF STUDY c (plain numbers)   (RC5 WP15)
//   STUDY RMS DYNAMICS "f.dyr" FAULT BUS "4" AT 0.1 S CLEAR AT 0.15 S FOR 3 S AS r
//                                          dynamics-study row    STABLE OF STUDY r (1 or 0), FINAL FREQUENCY OF STUDY r (HZ),
//                                                                MIN VOLTAGE OF BUS "4" IN STUDY r (PU)            (RC5 2.1.110)
//   STUDY CCT DYNAMICS "f.dyr" FAULT BUS "4" AT 0.1 S FOR 3 S AS c
//                                          dynamics-study CCT    CCT OF STUDY c (S)                                (RC5 2.1.110)
//   REPORT STUDY a                         the study's values as text
//
// The extension words are keywords only in such a script (Grammar.ExtensionReserved). KA is the unit of the IK reading
// and nothing else: it is not a unit a number can be written in.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>GRAMMAR 1 WITH STUDIES: grammar 1 plus the named extensions (only STUDIES exists).</summary>
public sealed record GrammarExtensionStatement(SourcePos Pos, Located<int> Version, ImmutableArray<string> Extensions) : Statement(Pos)
{
    public override string Canonical() => "GRAMMAR " + Version.Value.ToString(CultureInfo.InvariantCulture) + " WITH " + string.Join(" ", Extensions);
}

/// <summary>The studies of the extension.</summary>
public enum StudyKind { Contingency, ShortCircuit, Opf, Scopf, Rms, Cct }

/// <summary>RC5 2.1.110: the inputs of STUDY RMS and STUDY CCT: the DYR file, the fault time, the clearing time (RMS only)
/// and the run length.</summary>
public sealed record DynamicStudyArgs(Located<string> Dyr, Quantity FaultAt, Quantity? ClearAt, Quantity Duration);

/// <summary>STUDY CONTINGENCY AS name / STUDY SHORTCIRCUIT BUS "b" AS name / STUDY OPF AS name / STUDY SCOPF AS name /
/// STUDY RMS|CCT DYNAMICS "f.dyr" FAULT BUS "b" AT t [CLEAR AT t] FOR t AS name (the fault bus is <see cref="Bus"/>).</summary>
public sealed record StudyStatement(SourcePos Pos, StudyKind Kind, NameRef? Bus, Located<string> Name) : Statement(Pos)
{
    /// <summary>STUDY RMS and STUDY CCT only: the dynamic study's inputs.</summary>
    public DynamicStudyArgs? Dynamic { get; init; }

    public override string Canonical() => Dynamic is { } d
        ? "STUDY " + StudyExtension.Word(Kind) + " DYNAMICS " + Quote(d.Dyr.Value) + " FAULT BUS " + Bus!.Value.Canonical()
            + " AT " + d.FaultAt + (d.ClearAt is { } c ? " CLEAR AT " + c : "") + " FOR " + d.Duration + " AS " + Name.Value
        : "STUDY " + StudyExtension.Word(Kind) + (Bus is { } b ? " BUS " + b.Canonical() : "") + " AS " + Name.Value;
}

/// <summary>REPORT STUDY name: the study's values as text.</summary>
public sealed record ReportStudyStatement(SourcePos Pos, Located<string> Name) : Statement(Pos)
{
    public override string Canonical() => "REPORT STUDY " + Name.Value;
}

/// <summary>The readings of a study.</summary>
public enum StudyReading { Violations, WorstLoading, Objective, Ik, P, Secured, Stable, FinalFrequency, MinVoltage, Cct }

/// <summary>A reading of a study: VIOLATIONS / WORST LOADING / OBJECTIVE OF STUDY s, IK OF BUS b IN STUDY s, P OF GENERATOR g IN STUDY s.</summary>
public sealed record StudyExpr(SourcePos Pos, StudyReading Reading, Noun? Noun, NameRef? Ref, Located<string> Study) : Expr(Pos)
{
    public override string Canonical() => Reading switch
    {
        StudyReading.Violations => "VIOLATIONS OF STUDY " + Study.Value,
        StudyReading.WorstLoading => "WORST LOADING OF STUDY " + Study.Value,
        StudyReading.Objective => "OBJECTIVE OF STUDY " + Study.Value,
        StudyReading.Secured => "SECURED OF STUDY " + Study.Value,
        StudyReading.Stable => "STABLE OF STUDY " + Study.Value,
        StudyReading.FinalFrequency => "FINAL FREQUENCY OF STUDY " + Study.Value,
        StudyReading.Cct => "CCT OF STUDY " + Study.Value,
        StudyReading.MinVoltage => "MIN VOLTAGE OF BUS " + Ref!.Value.Canonical() + " IN STUDY " + Study.Value,
        StudyReading.Ik => "IK OF BUS " + Ref!.Value.Canonical() + " IN STUDY " + Study.Value,
        _ => "P OF GENERATOR " + Ref!.Value.Canonical() + " IN STUDY " + Study.Value,
    };
}

/// <summary>One study that has run: its values, kept by name in the session and written to the run record.</summary>
/// <param name="Violations">CONTINGENCY: the outages that fail after netting against the base case (with violations,
/// islanded or not solved), the count <c>nminus1</c> calls insecure.</param>
/// <param name="WorstLoadingPu">CONTINGENCY: the largest post-outage branch loading of a solved outage, per unit of rating A.</param>
public sealed record StudyRun(
    int Seq, int Line, string Name, StudyKind Kind, int PlantVersion,
    int Screened = 0, int Violations = 0, double WorstLoadingPu = 0,
    string? BusName = null, int BusId = 0, double IkKa = 0,
    double Objective = 0, int Iterations = 0, ImmutableArray<KeyValuePair<string, double>> GeneratorPMw = default,
    int Secured = 0, string? Status = null)
{
    /// <summary>RMS and CCT: the row's disposition (STABLE, UNSTABLE, CCT BRACKETED, ...).</summary>
    public string? Disposition { get; init; }
    /// <summary>RMS: the angle reference machine's frequency at the last sample (Hz).</summary>
    public double FinalFrequencyHz { get; init; }
    /// <summary>RMS: the least voltage magnitude over the run of each bus, by script bus name, in bus order (pu).</summary>
    public ImmutableArray<KeyValuePair<string, double>> MinVoltagePu { get; init; } = [];
    /// <summary>CCT: the longest clearing time found stable (s), and the shortest found unstable (null: none).</summary>
    public double CctStableSeconds { get; init; }
    /// <summary>CCT: the shortest clearing time found unstable (s), or null.</summary>
    public double? CctUnstableSeconds { get; init; }
}

/// <summary>The spelling, typing and running of the STUDIES extension.</summary>
public static class StudyExtension
{
    /// <summary>The one extension name.</summary>
    public const string Studies = "STUDIES";

    public static string Word(StudyKind k) => k switch
    {
        StudyKind.Contingency => "CONTINGENCY",
        StudyKind.ShortCircuit => "SHORTCIRCUIT",
        StudyKind.Scopf => ScopfWord,
        StudyKind.Rms => "RMS",
        StudyKind.Cct => CctWord,
        _ => "OPF",
    };

    /// <summary>RC5 2.1.110: STUDY CCT and CCT OF STUDY s. Read in place, not reserved.</summary>
    public const string CctWord = "CCT";

    /// <summary>RC5 2.1.110: STABLE OF STUDY s. Read in place, not reserved.</summary>
    public const string StableWord = "STABLE";

    /// <summary>RC5 2.1.110: FINAL FREQUENCY OF STUDY s. Read in place, not reserved.</summary>
    public const string FinalWord = "FINAL";

    /// <summary>RC5 2.1.110: STUDY RMS|CCT DYNAMICS "file.dyr" and MIN VOLTAGE OF BUS b IN STUDY s. Read in place.</summary>
    public const string DynamicsWord = "DYNAMICS", VoltageWord = "VOLTAGE";

    /// <summary>The fixed settings of STUDY RMS and STUDY CCT (the dynamics-study request they build): integration step,
    /// power-flow tolerance and iteration cap, the unmodelled-record policy, the slip and angle-spread criteria, and the
    /// CCT search window.</summary>
    public const double DynamicStepSeconds = 0.005, DynamicPowerFlowTolerancePu = 1e-8, DynamicSlipLimitRad = 2.0 * Math.PI,
        DynamicMaxAngleSpreadDeg = 180.0, CctMaxClearingSeconds = 1.0;

    /// <summary>The power-flow iteration cap of STUDY RMS and STUDY CCT.</summary>
    public const int DynamicPowerFlowMaxIterations = 100;

    /// <summary>RC5 WP15: STUDY SCOPF. Like <see cref="SecuredWord"/> it is read in place and is not a reserved word, so a
    /// STUDIES script written before WP15 that uses either as a name parses and runs as it did.</summary>
    public const string ScopfWord = "SCOPF";

    /// <summary>RC5 WP15: SECURED OF STUDY s, the number of outages a SCOPF study secured.</summary>
    public const string SecuredWord = "SECURED";

    /// <summary>The unit keyword of the IK reading. It is not in <see cref="Units.All"/>, so no number can be written in it.</summary>
    public const string KaKeyword = "KA";

    /// <summary>The type of a reading: a plain number, PU, KA or MW.</summary>
    public static VType TypeOf(StudyReading r) => r switch
    {
        StudyReading.Violations or StudyReading.Objective or StudyReading.Secured or StudyReading.Stable => VType.Scalar,
        StudyReading.WorstLoading or StudyReading.MinVoltage => VType.Of(Unit.Pu),
        StudyReading.FinalFrequency => VType.Of(Unit.Hz),
        StudyReading.Cct => VType.Of(Unit.S),
        StudyReading.Ik => VType.Of(Unit.Ka),
        _ => VType.Of(Unit.Mw),
    };

    private static ScriptException X(SourcePos pos, string msg) => new(ErrorPhase.Execution, pos, msg);

    private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Run one study on <paramref name="model"/> (the plant as edited). The entry points and options are the command line's
    /// defaults: <c>nminus1</c> (study solver in the OLTC loop, <see cref="ContingencyAnalysis.StudyOptions"/> with per-bus
    /// voltage limits and rating A, netted against the base case), <c>iec60909 --bus b</c> (three-phase, maximum, tb 0.10 s,
    /// Tk 1.0 s, peak Method B, f0 the case's), <c>acopf-full</c> (150 iterations). A study that has no answer (a base case
    /// that does not converge, an OPF that does not converge) is an execution error: nothing is kept.
    /// </summary>
    public static StudyRun Run(StudyStatement st, CaseInfo ci, GridModel model, int seq, int plantVersion)
    {
        switch (st.Kind)
        {
            case StudyKind.Contingency:
            {
                PowerFlowOptions options = ContingencyAnalysis.StudyOptions with { PerBusVoltageLimits = true, PostFaultRating = PostFaultRating.RateA };
                ContingencyResult baseCase = ContingencyAnalysis.ScreenBaseCase(model, ContingencyAnalysis.CreateStudySolver(model), options);
                if (!baseCase.Converged)
                    throw X(st.Pos, $"STUDY CONTINGENCY: the base case did not converge ({string.Join("; ", baseCase.Violations)}), so no outage can be screened against it");
                IReadOnlyList<ContingencyResult> results = ContingencyAnalysis.NetAgainstBase(
                    ContingencyAnalysis.ScreenBranches(model, ContingencyAnalysis.CreateStudySolver(model), options), baseCase);
                int failing = results.Count(c => !c.Converged || !c.Secure);
                double worst = 0;
                foreach (ContingencyResult c in results)
                    if (c.Converged && c.MaxBranchLoadingPercent / 100.0 > worst) worst = c.MaxBranchLoadingPercent / 100.0;
                return new StudyRun(seq, st.Pos.Line, st.Name.Value, st.Kind, plantVersion, Screened: results.Count, Violations: failing, WorstLoadingPu: worst);
            }
            case StudyKind.ShortCircuit:
            {
                string busName = st.Bus!.Value.Value;
                int busId = model.Buses[ci.BusIndex[busName]].Id;
                Iec60909.Duty d;
                try
                {
                    d = Iec60909.ThreePhase(model, busId, ShortCircuitObjective.Maximum, f0Hz: model.FrequencyHz, breakTimeSeconds: 0.10,
                        faultDurationSeconds: 1.0, peakFactorMethod: PeakFactorMethod.MethodB);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    throw X(st.Pos, "STUDY SHORTCIRCUIT: " + ex.Message);
                }
                return new StudyRun(seq, st.Pos.Line, st.Name.Value, st.Kind, plantVersion, BusName: busName, BusId: busId, IkKa: d.IkppKa);
            }
            case StudyKind.Scopf:
            {
                // The scopf verb's defaults: every branch outage, post-outage rating A, the fuel-type costs, constraint
                // generation with at most 50 rounds. Its reported objective is the LP's total cost (no constant term
                // without --linear-costs).
                ScopfResult r;
                try
                {
                    r = DcScopf.Solve(model, new ScopfOptions
                    {
                        Contingencies = null, PostContingencyRating = PostFaultRating.RateA, CostPerGen = null, ConstraintGeneration = true, MaxRounds = 50,
                    });
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
                {
                    throw X(st.Pos, "STUDY SCOPF: " + ex.Message);
                }
                if (!r.Optimal)
                    throw X(st.Pos, $"STUDY SCOPF: no secure dispatch ({r.Status}: {r.Message}); nothing is kept");
                return new StudyRun(seq, st.Pos.Line, st.Name.Value, st.Kind, plantVersion, Objective: r.TotalCost, Secured: r.Contingencies.Count,
                    Status: r.Status.ToString());
            }
            default:
            {
                AcOpfIpmResult r;
                try { r = AcOpfInteriorPoint.Solve(model, new AcOpfIpmOptions { MaxIterations = 150 }); }
                catch (InvalidDataException ex) { throw X(st.Pos, "STUDY OPF: " + ex.Message); }
                if (!r.Converged)
                    throw X(st.Pos, $"STUDY OPF: the AC optimal power flow did not converge ({r.Status}: {r.Message}); nothing is kept");
                // The OPF numbers the in-service units; the script names the whole fleet. Map each in-service unit to its name.
                var gens = ImmutableArray.CreateBuilder<KeyValuePair<string, double>>();
                var inServiceNames = new List<string>();
                for (int i = 0; i < model.AllGenerators.Count; i++)
                    if (model.AllGenerators[i].InService) inServiceNames.Add(ci.GeneratorNames[i]);
                foreach (AcOpfGenResult g in r.Generators.OrderBy(g => g.Index))
                    gens.Add(new KeyValuePair<string, double>(inServiceNames[g.Index], g.PgMw));
                return new StudyRun(seq, st.Pos.Line, st.Name.Value, st.Kind, plantVersion, Objective: r.ObjectivePerHour, Iterations: r.Iterations,
                    GeneratorPMw: gens.ToImmutable());
            }
        }
    }

    /// <summary>
    /// RC5 2.1.110: STUDY RMS and STUDY CCT. They build the <see cref="DynamicsStudyRequest"/> the <c>dynamics-study</c> verb
    /// builds from a study file and run it through the same <see cref="DynamicsStudyRunner.Run"/>, so the numbers are the
    /// verb's by construction. The case must be a PSS/E .raw file (the study names machines and branches by their RAW
    /// identity); it is read again from its file, checked against the SHA-256 LOAD CASE recorded, and parsed as the verb
    /// parses it. When the script has edited the plant, the plant as edited takes the case's place (the same elements in the
    /// same order, so the RAW identities still hold). The request: the DYR file named (its bytes read as the verb reads
    /// them), the v2 schemas found as SELECT ENGINE RMS finds them, initialisation 1e-8 pu and 100 iterations with
    /// unmodelled records refused, step 0.005 s, the run length FOR, slip limit 2 pi rad and angle spread 180 degrees, the
    /// first in-service machine as angle reference, channels the reference machine's frequency then every bus voltage in
    /// bus order. RMS: one events row (a bolted fault at the bus at AT, cleared in place at CLEAR AT). CCT: one critical
    /// clearing row (the fault at AT, cleared in place, searched up to 1.0 s, 18 halvings, 16 scan steps). A study that
    /// does not run (a refused model, a power flow or equilibrium that fails, a failed row) is an execution error.
    /// </summary>
    public static StudyRun RunDynamic(StudyStatement st, CaseInfo ci, GridModel model, int seq, int plantVersion,
        Func<string, SourcePos, string, string> resolve, string baseDir, Action<string, string>? input = null)
    {
        string word = "STUDY " + Word(st.Kind);
        DynamicStudyArgs d = st.Dynamic!;
        if (!string.Equals(ci.InputKind, "psse-raw", StringComparison.Ordinal))
            throw X(st.Pos, $"{word}: the case must be a PSS/E .raw file (the dynamic study names machines by their RAW identity); this one is {ci.InputKind}");
        string rawPath = resolve(ci.PathAsWritten, st.Pos, "LOAD CASE");
        byte[] rawBytes = File.ReadAllBytes(rawPath);
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rawBytes)), ci.Sha256, StringComparison.OrdinalIgnoreCase))
            throw X(st.Pos, $"{word}: the case file has changed since LOAD CASE read it");
        string dyrPath = resolve(d.Dyr.Value, d.Dyr.Pos, "DYNAMICS");
        if (!File.Exists(dyrPath)) throw X(d.Dyr.Pos, $"{word}: the DYR file \"{d.Dyr.Value}\" does not exist");
        byte[] dyrBytes = File.ReadAllBytes(dyrPath);
        input?.Invoke(d.Dyr.Value, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dyrBytes)).ToLowerInvariant());
        string schemas = ReplayEngine.FindSchemasFor(baseDir)
            ?? throw X(st.Pos, $"{word}: the v2 schemas (schemas/v2) were not found above the base folder");

        PsseParseResult raw = PsseRawParser.ParseWithDiagnostics(Decode(rawBytes), Path.GetFileNameWithoutExtension(rawPath), new PsseReadOptions());
        if (plantVersion != 0)
        {
            if (raw.Model.AllGenerators.Count != model.AllGenerators.Count || raw.Model.Buses.Count != model.Buses.Count
                || raw.Model.Branches.Count != model.Branches.Count)
                throw X(st.Pos, $"{word}: the plant as edited does not have the case's elements, so their RAW identities do not hold");
            raw = raw with { Model = model };
        }
        int busId = model.Buses[ci.BusIndex[st.Bus!.Value.Value]].Id;
        PsseMachineKey? first = null;
        for (int i = 0; i < raw.Model.AllGenerators.Count && i < raw.MachineKeys.Count; i++)
            if (raw.Model.AllGenerators[i].InService) { first = raw.MachineKeys[i]; break; }
        if (first is null) throw X(st.Pos, $"{word}: the case has no in-service machine to take as the angle reference");
        var reference = new DynamicsMachineId(first.Bus, first.Id);
        var channels = new List<DynamicsChannelRequest> { new(DynamicsChannelKind.Frequency, reference) };
        foreach (Bus b in raw.Model.Buses) channels.Add(new DynamicsChannelRequest(DynamicsChannelKind.BusVoltage, Bus: b.Id));
        double at = Seconds(d.FaultAt), length = Seconds(d.Duration);
        DynamicsCaseRequest row = st.Kind == StudyKind.Rms
            ? new DynamicsCaseRequest(st.Name.Value, DynamicsCaseKind.Events, new[]
            {
                new DynamicsEventRequest(DynamicsEventKind.BusFault, at, Bus: busId),
                new DynamicsEventRequest(DynamicsEventKind.FaultClear, Seconds(d.ClearAt!.Value), Bus: busId),
            })
            : new DynamicsCaseRequest(st.Name.Value, DynamicsCaseKind.CriticalClearing,
                Cct: new DynamicsCctRequest(busId, Array.Empty<DynamicsBranchId>(), at, CctMaxClearingSeconds));
        var request = new DynamicsStudyRequest(raw, Decode(dyrBytes), Path.GetFileName(dyrPath), schemas,
            new DynamicsInitialisation(DynamicPowerFlowTolerancePu, DynamicPowerFlowMaxIterations, DynamicsUnmodelledPolicy.Refuse),
            DynamicStepSeconds, length, new DynamicsCriteria(DynamicSlipLimitRad, DynamicMaxAngleSpreadDeg), reference, channels, new[] { row });
        DynamicsStudyResult result;
        try { result = DynamicsStudyRunner.Run(request); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            throw X(st.Pos, $"{word}: {ex.Message}");
        }
        DynamicsCaseResult c = result.Cases[0];
        bool ran = st.Kind == StudyKind.Rms ? c.Run is not null && c.Disposition is DynamicsDisposition.Stable or DynamicsDisposition.Unstable
            : c.Cct is not null && c.Disposition is not (DynamicsDisposition.Failed or DynamicsDisposition.NotRun or DynamicsDisposition.NotAttempted);
        if (!ran)
            throw X(st.Pos, $"{word}: the study did not run ({result.Status}, row {c.Disposition}{(c.Reason is null ? "" : ": " + c.Reason)}"
                + (result.Reasons.Count > 0 ? "; " + string.Join("; ", result.Reasons) : "") + "); nothing is kept");
        string busName = st.Bus!.Value.Value;
        if (st.Kind == StudyKind.Cct)
            return new StudyRun(seq, st.Pos.Line, st.Name.Value, st.Kind, plantVersion, BusName: busName, BusId: busId)
            {
                Disposition = c.Disposition, CctStableSeconds = c.Cct!.Coarse.StableSeconds, CctUnstableSeconds = c.Cct.Coarse.UnstableSeconds,
            };
        IReadOnlyList<DynamicsChannelTrace> traces = c.Run!.Channels;
        double fEnd = traces[0].Values[^1];
        var mins = ImmutableArray.CreateBuilder<KeyValuePair<string, double>>();
        int busIndex = 0;
        foreach (DynamicsChannelTrace t in traces)
        {
            if (t.Kind != "busVoltage" || t.Unit != "pu") continue;
            double least = double.PositiveInfinity;
            foreach (double v in t.Values) least = Math.Min(least, v);
            mins.Add(new KeyValuePair<string, double>(ci.BusNames[busIndex++], least));
        }
        return new StudyRun(seq, st.Pos.Line, st.Name.Value, st.Kind, plantVersion, BusName: busName, BusId: busId)
        {
            Disposition = c.Disposition, FinalFrequencyHz = fEnd, MinVoltagePu = mins.ToImmutable(),
        };
    }

    // The quantity in seconds (S or MS; the parser takes no other unit here).
    private static double Seconds(Quantity q) => q.Unit == Unit.Ms ? (double)q.Value / 1000.0 : (double)q.Value;

    // The bytes as the dynamics-study verb decodes its RAW and DYR files: UTF-8, a byte order mark honoured.
    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The reading <paramref name="e"/> of <paramref name="run"/> as a double (the value the run holds).</summary>
    public static double Read(StudyExpr e, StudyRun run, string? elementName)
    {
        bool fits = (e.Reading, run.Kind) switch
        {
            (StudyReading.Violations or StudyReading.WorstLoading, StudyKind.Contingency) => true,
            (StudyReading.Ik, StudyKind.ShortCircuit) => true,
            (StudyReading.Objective or StudyReading.P, StudyKind.Opf) => true,
            (StudyReading.Objective or StudyReading.Secured, StudyKind.Scopf) => true,
            (StudyReading.Stable or StudyReading.FinalFrequency or StudyReading.MinVoltage, StudyKind.Rms) => true,
            (StudyReading.Cct, StudyKind.Cct) => true,
            _ => false,
        };
        if (!fits)
            throw X(e.Pos, $"{e.Canonical()}: study '{run.Name}' is a {Word(run.Kind)} study, which has no such reading");
        switch (e.Reading)
        {
            case StudyReading.Violations: return run.Violations;
            case StudyReading.WorstLoading: return run.WorstLoadingPu;
            case StudyReading.Objective: return run.Objective;
            case StudyReading.Secured: return run.Secured;
            case StudyReading.Stable: return run.Disposition == DynamicsDisposition.Stable ? 1 : 0;
            case StudyReading.FinalFrequency: return run.FinalFrequencyHz;
            case StudyReading.Cct:
                if (run.Disposition != DynamicsDisposition.CctBracketed)
                    throw X(e.Pos, $"study '{run.Name}' found no critical clearing time bracket ({run.Disposition}), so it has no CCT to read");
                return run.CctStableSeconds;
            case StudyReading.MinVoltage:
                foreach (KeyValuePair<string, double> kv in run.MinVoltagePu)
                    if (string.Equals(kv.Key, elementName, StringComparison.Ordinal)) return kv.Value;
                throw X(e.Pos, $"bus \"{elementName}\" has no voltage in study '{run.Name}'");
            case StudyReading.Ik:
                if (!string.Equals(elementName, run.BusName, StringComparison.Ordinal))
                    throw X(e.Pos, $"study '{run.Name}' is the fault at bus \"{run.BusName}\", not at bus \"{elementName}\"");
                return run.IkKa;
            default:
                foreach (KeyValuePair<string, double> kv in run.GeneratorPMw)
                    if (string.Equals(kv.Key, elementName, StringComparison.Ordinal)) return kv.Value;
                throw X(e.Pos, $"generator \"{elementName}\" was not in service in study '{run.Name}'");
        }
    }

    /// <summary>The text of REPORT STUDY (numbers in round-trip form).</summary>
    public static string Report(StudyRun r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("study ").Append(r.Name).Append(": ").Append(Word(r.Kind)).Append('\n');
        switch (r.Kind)
        {
            case StudyKind.Contingency:
                sb.Append("  outages screened ").Append(r.Screened.ToString(CultureInfo.InvariantCulture)).Append(", failing ")
                  .Append(r.Violations.ToString(CultureInfo.InvariantCulture)).Append(", worst loading ").Append(R(r.WorstLoadingPu)).Append(" PU\n");
                break;
            case StudyKind.Scopf:
                sb.Append("  ").Append(r.Status).Append(": objective ").Append(R(r.Objective)).Append(" per hour, secured ")
                  .Append(r.Secured.ToString(CultureInfo.InvariantCulture)).Append(" outage(s)\n");
                break;
            case StudyKind.ShortCircuit:
                sb.Append("  bus \"").Append(r.BusName).Append("\" (id ").Append(r.BusId.ToString(CultureInfo.InvariantCulture)).Append("): Ik'' ")
                  .Append(R(r.IkKa)).Append(" KA\n");
                break;
            case StudyKind.Rms:
                sb.Append("  ").Append(r.Disposition).Append(": fault at bus \"").Append(r.BusName).Append("\", final frequency ")
                  .Append(R(r.FinalFrequencyHz)).Append(" HZ\n");
                foreach (KeyValuePair<string, double> kv in r.MinVoltagePu)
                    sb.Append("  bus \"").Append(kv.Key).Append("\" least voltage ").Append(R(kv.Value)).Append(" PU\n");
                break;
            case StudyKind.Cct:
                sb.Append("  ").Append(r.Disposition).Append(": fault at bus \"").Append(r.BusName).Append("\", stable to ")
                  .Append(R(r.CctStableSeconds)).Append(" S").Append(r.CctUnstableSeconds is { } u ? ", unstable from " + R(u) + " S" : "").Append('\n');
                break;
            default:
                sb.Append("  objective ").Append(R(r.Objective)).Append(" in ").Append(r.Iterations.ToString(CultureInfo.InvariantCulture)).Append(" iteration(s)\n");
                foreach (KeyValuePair<string, double> kv in r.GeneratorPMw)
                    sb.Append("  generator \"").Append(kv.Key).Append("\" P ").Append(R(kv.Value)).Append(" MW\n");
                break;
        }
        return sb.ToString();
    }

    /// <summary>Write the run record's <c>studies</c> array (only for a STUDIES script).</summary>
    public static void WriteRecord(System.Text.Json.Utf8JsonWriter w, IReadOnlyList<StudyRun> runs)
    {
        w.WriteStartArray("studies");
        foreach (StudyRun r in runs)
        {
            w.WriteStartObject();
            w.WriteNumber("seq", r.Seq);
            w.WriteNumber("line", r.Line);
            w.WriteString("name", r.Name);
            w.WriteString("study", Word(r.Kind));
            switch (r.Kind)
            {
                case StudyKind.Contingency:
                    w.WriteNumber("screened", r.Screened);
                    w.WriteNumber("violations", r.Violations);
                    w.WriteString("worstLoadingPu", R(r.WorstLoadingPu));
                    break;
                case StudyKind.Scopf:
                    w.WriteString("status", r.Status);
                    w.WriteString("objective", R(r.Objective));
                    w.WriteNumber("secured", r.Secured);
                    break;
                case StudyKind.ShortCircuit:
                    w.WriteString("bus", r.BusName);
                    w.WriteNumber("busId", r.BusId);
                    w.WriteString("ikKa", R(r.IkKa));
                    break;
                case StudyKind.Rms:
                    w.WriteString("bus", r.BusName);
                    w.WriteString("disposition", r.Disposition);
                    w.WriteString("finalFrequencyHz", R(r.FinalFrequencyHz));
                    w.WriteStartArray("minVoltagePu");
                    foreach (KeyValuePair<string, double> kv in r.MinVoltagePu)
                    {
                        w.WriteStartObject();
                        w.WriteString("bus", kv.Key);
                        w.WriteString("pu", R(kv.Value));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    break;
                case StudyKind.Cct:
                    w.WriteString("bus", r.BusName);
                    w.WriteString("disposition", r.Disposition);
                    w.WriteString("stableSeconds", R(r.CctStableSeconds));
                    if (r.CctUnstableSeconds is { } u) w.WriteString("unstableSeconds", R(u));
                    else w.WriteNull("unstableSeconds");
                    break;
                default:
                    w.WriteString("objective", R(r.Objective));
                    w.WriteNumber("iterations", r.Iterations);
                    w.WriteStartArray("generators");
                    foreach (KeyValuePair<string, double> kv in r.GeneratorPMw)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", kv.Key);
                        w.WriteString("pMw", R(kv.Value));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    break;
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
