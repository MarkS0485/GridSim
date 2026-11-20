using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.IO.Cim.Study;

/// <summary>RC3 P3: open or close one switch of the bundle before the buses are built (topology 'switches' only).</summary>
/// <param name="Mrid">The switch's mRID (any spelling that reduces to it: <c>_x</c>, <c>#_x</c>, <c>urn:uuid:x</c>).</param>
/// <param name="Open">True to open it, false to close it.</param>
public sealed record CgmesSwitchAction(string Mrid, bool Open);

/// <summary>RC3 P3: the optional P1 preventive branch N-1 verification over the imported model.</summary>
/// <param name="OutageMrids">The outaged branches, by source mRID (an ACLineSegment, a series compensator or a
/// two-winding PowerTransformer), in study order.</param>
/// <param name="PostFaultRating">A, B or C, as P1 (the CGMES import reads only the permanent limit, so B and C fall back
/// to A).</param>
/// <param name="MaxRounds">The DC candidate's round cap (P1).</param>
public sealed record CgmesSecurityRequest(IReadOnlyList<string> OutageMrids, PostFaultRating PostFaultRating, int MaxRounds = 50);

/// <summary>RC3 P3: a CGMES study job.</summary>
/// <param name="Profiles">The profile bundle, in manifest order.</param>
/// <param name="Import">The importer options (the incumbent <see cref="CimImportOptions"/>).</param>
/// <param name="MinPu">The voltage band's lower bound applied to every solved bus.</param>
/// <param name="MaxPu">The voltage band's upper bound.</param>
/// <param name="BaseMva">The system base (CGMES carries none; the bridge's default rule is 100 MVA).</param>
/// <param name="FrequencyHz">The nominal frequency, or null for the bridge's default (50 Hz).</param>
/// <param name="ApplyControls">True: solve with <see cref="CimControlledPowerFlow"/> (remote voltage control, SVCs,
/// regulating shunts). False: one plain engine solve with those devices held at their SSH values (recorded).</param>
/// <param name="Controls">The control loop's options (null = the incumbent defaults).</param>
/// <param name="SwitchActions">Switch actions applied before the buses are built (topology 'switches' only).</param>
/// <param name="Security">The optional P1 verification.</param>
/// <param name="Name">The model name.</param>
public sealed record CgmesStudyRequest(
    IReadOnlyList<CgmesProfileInput> Profiles, CimImportOptions Import, double MinPu, double MaxPu,
    double BaseMva = 100.0, double? FrequencyHz = null, bool ApplyControls = true, CimControlOptions? Controls = null,
    IReadOnlyList<CgmesSwitchAction>? SwitchActions = null, CgmesSecurityRequest? Security = null, string Name = "cgmes-study");

/// <summary>One step of the study, in execution order.</summary>
public sealed record CgmesStageRecord(int Order, string Stage, string Outcome, string Detail);

/// <summary>What the import and the bridge produced.</summary>
public sealed record CgmesImportReport(
    CimImportOptions Options, double BaseMva, string BaseMvaSource, double FrequencyHz, string FrequencySource,
    int Buses, int Lines, int Transformers, int Loads, int Generators, int Shunts, int StaticVarCompensators,
    int ClosedRetainedSwitches, int BusesWithStartingVoltage, int BranchesWithRating, IReadOnlyList<string> DroppedNodes,
    IReadOnlyList<string> ImporterNotes, IReadOnlyList<string> BridgeNotes);

/// <summary>A solved bus and the source nodes it stands for.</summary>
public sealed record CgmesBusIdentity(int BusId, string? Name, double BaseKv, BusType Type, double StartVm, double StartVaDeg,
    IReadOnlyList<string> CimNodes);

/// <summary>A model branch and the source equipment it stands for.</summary>
/// <param name="Winding">The winding number for a three-winding transformer's branch (to its star point), else null.</param>
/// <param name="RatingSource">Where <paramref name="RateAMva"/> comes from.</param>
public sealed record CgmesBranchIdentity(int Index, int From, int To, string CimMrid, string CimClass, int? Winding,
    string? Name, double RateAMva, string RatingSource);

/// <summary>A model generator and the source injection it stands for.</summary>
public sealed record CgmesGeneratorIdentity(int Index, int Bus, string CimMrid, string CimClass, string? Name, bool VoltageControl);

/// <summary>The connected electrical model's identity maps.</summary>
public sealed record CgmesModelIdentity(
    IReadOnlyList<CgmesBusIdentity> Buses, IReadOnlyList<CgmesBranchIdentity> Branches,
    IReadOnlyList<CgmesGeneratorIdentity> Generators, string PostFaultRatingOrigin, int LiveBranches, int RatedBranches);

/// <summary>A switch action as applied.</summary>
public sealed record CgmesSwitchActionRecord(string Mrid, string Class, bool? AsFoundOpen, bool RequestedOpen);

/// <summary>One equipment terminal whose bus the switch actions changed.</summary>
public sealed record CgmesTerminalMove(string Equipment, string Terminal, string? BeforeBus, string? AfterBus);

/// <summary>The switching stage: old and new electrical groups.</summary>
public sealed record CgmesSwitchingReport(
    IReadOnlyList<CgmesSwitchActionRecord> Actions, string OverlaySha256, int BusesBefore, int BusesAfter,
    IReadOnlyList<CgmesTerminalMove> Moves);

/// <summary>One control the study represented, and where it ended.</summary>
public sealed record CgmesRepresentedControl(string Kind, string Device, int BusId, int RegulatedBusId, double? TargetPu,
    double? Realised, string RealisedUnits, bool? AtLimit, string Treatment);

/// <summary>The control stage.</summary>
public sealed record CgmesControlReport(
    bool Applied, string Solver, bool Settled, int EngineSolves, string? Note, IReadOnlyList<string> ShuntChoices,
    IReadOnlyList<CimControlState> States, IReadOnlyList<CgmesRepresentedControl> Represented,
    IReadOnlyList<CgmesUnrepresentedControl> Unrepresented);

/// <summary>One compared quantity between two solved states (or two models).</summary>
/// <param name="Count">How many values were compared.</param>
/// <param name="Differing">How many differ at all (bitwise).</param>
/// <param name="MaxAbsDifference">The largest absolute difference, in <paramref name="Units"/>.</param>
/// <param name="Tolerance">The declared tolerance (0 for report-only data quantities).</param>
/// <param name="Worst">The element of the largest difference.</param>
public sealed record CgmesQuantityDifference(string Quantity, string Units, int Count, int Differing, double MaxAbsDifference,
    double Tolerance, string? Worst, double? Expected, double? Actual)
{
    /// <summary>True when every compared value is within the declared tolerance.</summary>
    public bool Within => MaxAbsDifference <= Tolerance;
}

/// <summary>The exchange map of one model element: source identity, model position, exported identity, read-back position.</summary>
public sealed record CgmesExchangeIdentity(string Kind, string SourceMrid, int ModelIndex, string ExportMrid, int ReadBackIndex);

/// <summary>The export and read-back stage.</summary>
public sealed record CgmesExchangeReport(
    string Evaluated, string? Reason, string Form, string? DocumentSha256, IReadOnlyList<string> WriterNotes,
    IReadOnlyList<CgmesExchangeIdentity> Identity,
    bool? IdentityEquivalent, IReadOnlyList<string> IdentityDifferences,
    bool? TopologyEquivalent, IReadOnlyList<string> TopologyDifferences,
    bool? RatingsEquivalent, IReadOnlyList<string> RatingDifferences,
    IReadOnlyList<CgmesQuantityDifference> ModelData,
    bool? ControlledOutputEquivalent, IReadOnlyList<CgmesQuantityDifference> FoldCheck,
    IReadOnlyList<CgmesQuantityDifference> ReadBackCheck, PowerFlowStatus? ReadBackStatus, double? ReadBackResidualPu,
    string? ReadBackDisposition, IReadOnlyList<string> VerifiedFields, IReadOnlyList<string> NotVerified);

/// <summary>RC3 P3: the CGMES study result.</summary>
/// <param name="Status">SECURE only when every required state (the intact controlled state, and each listed outage
/// when a security stage was asked for) is evaluated within its limits and the exchange read-back is equivalent;
/// otherwise NOT SECURE.</param>
/// <param name="Outcome">Verified, Violations or Incomplete (missing evidence takes precedence over violations).</param>
public sealed record CgmesStudyResult(
    string Status, string Outcome, string Scope, IReadOnlyList<CgmesStageRecord> Stages, CgmesBundleReport Bundle,
    CgmesImportReport Import, CgmesModelIdentity Identity, CgmesSwitchingReport? Switching, CgmesControlReport Controls,
    SecurityState Intact, TransmissionSecurityResult? Security, CgmesExchangeReport Exchange, IReadOnlyList<string> Deferred);

/// <summary>A run: the result and the exchange document it wrote (null when the export stage was not reached).</summary>
public sealed record CgmesStudyRun(CgmesStudyResult Result, string? ExportDocument);

/// <summary>
/// RC3 P3: CGMES as a normal study job. One profile bundle runs the full sequence, each stage over the incumbent seam
/// that owns it and kept separate:
/// <list type="number">
/// <item>bundle inspection (<see cref="CgmesBundleInspector"/>): digests, declared types, header identity and
/// dependencies, conflicting or dangling records;</item>
/// <item>import (<see cref="CimImporter.Parse(IEnumerable{string}, CimImportOptions)"/>), with switch actions applied as
/// an SSH-style overlay before the topology processor runs;</item>
/// <item>bridge and topology mapping (<see cref="CimGridModelBridge.Bridge"/>): the connected electrical model and its
/// identity maps;</item>
/// <item>control application and AC solve (<see cref="CimControlledPowerFlow"/> around the Newton engine, with the P1
/// study options: enforced reactive limits);</item>
/// <item>absolute intact limits (voltage band, permanent rating at both branch ends, unit P and Q);</item>
/// <item>optional P1 preventive N-1 verification (<see cref="TransmissionSecurityRunner"/>) over the realised model;</item>
/// <item>export (<see cref="CgmesWriter.Write"/>) of the realised model and read-back
/// (<see cref="CgmesWriter.ReadBack"/>, compared against <see cref="CgmesWriter.InFileOrder"/>): identity, topology,
/// ratings and controlled AC output.</item>
/// </list>
/// A later stage never bypasses a failed earlier one: a refused input stops before import; an unsettled control solve
/// leaves the security and exchange stages unevaluated (and the study incomplete). Nothing is solved in parallel.
/// </summary>
public static class CgmesStudyRunner
{
    /// <summary>Declared comparison tolerances for the controlled AC output.</summary>
    public const double VoltageTolerancePu = 1e-6, AngleToleranceDeg = 1e-5, PowerToleranceMw = 1e-4;

    /// <summary>The fixed deferral list (what this study does not evidence).</summary>
    public static readonly IReadOnlyList<string> Deferred =
    [
        "Conformity to an external CGMES profile specification (ENTSO-E CGMES 2.4.15 or 3.0 conformity assessment) is not claimed: no conformity evidence was produced. The header check compares a profile URI with the declared type only.",
        "The exchange output is one RDF/XML document holding EQ, TP, SSH and SV content (CgmesWriter); a split multi-profile bundle export is not produced.",
        "Source mRIDs are not carried into the exported document (CgmesWriter names equipment by model position); the identity map is explicit in exchange.identity.",
        "Temporary (TATL) apparent-power limits and current limits are not imported; a post-fault B or C rating falls back to the permanent (PATL) rating A.",
        "Automatic tap-changer control is not applied: taps are held at the step the import options select.",
        "Post-outage CIM controls are not re-applied in the security stage: it runs on the realised model with controls frozen at the intact controlled state.",
        "A licensed-comparator acceptance with identical controls and rating definitions was not run on this machine: unverified.",
        "Zipped bundles are not supported; each profile document is read from its own file.",
    ];

    /// <summary>Run the study. Input errors (a refused bundle, an invalid request) throw
    /// <see cref="CgmesStudyInputException"/>, <see cref="InvalidDataException"/> or <see cref="ArgumentException"/>
    /// before any solve; a verdict is returned.</summary>
    /// <param name="request">The study.</param>
    /// <param name="admit">Called with the bridged model before any solve (the CLI's licence gate); throw to refuse.</param>
    public static CgmesStudyRun Run(CgmesStudyRequest request, Action<GridModel>? admit = null)
    {
        Validate(request);
        var stages = new List<CgmesStageRecord>();
        void Stage(string name, string outcome, string detail) => stages.Add(new(stages.Count + 1, name, outcome, detail));
        CimImportOptions options = request.Import;
        bool fromSwitches = options.Topology == CimTopology.Switches;
        bool needsSv = options.StartingVoltages || options.TapPositions == CimTapPositions.StateVariables
            || options.ShuntSections == CimTapPositions.StateVariables;

        // ---- 1. bundle ----------------------------------------------------------------------------------------------
        CgmesBundleReport bundle = CgmesBundleInspector.InspectParsed(request.Profiles, fromSwitches, needsSv,
            out IReadOnlyList<CgmesBundleInspector.Parsed> parsed);
        Stage("bundle", "ACCEPTED", $"{bundle.Profiles.Count} profile(s), bundle {bundle.BundleSha256}.");
        List<string> imported = parsed.Where(p => CgmesBundleInspector.ImportedTypes.Contains(p.Input.Profile)).Select(p => p.Text).ToList();

        // ---- 2. import (and switch actions) -------------------------------------------------------------------------
        CgmesSwitchingReport? switching = null;
        CimNetwork net;
        IReadOnlyList<CgmesSwitchAction> actions = request.SwitchActions ?? [];
        if (actions.Count == 0)
        {
            net = CimImporter.Parse(imported, options);
            Stage("import", "IMPORTED", $"{net.Buses.Count} bus(es), {net.Branches.Count} line(s), {net.Transformers.Count} transformer(s).");
        }
        else
        {
            (string overlay, List<CgmesSwitchActionRecord> records) = Overlay(parsed, actions);
            CimNetwork before = CimImporter.Parse(imported, options);
            net = CimImporter.Parse(new[] { overlay }.Concat(imported), options);
            switching = new CgmesSwitchingReport(records,
                CgmesBundleInspector.Hash(System.Text.Encoding.UTF8.GetBytes(overlay)), before.Buses.Count, net.Buses.Count,
                Moves(before, net));
            Stage("import", "IMPORTED",
                $"{records.Count} switch action(s) applied before the topology processor: {before.Buses.Count} bus(es) as found, " +
                $"{net.Buses.Count} after; {switching.Moves.Count} equipment terminal(s) changed bus.");
        }

        // ---- 3. bridge and topology mapping -------------------------------------------------------------------------
        var bridgeNotes = new List<string>();
        CimBridgeResult bridge = CimGridModelBridge.Bridge(net, request.BaseMva, request.Name, notes: bridgeNotes, frequencyHz: request.FrequencyHz);
        GridModel model = bridge.Model;
        admit?.Invoke(model);
        CgmesModelIdentity identity = IdentityOf(bridge);
        // Resolve the security outages now, so a mistyped mRID is refused before any solve.
        BranchOutage[]? resolvedOutages = request.Security?.OutageMrids.Select(m => OutageOf(identity, m)).ToArray();
        var import = new CgmesImportReport(options, model.BaseMva,
            request.BaseMva == 100.0 ? "100 MVA (the bridge's default rule unless the study states another; CGMES carries no system base)" : "study file",
            model.FrequencyHz, request.FrequencyHz is null ? "50 Hz (the bridge's default rule; the profiles carry no system frequency)" : "study file",
            net.Buses.Count, net.Branches.Count, net.Transformers.Count, net.Loads.Count, net.Generators.Count, net.Shunts.Count,
            net.Svcs.Count, net.Couplers.Count, net.Buses.Count(b => b.SvVKv is not null),
            net.Branches.Count(b => b.RateAMva is not null) + net.Transformers.Count(t => t.RateAMva is not null),
            bridge.DroppedNodes, net.Notes, bridgeNotes);
        Stage("bridge", "BRIDGED",
            $"{model.Buses.Count} bus(es), {model.Branches.Count} branch(es), {model.Generators.Count} generator(s); " +
            $"{identity.RatedBranches} of {identity.LiveBranches} live branch(es) rated.");

        // ---- 4. controls and AC solve -------------------------------------------------------------------------------
        PowerFlowOptions pfOptions = ContingencyAnalysis.StudyOptions;
        PowerFlowResult solved;
        CimControlledPowerFlow? controlled = null;
        bool settled;
        string solverLabel;
        if (request.ApplyControls)
        {
            controlled = new CimControlledPowerFlow(SolverSelector.CreateNewton(model), bridge, request.Controls);
            solved = controlled.Solve(model, pfOptions);
            settled = controlled.ControlsSettled && solved.Converged;
            solverLabel = "CimControlledPowerFlow around " + SolverSelector.CreateNewton(model).GetType().Name +
                          " (ContingencyAnalysis.StudyOptions: enforced Q limits)";
        }
        else
        {
            IPowerFlowSolver engine = SolverSelector.CreateNewton(model);
            solved = engine.Solve(model, pfOptions);
            settled = solved.Converged;
            solverLabel = engine.GetType().Name + " (plain; CIM controls held at their SSH values by request)";
        }
        (GridModel? realised, List<CgmesRepresentedControl> represented) = Realise(bridge, controlled, solved, settled, request.ApplyControls);
        var controls = new CgmesControlReport(request.ApplyControls, solverLabel, settled, controlled?.LastSolves ?? 1,
            controlled?.LastNote ?? (solved.Converged ? null : "the engine did not converge"),
            controlled?.ShuntChoices ?? [], controlled?.LastStates ?? [], represented, bundle.UnrepresentedControls);
        Stage("solve", settled ? "SETTLED" : "NO SOLUTION",
            settled ? $"{controls.EngineSolves} engine solve(s); {represented.Count} control(s) represented."
                    : controls.Note ?? "no settled controlled solution");

        // ---- 5. intact absolute limits -------------------------------------------------------------------------------
        SecurityState intact = settled && realised is not null
            ? Evaluate(realised, solved, request.MinPu, request.MaxPu)
            : new SecurityState(null, "NO SOLUTION", controls.Note ?? "No settled controlled solution.", solved.Status,
                double.IsFinite(solved.MaxMismatch) ? solved.MaxMismatch : null, null, [], solved.SolvedBusTypes);
        Stage("intact", intact.Disposition, intact.Reason ?? $"{intact.Limits.Count} limit(s) evaluated.");

        // ---- 6. optional P1 verification -----------------------------------------------------------------------------
        TransmissionSecurityResult? security = null;
        if (request.Security is not null)
        {
            if (realised is null)
            {
                Stage("security", "UNEVALUATED", "No settled controlled intact state to verify from.");
            }
            else
            {
                BranchOutage[] outages = resolvedOutages!;
                security = TransmissionSecurityRunner.Run(realised,
                    new TransmissionSecurityRequest(outages, request.MinPu, request.MaxPu, request.Security.PostFaultRating,
                        request.Security.MaxRounds));
                Stage("security", security.Status, $"{security.Outcome}; {security.Outages.Count} outage row(s).");
            }
        }

        // ---- 7. export and read-back -------------------------------------------------------------------------------
        string? document = null;
        CgmesExchangeReport exchange;
        if (realised is null)
        {
            exchange = Unevaluated("No settled controlled solution: nothing verified to exchange.");
            Stage("exchange", "UNEVALUATED", exchange.Reason!);
        }
        else
        {
            (exchange, document) = Exchange(realised, solved, identity, request);
            Stage("exchange", exchange.Evaluated == "EVALUATED"
                    ? (exchange.ControlledOutputEquivalent == true && exchange.IdentityEquivalent == true && exchange.TopologyEquivalent == true
                       && exchange.RatingsEquivalent == true ? "EQUIVALENT" : "DIFFERENT")
                    : "UNEVALUATED",
                exchange.Reason ?? $"read-back {exchange.ReadBackStatus}; document {exchange.DocumentSha256}.");
        }

        // ---- 8. verdict -------------------------------------------------------------------------------------------
        bool exchangeOk = exchange.Evaluated == "EVALUATED" && exchange.IdentityEquivalent == true && exchange.TopologyEquivalent == true
            && exchange.RatingsEquivalent == true && exchange.ControlledOutputEquivalent == true;
        bool incomplete = intact.Disposition is "NO SOLUTION" or "INCOMPLETE" or "ISLANDED" or "UNEVALUATED"
            || !exchangeOk
            || (request.Security is not null && (security is null || security.Outcome == "Incomplete"));
        bool violated = intact.Limits.Any(l => l.Violated) || (security is not null && security.Status != "SECURE");
        string outcome = incomplete ? "Incomplete" : violated ? "Violations" : "Verified";
        string status = outcome == "Verified" ? "SECURE" : "NOT SECURE";
        Stage("verdict", status, outcome);
        string scope = request.Security is null
            ? "Intact (N-0) controlled state and its exchange read-back."
            : $"Intact controlled state, its exchange read-back and {request.Security.OutageMrids.Count} preventive branch outage(s) (P1).";
        var result = new CgmesStudyResult(status, outcome, scope, stages, bundle, import, identity, switching, controls, intact,
            security, exchange, Deferred);
        return new CgmesStudyRun(result, document);
    }

    private static void Validate(CgmesStudyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profiles);
        ArgumentNullException.ThrowIfNull(request.Import);
        if (!double.IsFinite(request.MinPu) || !double.IsFinite(request.MaxPu) || request.MinPu <= 0 || request.MaxPu < request.MinPu)
            throw new ArgumentException("Voltage bounds must be finite, positive and ordered.");
        if (!double.IsFinite(request.BaseMva) || request.BaseMva <= 0)
            throw new ArgumentException("The system base must be a finite number of MVA above 0.");
        if (request.FrequencyHz is double f && !(double.IsFinite(f) && f > 0))
            throw new ArgumentException("The nominal frequency must be a finite number of hertz above 0.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("The model name is empty.");
        IReadOnlyList<CgmesSwitchAction> actions = request.SwitchActions ?? [];
        if (actions.Count > 0 && request.Import.Topology != CimTopology.Switches)
            throw new ArgumentException(
                "Switch actions need topology 'switches': in the bus-branch view the TopologicalNodes decide the buses and a " +
                "switch state is only checked against them, so an action would not change the solved network.");
        if (actions.Any(a => a is null || string.IsNullOrWhiteSpace(a.Mrid)))
            throw new ArgumentException("Every switch action names a switch mRID.");
        if (actions.Select(a => CgmesBundleInspector.Mrid(a.Mrid)).Distinct(StringComparer.Ordinal).Count() != actions.Count)
            throw new ArgumentException("A switch is named by more than one action.");
        if (request.Security is CgmesSecurityRequest s)
        {
            if (s.OutageMrids is null || s.OutageMrids.Count == 0 || s.OutageMrids.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("The security stage needs at least one outage mRID.");
            if (s.MaxRounds < 1 || !Enum.IsDefined(s.PostFaultRating))
                throw new ArgumentException("Invalid DC round cap or post-fault rating.");
        }
    }

    // ---- switch actions -------------------------------------------------------------------------------------------

    private static (string Overlay, List<CgmesSwitchActionRecord> Records) Overlay(
        IReadOnlyList<CgmesBundleInspector.Parsed> parsed, IReadOnlyList<CgmesSwitchAction> actions)
    {
        const string rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var sb = new System.Text.StringBuilder();
        sb.Append("<rdf:RDF xmlns:rdf=\"").Append(rdf).Append("\">");
        var records = new List<CgmesSwitchActionRecord>();
        List<System.Xml.Linq.XElement> objects = parsed
            .Where(p => CgmesBundleInspector.ImportedTypes.Contains(p.Input.Profile))
            .SelectMany(p => p.Xml.Root!.Elements()).ToList();
        foreach (CgmesSwitchAction action in actions)
        {
            string mrid = CgmesBundleInspector.Mrid(action.Mrid);
            List<System.Xml.Linq.XElement> descriptions = objects
                .Where(e => CgmesBundleInspector.IdOf(e) is string id && CgmesBundleInspector.Mrid(id) == mrid).ToList();
            System.Xml.Linq.XElement? typed = descriptions.FirstOrDefault(e => e.Name.LocalName != "Description");
            if (typed is null || !CgmesBundleInspector.SwitchClasses.Contains(typed.Name.LocalName))
                throw new CgmesStudyInputException("switchActions", action.Mrid,
                    typed is null ? "no profile of the bundle defines this object." : $"the object is a {typed.Name.LocalName}, not a switch.");
            System.Xml.Linq.XAttribute? idAttr = typed.Attribute(System.Xml.Linq.XName.Get("ID", rdf));
            string reference = idAttr is not null ? "#" + idAttr.Value : CgmesBundleInspector.IdOf(typed)!;
            bool? asFound = null;
            foreach (string prop in new[] { "Switch.open", "Switch.normalOpen" })
            {
                string? v = descriptions.SelectMany(e => e.Elements()).FirstOrDefault(e => e.Name.LocalName == prop)?.Value.Trim();
                if (CgmesBundleInspector.ParseBoolean(v) is bool open) { asFound = open; break; }
            }
            string ns = typed.Name.NamespaceName;
            sb.Append("<rdf:Description rdf:about=\"").Append(System.Security.SecurityElement.Escape(reference)).Append("\">")
              .Append("<cim:Switch.open xmlns:cim=\"").Append(System.Security.SecurityElement.Escape(ns)).Append("\">")
              .Append(action.Open ? "true" : "false").Append("</cim:Switch.open></rdf:Description>");
            records.Add(new CgmesSwitchActionRecord(mrid, typed.Name.LocalName, asFound, action.Open));
        }
        sb.Append("</rdf:RDF>");
        return (sb.ToString(), records);
    }

    private static List<CgmesTerminalMove> Moves(CimNetwork before, CimNetwork after)
    {
        static Dictionary<string, string?> Ends(CimNetwork n)
        {
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (CimBranch b in n.Branches) { map[b.MRID + "|from"] = b.FromNode; map[b.MRID + "|to"] = b.ToNode; }
            foreach (CimTransformer t in n.Transformers)
                foreach (CimTransformerEnd e in t.Ends) map[$"{t.MRID}|end{e.EndNumber}"] = e.Node;
            foreach (CimLoad l in n.Loads) map[l.MRID + "|node"] = l.Node;
            foreach (CimGenerator g in n.Generators) map[g.MRID + "|node"] = g.Node;
            foreach (CimShunt s in n.Shunts) map[s.MRID + "|node"] = s.Node;
            foreach (CimStaticVarCompensator s in n.Svcs) map[s.MRID + "|node"] = s.Node;
            return map;
        }
        Dictionary<string, string?> a = Ends(before), b = Ends(after);
        var moves = new List<CgmesTerminalMove>();
        foreach (string key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            string? x = a.GetValueOrDefault(key), y = b.GetValueOrDefault(key);
            if (string.Equals(x, y, StringComparison.Ordinal)) continue;
            int bar = key.IndexOf('|');
            moves.Add(new CgmesTerminalMove(key[..bar], key[(bar + 1)..], x, y));
        }
        return moves;
    }

    // ---- identity --------------------------------------------------------------------------------------------------

    private static CgmesModelIdentity IdentityOf(CimBridgeResult bridge)
    {
        GridModel model = bridge.Model;
        CimNetwork net = bridge.Network;
        var nodesOf = new Dictionary<int, List<string>>();
        foreach ((string node, int id) in bridge.BusIdOfNode.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!nodesOf.TryGetValue(id, out List<string>? list)) nodesOf[id] = list = new List<string>();
            list.Add(node);
        }
        var buses = model.Buses.Select(b => new CgmesBusIdentity(b.Id, b.Name, b.BaseKv, b.Type, b.Vm, b.VaDeg,
            nodesOf.TryGetValue(b.Id, out List<string>? n) ? n : [])).ToList();

        // Branches: the bridge adds lines in network order (skipping a line whose two ends a closed switch joins), then
        // transformers (one branch for two windings, one per winding for three). The map is rebuilt from that rule and
        // checked against the bridge's own end-node record, so a change in the bridge fails here rather than mislabel.
        var rows = new List<(string Mrid, string Class, int? Winding, string? Rating)>();
        foreach (CimBranch br in net.Branches)
        {
            if (bridge.BusIdOfNode[br.FromNode] == bridge.BusIdOfNode[br.ToNode] && !string.Equals(br.FromNode, br.ToNode, StringComparison.Ordinal))
                continue;
            rows.Add((br.MRID, br.Class, null, br.RateAMva is null ? null : "PATL ApparentPowerLimit (rate A)"));
        }
        foreach (CimTransformer tx in net.Transformers)
        {
            if (tx.Ends.Count == 2) rows.Add((tx.MRID, "PowerTransformer", null, tx.RateAMva is null ? null : "PATL ApparentPowerLimit (rate A)"));
            else foreach (CimTransformerEnd e in tx.Ends) rows.Add((tx.MRID, "PowerTransformer", e.EndNumber, null));
        }
        if (rows.Count != model.Branches.Count || rows.Count != bridge.BranchNodes.Count)
            throw new InvalidOperationException(
                $"The CGMES identity map has {rows.Count} branch(es) but the bridged model {model.Branches.Count}; the source identity of each branch cannot be established.");
        var branches = new List<CgmesBranchIdentity>(rows.Count);
        int k = 0;
        foreach (CimBranch br in net.Branches)
        {
            if (bridge.BusIdOfNode[br.FromNode] == bridge.BusIdOfNode[br.ToNode] && !string.Equals(br.FromNode, br.ToNode, StringComparison.Ordinal))
                continue;
            Check(k, br.FromNode, br.ToNode);
            k++;
        }
        foreach (CimTransformer tx in net.Transformers)
        {
            if (tx.Ends.Count == 2) { Check(k, tx.Ends[0].Node, tx.Ends[1].Node); k++; }
            else foreach (CimTransformerEnd e in tx.Ends) { Check(k, e.Node, null); k++; }
        }
        void Check(int index, string from, string? to)
        {
            (string f, string? t) = bridge.BranchNodes[index];
            if (!string.Equals(f, from, StringComparison.Ordinal) || !string.Equals(t, to, StringComparison.Ordinal))
                throw new InvalidOperationException($"The CGMES identity map disagrees with the bridge at branch {index}.");
        }
        for (int i = 0; i < rows.Count; i++)
        {
            Branch b = model.Branches[i];
            branches.Add(new CgmesBranchIdentity(i, b.From, b.To, rows[i].Mrid, rows[i].Class, rows[i].Winding, b.Name, b.RateAMva,
                b.RateAMva > 0 ? rows[i].Rating ?? "rate A" : "none (no permanent apparent-power limit read)"));
        }
        if (net.Generators.Count != model.Generators.Count)
            throw new InvalidOperationException("The bridged generators do not match the source injections one to one.");
        var gens = new List<CgmesGeneratorIdentity>(model.Generators.Count);
        for (int i = 0; i < model.Generators.Count; i++)
        {
            CimGenerator g = net.Generators[i];
            Generator m = model.Generators[i];
            if (m.Bus != bridge.BusIdOfNode[g.Node])
                throw new InvalidOperationException($"The bridged generator {i} is not on the bus of {g.MRID}.");
            gens.Add(new CgmesGeneratorIdentity(i, m.Bus, g.MRID, g.Class, m.Name, g.VoltageControl != false));
        }
        int live = model.Branches.Count(b => b.InService);
        int rated = model.Branches.Count(b => b.InService && b.RateAMva > 0);
        return new CgmesModelIdentity(buses, branches, gens,
            "Rate A is the permanent (PATL) ApparentPowerLimit, the smallest over the equipment and its terminals. Temporary " +
            "(TATL) and current limits are not imported, so rates B and C are 0 and a post-fault B or C request falls back to A.",
            live, rated);
    }

    private static BranchOutage OutageOf(CgmesModelIdentity identity, string mrid)
    {
        string id = CgmesBundleInspector.Mrid(mrid);
        List<CgmesBranchIdentity> hits = identity.Branches.Where(b => b.CimMrid == id).ToList();
        if (hits.Count == 0)
            throw new ArgumentException($"Security outage '{mrid}' names no branch of the bridged model.");
        if (hits.Count > 1)
            throw new ArgumentException($"Security outage '{mrid}' is a three-winding transformer ({hits.Count} model branches); v1 outages are single branches.");
        return new BranchOutage(hits[0].Index, hits[0].From, hits[0].To);
    }

    // ---- realised model -------------------------------------------------------------------------------------------

    /// <summary>The model the settled solve actually solved, with every applied control folded in at its realised value
    /// (remote control: the machines' buses PV at their final set-points; SVC: its SSH q taken out of the bus and its
    /// final susceptance added to Bs; regulating shunt: its final steps or susceptance added to Bs), and the solved
    /// voltages as the starting state. Null when there is no settled solution.</summary>
    private static (GridModel? Model, List<CgmesRepresentedControl> Represented) Realise(
        CimBridgeResult bridge, CimControlledPowerFlow? solver, PowerFlowResult r, bool settled, bool applied)
    {
        GridModel model = bridge.Model;
        var represented = new List<CgmesRepresentedControl>();
        var pos = new Dictionary<int, int>();
        for (int i = 0; i < model.Buses.Count; i++) pos[model.Buses[i].Id] = i;
        Bus[] buses = model.Buses.ToArray();
        Generator[] gens = model.Generators.ToArray();
        double? Vm(int bus) => settled ? r.Vm[pos[bus]] : null;

        foreach (int i in Enumerable.Range(0, buses.Length).Where(j => buses[j].Type is BusType.Pv or BusType.Slack))
        {
            Generator? g = gens.FirstOrDefault(x => x.Bus == buses[i].Id);
            if (g is null) continue;
            represented.Add(new CgmesRepresentedControl("local voltage", string.Join(", ", gens.Where(x => x.Bus == buses[i].Id).Select(x => x.Name)),
                buses[i].Id, buses[i].Id, g.Vg, Vm(buses[i].Id), "pu (solved voltage)",
                settled && r.SolvedBusTypes is { } t ? t[i] == BusType.Pq : null,
                buses[i].Type == BusType.Slack ? "slack (angle reference)" : "PV bus; reactive limits enforced"));
        }

        CimControlledSolveRecord? record = settled && solver is not null ? CimControlledPowerFlow.RecordOf(r) : null;
        if (settled && solver is not null && record is null)
            throw new InvalidOperationException("The controlled solve returned a result it did not register.");
        int s = 0;
        IReadOnlyList<CimControlState> states = solver?.LastStates ?? [];
        foreach (CimRemoteVoltageControl rc in bridge.RemoteVoltageControls)
            foreach (int gb in rc.GeneratorBusIds)
            {
                if (!applied || !settled)
                {
                    represented.Add(new CgmesRepresentedControl("remote voltage", string.Join(", ", rc.Machines), gb, rc.RegulatedBusId,
                        rc.TargetPu, null, "pu set-point", null, applied ? "not settled" : "held at the SSH q (controls not applied by request)"));
                    continue;
                }
                CimControlState st = states[s++];
                if (st.Kind != "remote voltage") throw new InvalidOperationException("The control states are not in the bridge's order.");
                int p = pos[gb];
                if (buses[p].Type == BusType.Pq) buses[p] = buses[p] with { Type = BusType.Pv };
                for (int g = 0; g < gens.Length; g++) if (gens[g].Bus == gb) gens[g] = gens[g] with { Vg = st.Value };
                represented.Add(new CgmesRepresentedControl("remote voltage", string.Join(", ", rc.Machines), gb, rc.RegulatedBusId,
                    rc.TargetPu, st.Value, "pu set-point at the machine bus", st.AtLimit,
                    "exported as a local PV set-point at the machine bus (its realised value)"));
            }
        foreach (CimSusceptanceControl sc in bridge.SusceptanceControls)
        {
            if (!applied || !settled || record is null || !record.SusceptanceMvarByMrid.TryGetValue(sc.MRID, out double x))
            {
                represented.Add(new CgmesRepresentedControl("susceptance (" + sc.Class + ")", sc.Name, sc.BusId, sc.RegulatedBusId,
                    sc.TargetPu, null, "MVAr at 1.0 pu", null, applied ? "not settled" : "held at its SSH q (controls not applied by request)"));
                continue;
            }
            int p = pos[sc.BusId];
            buses[p] = buses[p] with { Qd = buses[p].Qd - sc.HeldQdMvar, Bs = buses[p].Bs + x };
            represented.Add(new CgmesRepresentedControl("susceptance (" + sc.Class + ")", sc.Name, sc.BusId, sc.RegulatedBusId,
                sc.TargetPu, x, "MVAr at 1.0 pu", x >= sc.BMaxMvar || x <= sc.BMinMvar,
                "exported as a fixed shunt susceptance at its realised value (its SSH q removed from the bus demand)"));
        }
        foreach (CimShuntRegulation sh in bridge.ShuntRegulations)
        {
            int p = pos[sh.BusId];
            if (applied && settled && record is not null && record.SwitchedMvarByMrid.TryGetValue(sh.MRID, out double sw))
            {
                buses[p] = buses[p] with { Bs = buses[p].Bs + sw };
                represented.Add(new CgmesRepresentedControl("regulating shunt (switched)", sh.Name, sh.BusId, sh.RegulatedBusId,
                    null, sw, "MVAr at 1.0 pu relative to the SSH sections", null, "exported as fixed sections at the realised steps"));
            }
            else if (applied && settled && record is not null && record.SusceptanceMvarByMrid.TryGetValue(sh.MRID, out double cont))
            {
                buses[p] = buses[p] with { Bs = buses[p].Bs + cont };
                represented.Add(new CgmesRepresentedControl("regulating shunt (continuous)", sh.Name, sh.BusId, sh.RegulatedBusId,
                    sh.Continuous?.TargetPu, cont, "MVAr at 1.0 pu relative to the SSH sections", null, "exported as a fixed susceptance at its realised value"));
            }
            else
            {
                represented.Add(new CgmesRepresentedControl("regulating shunt (held)", sh.Name, sh.BusId, sh.RegulatedBusId,
                    null, null, "MVAr at 1.0 pu", null, "held at its SSH sections"));
            }
        }
        if (!settled) return (null, represented);
        for (int i = 0; i < buses.Length; i++) buses[i] = buses[i] with { Vm = r.Vm[i], VaDeg = r.VaDeg[i] };
        return (model.CopyWith(buses: buses, generators: gens), represented);
    }

    // ---- limits ---------------------------------------------------------------------------------------------------

    /// <summary>Absolute limits of a converged state, as P1 evaluates them (voltage band at every bus, branch MVA at both
    /// ends against rate A, a unit's realised P and Q where it is alone on its bus). <paramref name="accounting"/> is the
    /// model whose demand the injections are reconciled with. Unrated branches are counted, not evaluated.</summary>
    internal static SecurityState Evaluate(GridModel accounting, PowerFlowResult solved, double minPu, double maxPu)
    {
        PowerFlowOptions o = ContingencyAnalysis.StudyOptions;
        int count = accounting.Buses.Count;
        bool complete = solved.BusIds.SequenceEqual(accounting.Buses.Select(b => b.Id)) && solved.Vm.Length == count
            && solved.VaDeg.Length == count && solved.PInjMw.Length == count && solved.QInjMvar.Length == count
            && solved.LineFlows.Count == accounting.Branches.Count && solved.SolvedBusTypes?.Count == count;
        bool finite = solved.Vm.All(double.IsFinite) && solved.VaDeg.All(double.IsFinite) && solved.PInjMw.All(double.IsFinite)
            && solved.QInjMvar.All(double.IsFinite) && double.IsFinite(solved.MaxMismatch) && double.IsFinite(solved.TotalLossMw);
        if (solved.MaxMismatch > o.Tolerance || !solved.Converged || solved.Status != PowerFlowStatus.Converged || !complete || !finite)
            return new(null, "NO SOLUTION", "Convergence or complete finite AC evidence unavailable.", solved.Status,
                double.IsFinite(solved.MaxMismatch) ? solved.MaxMismatch : null, double.IsFinite(solved.TotalLossMw) ? solved.TotalLossMw : null,
                [], solved.SolvedBusTypes);
        double unitTolerance = o.Tolerance * accounting.BaseMva;
        var limits = new List<LimitEvidence>();
        var missing = new List<string>();
        for (int i = 0; i < count; i++)
        {
            Bus bus = accounting.Buses[i];
            limits.Add(Limit("Voltage", bus.Id, "pu", solved.Vm[i], minPu, maxPu));
            int[] units = Enumerable.Range(0, accounting.Generators.Count).Where(g => accounting.Generators[g].Bus == bus.Id).ToArray();
            if (units.Length > 1) { missing.Add($"Bus {bus.Id}: individual realised generator allocation is unavailable."); continue; }
            if (units.Length == 0)
            {
                if (bus.Type == BusType.Slack) missing.Add($"Bus {bus.Id}: the slack bus has no in-service unit, so its balancing output is not checked.");
                continue;
            }
            Generator g = accounting.Generators[units[0]];
            // As P1: the converged injection is generation minus modelled demand (shunts are in the admittance matrix).
            limits.Add(Limit("GeneratorP", units[0], "MW", solved.PInjMw[i] + bus.Pd, g.Pmin, g.Pmax) with { Tolerance = unitTolerance });
            limits.Add(Limit("GeneratorQ", units[0], "MVAr", solved.QInjMvar[i] + bus.Qd, g.Qmin, g.Qmax) with { Tolerance = unitTolerance });
        }
        int unrated = 0;
        for (int i = 0; i < accounting.Branches.Count; i++)
        {
            Branch br = accounting.Branches[i];
            LineFlow f = solved.LineFlows[i];
            if (f.From != br.From || f.To != br.To) { missing.Add($"Branch {i}: solved identity mismatch."); continue; }
            if (!br.InService) continue;
            if (!(br.RateAMva > 0) || !double.IsFinite(br.RateAMva))
            {
                unrated++;
                continue;
            }
            double from = Math.Sqrt(f.PFromMw * f.PFromMw + f.QFromMvar * f.QFromMvar);   // DetMath rule: no Hypot
            double to = Math.Sqrt(f.PToMw * f.PToMw + f.QToMvar * f.QToMvar);
            limits.Add(Limit("BranchFrom", i, "MVA", from, null, br.RateAMva) with { Source = "A" });
            limits.Add(Limit("BranchTo", i, "MVA", to, null, br.RateAMva) with { Source = "A" });
        }
        if (unrated > 0) missing.Add($"{unrated} live branch(es) have no rating and are not thermally evaluated.");
        if (limits.Any(l => !double.IsFinite(l.Observed)))
            return new(null, "INCOMPLETE", "Non-finite realised measurement.", solved.Status, solved.MaxMismatch, solved.TotalLossMw,
                limits.Where(l => double.IsFinite(l.Observed)).ToArray(), solved.SolvedBusTypes);
        string disposition = missing.Count > 0 ? "INCOMPLETE"
            : limits.Any(l => l.Violated) ? $"VIOLATIONx{limits.Count(l => l.Violated)}" : "SECURE";
        string? reason = missing.Count > 0 ? string.Join(" ", missing) : null;
        return new(null, disposition, reason, solved.Status, solved.MaxMismatch, solved.TotalLossMw, limits, solved.SolvedBusTypes);
    }

    private static LimitEvidence Limit(string kind, int index, string units, double value, double? lower, double? upper)
    {
        double? lo = lower is double l && double.IsFinite(l) ? l : null;
        double? hi = upper is double h && double.IsFinite(h) ? h : null;
        return new(kind, index, units, value, lo, hi, lo.HasValue ? value - lo : null, hi.HasValue ? hi - value : null);
    }

    // ---- exchange -------------------------------------------------------------------------------------------------

    private static CgmesExchangeReport Unevaluated(string reason) => new(
        "UNEVALUATED", reason, ExchangeForm, null, [], [], null, [], null, [], null, [], [], null, [], [], null, null, null, [],
        ["identity", "topology", "ratings", "controlled AC output"]);

    private const string ExchangeForm =
        "One CGMES RDF/XML document (EQ, TP, SSH and SV content in one file) written by CgmesWriter from the realised model, " +
        "read back with CgmesWriter.ReadBackOptions (SV starting voltages and PATL ratings). Not a split profile bundle.";

    private static (CgmesExchangeReport, string?) Exchange(GridModel realised, PowerFlowResult source, CgmesModelIdentity identity, CgmesStudyRequest request)
    {
        CgmesWriteResult written;
        try { written = CgmesWriter.Write(realised); }
        catch (InvalidDataException ex) { return (Unevaluated("CgmesWriter refused the realised model: " + ex.Message), null); }
        string sha = CgmesBundleInspector.Hash(System.Text.Encoding.UTF8.GetBytes(written.Text));
        GridModel expected = CgmesWriter.InFileOrder(realised, written);
        GridModel readBack = CgmesWriter.ReadBack(written.Text, realised.BaseMva, request.Name);
        CimNetwork rbNet = CimImporter.Parse(written.Text, CgmesWriter.ReadBackOptions);

        // Identity: source mRID -> model position -> exported mRID -> read-back position.
        var map = new List<CgmesExchangeIdentity>();
        for (int i = 0; i < realised.Buses.Count; i++)
        {
            CgmesBusIdentity b = identity.Buses[i];
            map.Add(new CgmesExchangeIdentity("bus", b.CimNodes.Count == 0 ? "(star point)" : string.Join("+", b.CimNodes), b.BusId,
                written.Network.Buses[i].MRID, i + 1));
        }
        int lines = written.Network.Branches.Count;
        for (int j = 0; j < written.BranchOrder.Count; j++)
        {
            int k = written.BranchOrder[j];
            CgmesBranchIdentity b = identity.Branches[k];
            string export = j < lines ? written.Network.Branches[j].MRID : written.Network.Transformers[j - lines].MRID;
            map.Add(new CgmesExchangeIdentity("branch", b.Winding is int w ? $"{b.CimMrid}#w{w}" : b.CimMrid, k, export, j));
        }
        for (int g = 0; g < realised.Generators.Count; g++)
            map.Add(new CgmesExchangeIdentity("generator", identity.Generators[g].CimMrid, g, written.Network.Generators[g].MRID, g));

        var idDiff = new List<string>();
        void Same<T>(string what, IEnumerable<T> a, IEnumerable<T> b, Func<T, string> key)
        {
            List<string> x = a.Select(key).ToList(), y = b.Select(key).ToList();
            if (!x.SequenceEqual(y, StringComparer.Ordinal))
                idDiff.Add($"{what}: written [{string.Join("; ", x.Except(y))}] read back [{string.Join("; ", y.Except(x))}]");
        }
        Same("buses", written.Network.Buses, rbNet.Buses, b => $"{b.MRID}|{b.Name}");
        Same("lines", written.Network.Branches, rbNet.Branches, b => $"{b.MRID}|{b.Name}|{b.FromNode}|{b.ToNode}");
        Same("transformers", written.Network.Transformers, rbNet.Transformers, t => $"{t.MRID}|{t.Name}|{string.Join(",", t.Ends.Select(e => e.Node))}");
        Same("generators", written.Network.Generators, rbNet.Generators, g => $"{g.MRID}|{g.Name}|{g.Node}");
        Same("loads", written.Network.Loads, rbNet.Loads, l => $"{l.MRID}|{l.Node}");
        Same("shunts", written.Network.Shunts, rbNet.Shunts, s => $"{s.MRID}|{s.Node}");

        // Topology: the read-back against the input in file order (buses 1..n in model order, lines then transformers).
        var topo = new List<string>();
        if (expected.Buses.Count != readBack.Buses.Count) topo.Add($"bus count {expected.Buses.Count} vs {readBack.Buses.Count}");
        else
            for (int i = 0; i < expected.Buses.Count; i++)
                if (expected.Buses[i].Id != readBack.Buses[i].Id || expected.Buses[i].Type != readBack.Buses[i].Type
                    || !Bits(expected.Buses[i].BaseKv, readBack.Buses[i].BaseKv))
                    topo.Add($"bus {i + 1}: {expected.Buses[i].Type} {expected.Buses[i].BaseKv} kV vs {readBack.Buses[i].Type} {readBack.Buses[i].BaseKv} kV");
        if (expected.Branches.Count != readBack.Branches.Count) topo.Add($"branch count {expected.Branches.Count} vs {readBack.Branches.Count}");
        else
            for (int j = 0; j < expected.Branches.Count; j++)
                if (expected.Branches[j].From != readBack.Branches[j].From || expected.Branches[j].To != readBack.Branches[j].To
                    || expected.Branches[j].InService != readBack.Branches[j].InService)
                    topo.Add($"branch {j}: {expected.Branches[j].From}-{expected.Branches[j].To} vs {readBack.Branches[j].From}-{readBack.Branches[j].To}");
        if (expected.Generators.Count != readBack.Generators.Count) topo.Add($"generator count {expected.Generators.Count} vs {readBack.Generators.Count}");
        else
            for (int g = 0; g < expected.Generators.Count; g++)
                if (expected.Generators[g].Bus != readBack.Generators[g].Bus) topo.Add($"generator {g}: bus {expected.Generators[g].Bus} vs {readBack.Generators[g].Bus}");

        var ratings = new List<string>();
        bool sameShape = topo.Count == 0;
        if (sameShape)
            for (int j = 0; j < expected.Branches.Count; j++)
                if (!Bits(expected.Branches[j].RateAMva, readBack.Branches[j].RateAMva))
                    ratings.Add($"branch {j}: rate A {expected.Branches[j].RateAMva:R} MVA vs {readBack.Branches[j].RateAMva:R} MVA");

        var data = new List<CgmesQuantityDifference>();
        if (sameShape)
        {
            data.Add(Diff("branch R", "pu", expected.Branches.Count, j => expected.Branches[j].R, j => readBack.Branches[j].R, 0, j => $"branch {j}"));
            data.Add(Diff("branch X", "pu", expected.Branches.Count, j => expected.Branches[j].X, j => readBack.Branches[j].X, 0, j => $"branch {j}"));
            data.Add(Diff("branch B", "pu", expected.Branches.Count, j => expected.Branches[j].B, j => readBack.Branches[j].B, 0, j => $"branch {j}"));
            data.Add(Diff("branch tap ratio", "pu", expected.Branches.Count, j => Tap(expected.Branches[j]), j => Tap(readBack.Branches[j]), 0, j => $"branch {j}"));
            data.Add(Diff("branch phase shift", "deg", expected.Branches.Count, j => expected.Branches[j].PhaseShiftDeg, j => readBack.Branches[j].PhaseShiftDeg, 0, j => $"branch {j}"));
            data.Add(Diff("bus Pd", "MW", expected.Buses.Count, i => expected.Buses[i].Pd, i => readBack.Buses[i].Pd, 0, i => $"bus {i + 1}"));
            data.Add(Diff("bus Qd", "MVAr", expected.Buses.Count, i => expected.Buses[i].Qd, i => readBack.Buses[i].Qd, 0, i => $"bus {i + 1}"));
            data.Add(Diff("bus Gs", "MW at 1 pu", expected.Buses.Count, i => expected.Buses[i].Gs, i => readBack.Buses[i].Gs, 0, i => $"bus {i + 1}"));
            data.Add(Diff("bus Bs", "MVAr at 1 pu", expected.Buses.Count, i => expected.Buses[i].Bs, i => readBack.Buses[i].Bs, 0, i => $"bus {i + 1}"));
            data.Add(Diff("starting Vm", "pu", expected.Buses.Count, i => expected.Buses[i].Vm, i => readBack.Buses[i].Vm, 0, i => $"bus {i + 1}"));
            data.Add(Diff("generator Pg", "MW", expected.Generators.Count, g => expected.Generators[g].Pg, g => readBack.Generators[g].Pg, 0, g => $"generator {g}"));
            data.Add(Diff("generator Vg", "pu", expected.Generators.Count, g => expected.Generators[g].Vg, g => readBack.Generators[g].Vg, 0, g => $"generator {g}"));
        }

        // Controlled AC output: (a) the realised model re-solved plainly reproduces the controlled solve (the fold is
        // faithful); (b) the read-back re-solved plainly reproduces it through the exchange. Same engine rule and options.
        PowerFlowOptions o = ContingencyAnalysis.StudyOptions;
        PowerFlowResult fold = SolverSelector.CreateNewton(realised).Solve(realised, o);
        PowerFlowResult rb = SolverSelector.CreateNewton(readBack).Solve(readBack, o);
        int n = realised.Buses.Count;
        List<CgmesQuantityDifference> foldCheck = Compare(source, fold, n, Enumerable.Range(0, realised.Branches.Count).ToArray(), i => i);
        List<CgmesQuantityDifference> rbCheck = sameShape && rb.Converged
            ? Compare(source, rb, n, written.BranchOrder.ToArray(), i => i)
            : [];
        SecurityState rbState = sameShape ? Evaluate(readBack, rb, request.MinPu, request.MaxPu) : new SecurityState(null, "UNEVALUATED", "Read-back shape differs.", rb.Status, null, null, [], null);
        SecurityState srcState = Evaluate(realised, source, request.MinPu, request.MaxPu);
        bool limitsAgree = rbState.Disposition == srcState.Disposition;
        bool outputOk = fold.Converged && rb.Converged && foldCheck.All(d => d.Within) && rbCheck.Count > 0 && rbCheck.All(d => d.Within) && limitsAgree;

        var verified = new List<string>();
        var notVerified = new List<string>();
        void Field(bool ok, string name) => (ok ? verified : notVerified).Add(name);
        Field(idDiff.Count == 0, "equipment identity (exported mRIDs, names and terminal nodes of buses, lines, transformers, generators, loads and shunts)");
        Field(topo.Count == 0, "electrical topology (bus numbering and types, base voltages, branch end buses and in-service state, generator buses)");
        Field(sameShape && ratings.Count == 0, "permanent ratings (rate A, bit for bit)");
        Field(outputOk, $"controlled AC output (Vm within {VoltageTolerancePu} pu, angle within {AngleToleranceDeg} deg, terminal P and Q within {PowerToleranceMw} MW / MVAr, the same limit disposition)");
        notVerified.Add("rates B and C, out-of-service equipment, sequence data, dynamics, layout and the source mRIDs (not written by CgmesWriter; see the writer notes)");
        if (realised.FrequencyHz != readBack.FrequencyHz)
            notVerified.Add($"nominal frequency ({realised.FrequencyHz} Hz is not carried; the read-back takes {readBack.FrequencyHz} Hz)");

        var report = new CgmesExchangeReport("EVALUATED", null, ExchangeForm, sha, written.Notes, map,
            idDiff.Count == 0, idDiff, topo.Count == 0, topo, sameShape && ratings.Count == 0, ratings, data,
            outputOk, foldCheck, rbCheck, rb.Status, double.IsFinite(rb.MaxMismatch) ? rb.MaxMismatch : null, rbState.Disposition,
            verified, notVerified);
        return (report, written.Text);
    }

    private static double Tap(Branch b) => b.TapRatio <= 0 ? 1.0 : b.TapRatio;

    private static bool Bits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static CgmesQuantityDifference Diff(string what, string units, int count, Func<int, double> expected, Func<int, double> actual,
        double tolerance, Func<int, string> label)
    {
        double worst = 0.0;
        int at = -1, differing = 0;
        for (int i = 0; i < count; i++)
        {
            double e = expected(i), a = actual(i);
            if (Bits(e, a)) continue;
            differing++;
            double d = Math.Abs(e - a);
            if (!double.IsFinite(d)) d = double.PositiveInfinity;
            if (at < 0 || d > worst) { worst = d; at = i; }
        }
        return new CgmesQuantityDifference(what, units, count, differing, worst, tolerance, at < 0 ? null : label(at),
            at < 0 ? null : expected(at), at < 0 ? null : actual(at));
    }

    /// <summary>Per-bus voltages and per-terminal flows of <paramref name="actual"/> (buses in model order, branch j of it
    /// being model branch <paramref name="branchOrder"/>[j]) against the controlled <paramref name="source"/>.</summary>
    private static List<CgmesQuantityDifference> Compare(PowerFlowResult source, PowerFlowResult actual, int buses, int[] branchOrder, Func<int, int> bus)
    {
        if (!actual.Converged || actual.Vm.Length != buses || actual.LineFlows.Count != branchOrder.Length) return [];
        return
        [
            Diff("Vm", "pu", buses, i => source.Vm[bus(i)], i => actual.Vm[i], VoltageTolerancePu, i => $"bus {source.BusIds[bus(i)]}"),
            Diff("Va", "deg", buses, i => source.VaDeg[bus(i)], i => actual.VaDeg[i], AngleToleranceDeg, i => $"bus {source.BusIds[bus(i)]}"),
            Diff("P from", "MW", branchOrder.Length, j => source.LineFlows[branchOrder[j]].PFromMw, j => actual.LineFlows[j].PFromMw, PowerToleranceMw, j => $"branch {branchOrder[j]}"),
            Diff("Q from", "MVAr", branchOrder.Length, j => source.LineFlows[branchOrder[j]].QFromMvar, j => actual.LineFlows[j].QFromMvar, PowerToleranceMw, j => $"branch {branchOrder[j]}"),
            Diff("P to", "MW", branchOrder.Length, j => source.LineFlows[branchOrder[j]].PToMw, j => actual.LineFlows[j].PToMw, PowerToleranceMw, j => $"branch {branchOrder[j]}"),
            Diff("Q to", "MVAr", branchOrder.Length, j => source.LineFlows[branchOrder[j]].QToMvar, j => actual.LineFlows[j].QToMvar, PowerToleranceMw, j => $"branch {branchOrder[j]}"),
            Diff("loss", "MW", 1, _ => source.TotalLossMw, _ => actual.TotalLossMw, PowerToleranceMw, _ => "system"),
        ];
    }
}
