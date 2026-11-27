using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>A check asked for in a grid-code study (<see cref="GridCodeRequest.Checks"/>).</summary>
public abstract record GridCodeCheckRequest(string Id);

/// <summary><c>frt-envelope</c>: the fault to apply (default: three-phase at the PCC at 0.1 s through the retained-voltage reactance).</summary>
public sealed record FrtEnvelopeRequest(FrtFault Fault, double? DurationSeconds = null) : GridCodeCheckRequest("frt-envelope");

/// <summary><c>frt-retained-voltage</c> (helper, no verdict).</summary>
public sealed record FrtRetainedVoltageRequest() : GridCodeCheckRequest("frt-retained-voltage");

/// <summary><c>frt-recovery</c>: the fault to apply (as <see cref="FrtEnvelopeRequest"/>).</summary>
public sealed record FrtRecoveryRequest(FrtFault Fault, double? DurationSeconds = null) : GridCodeCheckRequest("frt-recovery");

/// <summary><c>frt-uv-settings</c>: the undervoltage stages.</summary>
public sealed record FrtUvSettingsRequest(IReadOnlyList<GridCodeRelayStage> Stages) : GridCodeCheckRequest("frt-uv-settings");

/// <summary><c>q-capability</c>: Pmax, Qmax and Qmin (null = the unit's case values) and, where the code fixes no
/// requirement, the user's U-Q/Pmax profile with its citation.</summary>
public sealed record QCapabilityRequest(double? PmaxMw = null, double? QmaxMvar = null, double? QminMvar = null, UqProfile? UqProfile = null)
    : GridCodeCheckRequest("q-capability");

/// <summary><c>lfsm-o</c> (<see cref="Over"/>) or <c>lfsm-u</c>. Frequency injection (a step to <see cref="FrequencyHz"/>) unless
/// <see cref="Events"/> is given (the network variant). RfG needs <see cref="ThresholdHz"/>, <see cref="Droop"/> and
/// <see cref="Pref"/> (<c>output</c> or <c>pmax</c>); GB fixes them. GB LFSM-O is read 10 s after the step (or, in the
/// network variant, 10 s after the first event), so <see cref="TEndSeconds"/> must be that time.</summary>
public sealed record LfsmRequest(bool Over, double FrequencyHz, double TEndSeconds = 10.0, double StepSeconds = 0.01, double? ThresholdHz = null,
    double? Droop = null, string? Pref = null, double? PmaxMw = null, double? Pm0Pu = null, IReadOnlyList<RmsEvent>? Events = null)
    : GridCodeCheckRequest(Over ? "lfsm-o" : "lfsm-u");

/// <summary><c>fsm-step</c>: a step to <see cref="FrequencyHz"/>; t1 and t2 (null = the code's limits), dP1 as a fraction of
/// Pmax (GB 10 %; RfG the TSO's choice), with or without inertia, and the RfG options checked against Table 4 when given.
/// <see cref="Droop"/> is the unit's droop setting, range-checked; the step must be at least dP1 fn s from nominal with s that
/// droop, or the code's largest droop when none is given.</summary>
public sealed record FsmStepRequest(double FrequencyHz, double? T1Seconds = null, double? T2Seconds = null, double? Dp1PerPmax = null,
    bool WithInertia = true, double? TEndSeconds = null, double StepSeconds = 0.01, double? PmaxMw = null, double? Pm0Pu = null,
    double? InsensitivityHz = null, double? DeadbandHz = null, double? Droop = null) : GridCodeCheckRequest("fsm-step");

/// <summary><c>rocof-settings</c>: the 81R stages; RfG needs the TSO's limit and window with its source.</summary>
public sealed record RocofSettingsRequest(IReadOnlyList<GridCodeRelayStage> Stages, double? LimitHzPerSecond = null, double? WindowSeconds = null, string? Source = null)
    : GridCodeCheckRequest("rocof-settings");

/// <summary><c>rocof-event</c>: the RMS events (a generator trip or a load step), the run length, and for RfG the TSO's limit.</summary>
public sealed record RocofEventRequest(IReadOnlyList<RmsEvent> Events, double DurationSeconds, double? LimitHzPerSecond = null, double? WindowSeconds = null,
    string? Source = null) : GridCodeCheckRequest("rocof-event");

/// <summary><c>frequency-relays</c>: the 81U / 81O stages.</summary>
public sealed record FrequencyRelaysRequest(IReadOnlyList<GridCodeRelayStage> Stages) : GridCodeCheckRequest("frequency-relays");

/// <summary>RC5 WP22, <c>ffci-envelope</c> (gb-ecc only): GB fast fault current injection (ECC.6.3.16) on the FRT run of the
/// fault given (as <see cref="FrtEnvelopeRequest"/>; a balanced fault).</summary>
public sealed record FfciRequest(FrtFault Fault, double? DurationSeconds = null) : GridCodeCheckRequest(FfciCheck.Id);

/// <summary>RC5 WP22, <c>frt-long</c> (gb-ecc only): faults over 140 ms (ECC.6.3.15.9). Each duration (s, null = every labelled
/// corner of the module's profile longer than 140 ms) is tested as a dip held at the profile's voltage; stages of
/// <see cref="StageSeconds"/>, <see cref="AfterSeconds"/> run after clearing, at most <see cref="MaxPasses"/> staging passes,
/// the dip at <see cref="BusId"/> (null = the PCC) from <see cref="AtSeconds"/>.</summary>
public sealed record LongFaultRequest(IReadOnlyList<double>? DurationsSeconds = null, double StageSeconds = 0.05, double AfterSeconds = 1.0, int MaxPasses = 8,
    int? BusId = null, double AtSeconds = 0.1) : GridCodeCheckRequest(LongFaultCheck.Id);

/// <summary>RC5 WP22, <c>avr-step</c> (gb-ecc only): the ECC.A.6.2.4.1 open-circuit step, run with
/// <see cref="StepSeconds"/> (null = the study step) for <see cref="DurationSeconds"/>. Needs the unit's exciter
/// (<see cref="GridCodeRequest.Exciters"/>).</summary>
public sealed record AvrStepRequest(double? StepSeconds = null, double DurationSeconds = 10.0) : GridCodeCheckRequest(StepResponseChecks.AvrId);

/// <summary>RC5 WP22, <c>vctrl-step</c> (gb-ecc only): the ECC.A.7.2.3.1 voltage step made by <see cref="Events"/>, run for
/// <see cref="DurationSeconds"/>; <see cref="LeadToLag"/> picks the 2 s limit (else zero to maximum, 1 s); the unit's
/// reactive range (null = its case Qmax / Qmin).</summary>
public sealed record VctrlStepRequest(IReadOnlyList<RmsEvent> Events, double DurationSeconds, bool LeadToLag = false, double? QmaxMvar = null, double? QminMvar = null)
    : GridCodeCheckRequest(StepResponseChecks.VctrlId);

/// <summary>A grid-code compliance study: the case, its dynamic data, the unit, the PCC and plant, the code and module, and
/// the checks. RfG studies give the TSO's FRT profile (<see cref="RfgProfile"/>) and the synchronous area (<see cref="Area"/>).</summary>
public sealed record GridCodeRequest
{
    public required string Label { get; init; }
    public required string Code { get; init; }
    public required ModuleType ModuleType { get; init; }
    public required string Technology { get; init; }
    public required double ConnectionKv { get; init; }
    public required GridModel Model { get; init; }
    public IReadOnlyList<SubtransientInit.MachineData>? Data { get; init; }
    public required int UnitIndex { get; init; }
    public required int PccBus { get; init; }
    public IReadOnlyList<int> PlantBuses { get; init; } = Array.Empty<int>();
    public bool ExtendedClearingTime { get; init; }
    public FrtProfile? RfgProfile { get; init; }
    public string? Area { get; init; }
    public bool FsmMode { get; init; }
    public double StepSeconds { get; init; } = 0.01;
    public required IReadOnlyList<GridCodeCheckRequest> Checks { get; init; }
    /// <summary>RC5 WP22: each generator's exciter (aligned to <c>Model.Generators</c>, null = none), read only by
    /// <c>avr-step</c>. Null (the default) leaves every other check exactly as before.</summary>
    public IReadOnlyList<RmsExciter?>? Exciters { get; init; }
}

/// <summary>The result of a grid-code study (<c>gridsim-gridcode/1</c>).</summary>
public sealed record GridCodeResult(string Label, GridCodeRequirementSet Requirements, GridCodeRequest Request, FrtProfile? Profile, string ProfileSource,
    IReadOnlyList<GridCodeCheckResult> Checks, string Verdict, int Passed, int Failed, int NotEvaluated, int NotApplicable, IReadOnlyList<string> Notes,
    IReadOnlyList<FrtRun> FrtRuns);

/// <summary>
/// The grid-code compliance suite (GB Grid Code ECC Issue 6 Revision 33, Regulation (EU) 2016/631): runs the checks of a
/// <see cref="GridCodeRequest"/> and gives the verdict as <see cref="RuleSetEngine"/> does: NON-COMPLIANT on any failure,
/// else INCOMPLETE while any check is not evaluated (or nothing with a verdict was checked), else COMPLIANT.
/// </summary>
public static class GridCodeStudy
{
    public const string Format = "gridsim-gridcode/1";

    /// <summary>What the suite is and is not, carried in every result.</summary>
    public static IReadOnlyList<string> ScopeNotes { get; } = new[]
    {
        "study evidence only: not a compliance certificate, and not checked against an ESO compliance submission or a vendor tool",
        "the FRT profile shape is read from Figure 3 of Regulation (EU) 2016/631; Figure ECC.6.3.2.3 and the FSM trajectory of Figure 6 are read from the figures",
        "not covered: the PPM reactive polygons of Figures ECC.6.3.2.4(a)-(d) and ECC.6.3.2.5/6 (user-supplied only), faults over 140 ms (ECC.6.3.15.9), fast fault current injection (ECC.6.3.16), voltage control and AVR performance (ECC Appendices E6/E7), FSM monitoring and response services, unbalanced voltages away from the fault point, active-power recovery of synchronous units",
    };

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Run the study. Input it cannot accept (a module the code does not cover, an RfG choice outside its range, a
    /// missing value) is refused with <see cref="InvalidDataException"/> naming it.</summary>
    public static GridCodeResult Run(GridCodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        GridCodeRequirementSet req = GridCodeRequirements.Load(GridCodeRequirements.IdOfCode(request.Code));
        bool gb = request.Code == "gb-ecc";
        GridModel model = request.Model;
        if (request.UnitIndex < 0 || request.UnitIndex >= model.Generators.Count) throw new InvalidDataException("the unit is not an in-service generator of the case.");
        if (!model.Buses.Any(b => b.Id == request.PccBus)) throw new InvalidDataException($"pccBus {request.PccBus} is not a bus of the case.");
        if (request.Checks.Count == 0) throw new InvalidDataException("checks must name at least one check.");
        Generator gen = model.Generators[request.UnitIndex];
        double mbase = MachineBase.EffectiveMva(gen, model.BaseMva);

        // The FRT profile: fixed for GB; the TSO's choice, checked against the RfG table, for RfG.
        FrtProfile profile;
        string profileSource;
        if (gb)
        {
            if (request.RfgProfile is not null) throw new InvalidDataException("frtProfile is for eu-rfg only: the GB profile is fixed by the code.");
            if (request.ExtendedClearingTime) throw new InvalidDataException("extendedClearingTime is for eu-rfg only.");
            GbFrtProfileEntry e = GridCodeRequirements.GbProfileEntry(req, request.ModuleType, request.Technology, request.ConnectionKv);
            profile = e.Profile;
            profileSource = $"fixed by {e.Profile.Clause} ({e.AppliesTo})";
        }
        else
        {
            RfgFrtTable table = GridCodeRequirements.RfgTable(req, request.ModuleType, request.Technology, request.ConnectionKv);
            bool needsProfile = request.Checks.Any(c => c is FrtEnvelopeRequest or FrtRecoveryRequest or FrtRetainedVoltageRequest or FrtUvSettingsRequest);
            if (request.RfgProfile is null)
            {
                if (needsProfile) throw new InvalidDataException($"eu-rfg: the FRT checks need frtProfile, the TSO's choice inside RfG Table {table.Table} ({table.Clause}).");
                profile = null!;
                profileSource = "";
            }
            else
            {
                IReadOnlyList<string> v = RfgRanges.Validate(request.RfgProfile, table, request.ExtendedClearingTime);
                if (v.Count > 0) throw new InvalidDataException("frtProfile: " + string.Join("; ", v));
                profile = new FrtProfile(request.RfgProfile.Uret, request.RfgProfile.Uclear, request.RfgProfile.Urec1, request.RfgProfile.Urec2,
                    request.RfgProfile.Tclear, request.RfgProfile.Trec1, request.RfgProfile.Trec2, request.RfgProfile.Trec3, "RfG Table " + table.Table + ", " + table.Clause);
                profileSource = $"the TSO's choice, inside RfG Table {table.Table}" + (request.ExtendedClearingTime ? " (extended clearing time)" : "");
            }
        }

        PowerFlowResult? pf = null;
        PowerFlowResult Pf()
        {
            if (pf is not null) return pf;
            pf = SolverSelector.CreateNewton(model).Solve(model, new PowerFlowOptions());
            if (!pf.Converged) throw new InvalidDataException("the case power flow did not converge, so there is no operating point.");
            return pf;
        }
        IReadOnlyList<SubtransientInit.MachineData> Data() => request.Data
            ?? throw new InvalidDataException("this check needs the case's dynamic data (dynData).");

        var results = new List<GridCodeCheckResult>();
        var runs = new List<FrtRun>();
        var notes = new List<string>(ScopeNotes);
        string fsmBlock = "fsm";
        // RC5 WP22: the GB extension set, read only when an extension check is asked for.
        GridCodeExtRequirementSet? ext = null;
        GridCodeExtRequirementSet Ext()
        {
            if (ext is not null) return ext;
            if (!gb) throw new InvalidDataException("ffci-envelope, frt-long, avr-step and vctrl-step are GB Grid Code checks (code gb-ecc).");
            ext = GridCodeExtRequirements.LoadGb();
            notes.Add($"extension checks: {ext.Title} ({ext.Id}); each check's notes say what it leaves out");
            // 2.1.126: the "not covered" scope note above is kept word for word (it is still true of a run without these
            // checks); this line says which of its topics the requested extension checks take up.
            var covered = new List<string>();
            if (request.Checks.Any(x => x is LongFaultRequest)) covered.Add("faults over 140 ms (ECC.6.3.15.9) by frt-long");
            if (request.Checks.Any(x => x is FfciRequest)) covered.Add("fast fault current injection (ECC.6.3.16) by ffci-envelope");
            if (request.Checks.Any(x => x is AvrStepRequest)) covered.Add("AVR performance (ECC.A.6.2.4.1) by avr-step");
            if (request.Checks.Any(x => x is VctrlStepRequest)) covered.Add("voltage control performance (ECC.A.7.2.3.1) by vctrl-step");
            notes.Add("of the topics listed as not covered, this study checks " + string.Join("; ", covered) + ", within the limits in each check's notes");
            notes.Add(ext.NotInIssue);
            return ext;
        }
        foreach (GridCodeCheckRequest c in request.Checks)
        {
            switch (c)
            {
                case FrtEnvelopeRequest fe:
                {
                    FrtRun run = GridCodeChecks.RunFrt(model, Pf(), Data(), request.PccBus, profile, fe.Fault, request.StepSeconds, fe.DurationSeconds);
                    runs.Add(run);
                    results.Add(GridCodeChecks.FrtEnvelope(run, model, Pf(), Data(), request.UnitIndex, request.PccBus, PreFault(request, gb, Pf())));
                    break;
                }
                case FrtRecoveryRequest fr:
                {
                    string clause = gb ? req.Clause("recovery") : "RfG Art. 17(3) and 20(3) (post-fault active power recovery, the TSO's specification)";
                    if (!gb && !SubtransientInit.IsConverterUnit(gen, Data()[request.UnitIndex]))
                    {
                        results.Add(new GridCodeCheckResult("frt-recovery", clause, RuleOutcome.NotEvaluated, null, null, null, "pu",
                            "a synchronous unit's electrical power is not in the RMS traces, so its active-power recovery is not evaluated"));
                        break;
                    }
                    if (!gb)
                    {
                        results.Add(new GridCodeCheckResult("frt-recovery", clause, RuleOutcome.NotEvaluated, null, null, null, "pu",
                            "RfG leaves the recovery magnitude and time to the TSO; no value is shipped"));
                        break;
                    }
                    if (!SubtransientInit.IsConverterUnit(gen, Data()[request.UnitIndex]))
                    {
                        results.Add(new GridCodeCheckResult("frt-recovery", clause, RuleOutcome.NotEvaluated, null, null, null, "pu",
                            "a synchronous unit's electrical power is not in the RMS traces, so its active-power recovery is not evaluated"));
                        break;
                    }
                    FrtRun run = GridCodeChecks.RunFrt(model, Pf(), Data(), request.PccBus, profile, fr.Fault, request.StepSeconds, fr.DurationSeconds);
                    runs.Add(run);
                    results.Add(GridCodeChecks.FrtRecovery(run, model, Data(), request.UnitIndex, clause,
                        req.Number("recovery.voltagePu"), req.Number("recovery.delaySeconds"), req.Number("recovery.fraction"), PreFault(request, gb, Pf())));
                    break;
                }
                case FrtRetainedVoltageRequest:
                    results.Add(GridCodeChecks.FrtRetainedVoltage(model, Pf(), request.PccBus, profile));
                    break;
                case FrtUvSettingsRequest uv:
                    results.AddRange(GridCodeChecks.FrtUvSettings(profile, uv.Stages));
                    break;
                case QCapabilityRequest q:
                    results.AddRange(QCapability(request, req, gb, gen, q, notes));
                    break;
                case LfsmRequest l:
                    results.Add(Lfsm(request, req, gb, gen, mbase, l, Pf, Data));
                    break;
                case FsmStepRequest f:
                    results.Add(Fsm(request, req, gb, gen, mbase, f, Data, fsmBlock));
                    break;
                case RocofSettingsRequest rs:
                {
                    (double limit, double window, string clause) = RocofLimit(request, req, gb, rs.LimitHzPerSecond, rs.WindowSeconds, rs.Source);
                    results.AddRange(GridCodeChecks.RocofSettings(limit, window, clause, rs.Stages));
                    break;
                }
                case RocofEventRequest re:
                {
                    (double limit, double window, string clause) = RocofLimit(request, req, gb, re.LimitHzPerSecond, re.WindowSeconds, re.Source);
                    results.Add(GridCodeChecks.RocofEvent(model, Pf(), Data(), request.UnitIndex, request.PccBus, re.Events, limit, window,
                        request.StepSeconds, re.DurationSeconds, clause));
                    break;
                }
                case FrequencyRelaysRequest fq:
                {
                    string area = gb ? "GB" : request.Area ?? throw new InvalidDataException("eu-rfg: frequency-relays needs area (CE, Nordic, GB, IE-NI or Baltic).");
                    var bands = req.FrequencyBands.Where(b => b.Area == area).ToArray();
                    if (bands.Length == 0) throw new InvalidDataException($"area '{area}' is not a row of {req.FrequencyBandsClause}.");
                    results.AddRange(GridCodeChecks.FrequencyRelays(bands, req.FrequencyBandsClause, req.NoTripLowHz, req.NoTripHighHz, req.NoTripClause, fq.Stages));
                    break;
                }
                case FfciRequest ff:
                {
                    FfciRequirement fr = Ext().Ffci;
                    FrtRun run = GridCodeChecks.RunFrt(model, Pf(), Data(), request.PccBus, profile, ff.Fault, request.StepSeconds, ff.DurationSeconds);
                    runs.Add(run);
                    results.Add(FfciCheck.Run(fr, run, model, Data(), request.UnitIndex, request.ModuleType, request.Technology));
                    break;
                }
                case LongFaultRequest lf:
                    results.AddRange(LongFault(request, Ext(), lf, Pf, Data));
                    break;
                case AvrStepRequest av:
                {
                    AvrRequirement ar = Ext().Avr;
                    RmsExciter? exciter = request.Exciters is { } ex && request.UnitIndex < ex.Count ? ex[request.UnitIndex] : null;
                    SubtransientInit.MachineData? machine = request.Data is { } d ? d[request.UnitIndex] : null;
                    results.AddRange(StepResponseChecks.Avr(ar, request.ModuleType, request.Technology, machine, exciter, av.StepSeconds ?? request.StepSeconds, av.DurationSeconds));
                    break;
                }
                case VctrlStepRequest vc:
                {
                    VctrlRequirement vr = Ext().Vctrl;
                    double qmax = vc.QmaxMvar ?? (double.IsFinite(gen.Qmax) ? gen.Qmax : throw new InvalidDataException("vctrl-step needs qmaxMvar (the unit's Qmax is not finite)."));
                    double qmin = vc.QminMvar ?? (double.IsFinite(gen.Qmin) ? gen.Qmin : throw new InvalidDataException("vctrl-step needs qminMvar (the unit's Qmin is not finite)."));
                    if (!(qmax > qmin)) throw new InvalidDataException("vctrl-step needs qmaxMvar above qminMvar.");
                    if (vc.Events.Count == 0) throw new InvalidDataException("vctrl-step needs the events that make the voltage step.");
                    results.AddRange(StepResponseChecks.Vctrl(vr, request.Technology, model, Pf(), Data(), request.UnitIndex, request.PccBus, vc.Events,
                        request.StepSeconds, vc.DurationSeconds, vc.LeadToLag, qmax, qmin));
                    break;
                }
                default:
                    throw new InvalidDataException($"unknown check '{c.Id}'.");
            }
        }

        int pass = results.Count(r => r.Outcome == RuleOutcome.Pass), fail = results.Count(r => r.Outcome == RuleOutcome.Fail);
        int ne = results.Count(r => r.Outcome is RuleOutcome.NotEvaluated or RuleOutcome.OutOfScope), na = results.Count(r => r.Outcome == RuleOutcome.NotApplicable);
        string verdict = fail > 0 ? RuleSetEngine.NonCompliant : (ne > 0 || pass == 0) ? RuleSetEngine.Incomplete : RuleSetEngine.Compliant;
        return new GridCodeResult(request.Label, req, request, profile, profileSource, results, verdict, pass, fail, ne, na, notes, runs);
    }

    /// <summary>RC5 WP22: <c>frt-long</c>, one row per dip duration (see <see cref="LongFaultCheck"/>).</summary>
    private static List<GridCodeCheckResult> LongFault(GridCodeRequest request, GridCodeExtRequirementSet ext, LongFaultRequest lf,
        Func<PowerFlowResult> pf, Func<IReadOnlyList<SubtransientInit.MachineData>> data)
    {
        const string id = LongFaultCheck.Id;
        var rows = new List<GridCodeCheckResult>();
        if (request.Technology == GridCodeTechnology.Hvdc)
        {
            rows.Add(new GridCodeCheckResult(id, ext.HvdcLongClause, RuleOutcome.NotEvaluated, null, null, null, "pu", ext.HvdcLongText));
            return rows;
        }
        if (request.Technology == GridCodeTechnology.DcPpm)
        {
            rows.Add(new GridCodeCheckResult(id, "ECC.6.3.15.9", RuleOutcome.NotEvaluated, null, null, null, "pu",
                "ECC.6.3.15.9.2 places type C and D synchronous modules, power park modules and OTSDUW plant in its two profiles; a DC-connected power park module is in neither, so no profile is applied"));
            return rows;
        }
        LongFaultProfile? p = LongFaultCheck.ProfileFor(ext, request.ModuleType, request.Technology);
        if (p is null)
        {
            rows.Add(new GridCodeCheckResult(id, "ECC.6.3.15.9.2", RuleOutcome.NotApplicable, null, null, null, "pu", "ECC.6.3.15.9.2 applies to type C and D modules only"));
            return rows;
        }
        IReadOnlyList<double> durations = lf.DurationsSeconds
            ?? p.Corners.Select(c => c.DurationSeconds).Where(d => d > 0.14).Distinct().ToArray();
        if (durations.Count == 0) throw new InvalidDataException("frt-long: give at least one duration.");
        FrtPreFault pre = GridCodeChecks.GbPreFault(request.Model, pf(), request.UnitIndex, request.PccBus, "ECC.6.3.15.8");
        foreach (double d in durations)
        {
            if (!(d >= 0.14 && d <= ext.LongFaultMaxDuration.Value))
                throw new InvalidDataException($"frt-long: a duration of {R(d)} s is outside 0.14 to {R(ext.LongFaultMaxDuration.Value)} s ({ext.LongFaultMaxDuration.Clause}).");
            string label = $"dip of {R(d)} s";
            if (p.LabelledVoltage(d) is not double u)
            {
                rows.Add(new GridCodeCheckResult(id, p.Clause + "(i)", RuleOutcome.NotEvaluated, null, null, null, "pu",
                    $"{p.Figure} labels no voltage for a dip of {R(d)} s (it lies on a sloped segment of a figure marked NOT TO SCALE); give a labelled corner duration") { Case = label });
                continue;
            }
            LongFaultRun run = LongFaultCheck.RunStaged(request.Model, pf(), data(), request.PccBus, lf.BusId ?? request.PccBus, lf.AtSeconds,
                LongFaultCheck.WholeStepDip(d, request.StepSeconds), u, lf.StageSeconds, request.StepSeconds, lf.AfterSeconds, lf.MaxPasses);
            rows.Add(LongFaultCheck.Judge(run, p, request.Model, pf(), data(), request.UnitIndex, pre, d));
        }
        return rows;
    }

    /// <summary>The pre-fault conditions an FRT check is judged under: GB ECC.6.3.15.8 (rated MW, 1.0 pu at the PCC); under
    /// RfG the TSO sets them, so the result carries a note only.</summary>
    private static FrtPreFault PreFault(GridCodeRequest request, bool gb, PowerFlowResult pf) => gb
        ? GridCodeChecks.GbPreFault(request.Model, pf, request.UnitIndex, request.PccBus, "ECC.6.3.15.8")
        : new FrtPreFault("RfG Art. 14(3)(a)", null, new[] { "RfG leaves the pre-fault conditions to the TSO (Art. 14(3)(a)); the study runs at the case's operating point and does not check them" });

    private static (double Limit, double Window, string Clause) RocofLimit(GridCodeRequest request, GridCodeRequirementSet req, bool gb, double? limit, double? window, string? source)
    {
        if (gb)
        {
            if (limit is not null) throw new InvalidDataException("gb-ecc fixes the RoCoF withstand (ECC.6.3.13); limitHzPerSecond is for eu-rfg only.");
            RocofRequirement r = req.Rocof.FirstOrDefault(x => x.Technologies.Contains(request.Technology, StringComparer.Ordinal))
                ?? throw new InvalidDataException($"no GB RoCoF requirement for technology '{request.Technology}'.");
            return (r.LimitHzPerSecond, r.WindowSeconds, r.Clause);
        }
        if (limit is not double l || window is not double w || string.IsNullOrWhiteSpace(source))
            throw new InvalidDataException($"eu-rfg: the RoCoF withstand is the TSO's value ({req.RocofClause}): give limitHzPerSecond, windowSeconds and its source.");
        if (!(l > 0.0) || !(w > 0.0) || !double.IsFinite(l) || !double.IsFinite(w)) throw new InvalidDataException("limitHzPerSecond and windowSeconds must be positive and finite.");
        return (l, w, $"{req.RocofClause} ({source})");
    }

    private static IEnumerable<GridCodeCheckResult> QCapability(GridCodeRequest request, GridCodeRequirementSet req, bool gb, Generator gen, QCapabilityRequest q, List<string> notes)
    {
        const string id = "q-capability";
        double pmax = q.PmaxMw ?? (double.IsFinite(gen.Pmax) ? gen.Pmax : throw new InvalidDataException("q-capability needs pmaxMw (the unit's Pmax is not finite)."));
        double qmax = q.QmaxMvar ?? (double.IsFinite(gen.Qmax) ? gen.Qmax : throw new InvalidDataException("q-capability needs qmaxMvar (the unit's Qmax is not finite)."));
        double qmin = q.QminMvar ?? (double.IsFinite(gen.Qmin) ? gen.Qmin : throw new InvalidDataException("q-capability needs qminMvar (the unit's Qmin is not finite)."));
        if (request.PlantBuses.Count == 0) throw new InvalidDataException("q-capability needs plantBuses.");
        string mt = request.ModuleType.ToString();
        var tests = new List<(double, double, double)>();
        string clause;
        if (gb)
        {
            GbReactiveEntry? e = req.GbReactive.FirstOrDefault(x => x.ModuleTypes.Contains(mt, StringComparer.Ordinal) && x.Technologies.Contains(request.Technology, StringComparer.Ordinal));
            if (e is not null)
            {
                if (q.UqProfile is not null) throw new InvalidDataException($"uqProfile is not taken here: {e.Clause} fixes the reactive requirement.");
                foreach (double v in e.TestVoltagesPu) tests.Add((v, e.QPerPmax * pmax, -(e.QPerPmax * pmax)));
                clause = e.Clause;
                if (e.FigureRead) notes.Add($"{e.Clause}: the {R(e.PowerFactor)} power factor range for 0.95 to 1.05 pu is read from the figure");
            }
            else if (q.UqProfile is UqProfile up)
            {
                foreach (UqCorner c in up.Corners) tests.Add((c.VoltagePu, c.QMaxPerPmax * pmax, c.QMinPerPmax * pmax));
                clause = $"{req.ReactiveNotTranscribedClause} (user profile: {up.Citation})";
            }
            else
            {
                return new[] { new GridCodeCheckResult(id, req.ReactiveNotTranscribedClause ?? "", RuleOutcome.NotEvaluated, null, null, null, "Mvar",
                    req.ReactiveNotTranscribed ?? "figure not transcribed") };
            }
        }
        else
        {
            if (q.UqProfile is not UqProfile up)
                return new[] { new GridCodeCheckResult(id, "RfG Art. 17(2)(a), 18(2)(b), 21(3)(b)", RuleOutcome.NotEvaluated, null, null, null, "Mvar",
                    "the reactive requirement is the TSO's U-Q/Pmax profile: give uqProfile with its citation") };
            clause = $"RfG, the TSO's profile ({up.Citation})";
            if (request.ModuleType != ModuleType.B)
            {
                ReactiveLimitTable table = req.ReactiveLimits.FirstOrDefault(t => t.Technologies.Contains(request.Technology, StringComparer.Ordinal))
                    ?? throw new InvalidDataException($"no RfG reactive limit table for technology '{request.Technology}'.");
                string area = request.Area ?? throw new InvalidDataException("eu-rfg: q-capability needs area (CE, Nordic, GB, IE-NI or Baltic).");
                IReadOnlyList<string> v = RfgRanges.ValidateUqProfile(up, table, area);
                if (v.Count > 0) throw new InvalidDataException(string.Join("; ", v));
                clause = $"{table.Clause}, the TSO's profile ({up.Citation})";
            }
            foreach (UqCorner c in up.Corners) tests.Add((c.VoltagePu, c.QMaxPerPmax * pmax, c.QMinPerPmax * pmax));
        }
        return GridCodeChecks.QCapability(request.Model, request.UnitIndex, request.PccBus, request.PlantBuses, pmax, qmax, qmin, tests, clause);
    }

    private static double Pm0(Generator gen, double mbase, double? given) => given ?? gen.Pg / mbase;

    private static GovernorData Governor(GridCodeRequest request, Func<IReadOnlyList<SubtransientInit.MachineData>> data) =>
        data()[request.UnitIndex].Governor ?? throw new InvalidDataException("the unit's dynamic data carries no governor, so its frequency response cannot be injected.");

    private static GridCodeCheckResult Lfsm(GridCodeRequest request, GridCodeRequirementSet req, bool gb, Generator gen, double mbase, LfsmRequest l,
        Func<PowerFlowResult> pf, Func<IReadOnlyList<SubtransientInit.MachineData>> data)
    {
        string block = l.Over ? "lfsmO" : "lfsmU";
        string id = l.Over ? "lfsm-o" : "lfsm-u";
        string clause = req.Clause(block);
        if (!req.Applies(block, request.ModuleType))
            return new GridCodeCheckResult(id, clause, RuleOutcome.NotApplicable, null, null, null, "pu", $"{clause} applies to type C and D modules only");
        double threshold, droop;
        double fn = request.Model.FrequencyHz;
        bool prefPmax;
        double? pmaxMw = l.PmaxMw ?? (double.IsFinite(gen.Pmax) ? gen.Pmax : null);
        if (gb)
        {
            if (l.ThresholdHz is not null || l.Droop is not null || l.Pref is not null)
                throw new InvalidDataException($"gb-ecc fixes the {id} threshold, droop and reference ({clause}); they are for eu-rfg only.");
            threshold = l.Over && request.FsmMode ? req.Number("lfsmO.thresholdFsmHz") : req.Number(block + ".thresholdHz");
            droop = req.Number(block + ".droop");
            prefPmax = false;
        }
        else
        {
            if (l.ThresholdHz is not double th || l.Droop is not double dr || l.Pref is null)
                throw new InvalidDataException($"eu-rfg: {id} needs thresholdHz, droop and pref (the TSO's choices, {clause}).");
            var v = RfgRanges.Range("thresholdHz", th, req.Number(block + ".thresholdMinHz"), req.Number(block + ".thresholdMaxHz"), "Hz", clause)
                .Concat(RfgRanges.Range("droop", dr, req.Number(block + ".droopMin"), req.Number(block + ".droopMax"), "", clause)).ToList();
            if (l.Pref is not ("output" or "pmax")) v.Add("pref must be output or pmax");
            if (v.Count > 0) throw new InvalidDataException(string.Join("; ", v));
            threshold = th; droop = dr; prefPmax = l.Pref == "pmax";
        }
        // GB LFSM-O: the response must be achieved within responseSeconds (ECC.6.3.7.1.2(iii)), so it is read then and no
        // other read time is taken. The GB requirement set gives no response time for LFSM-U; it is read at tEndSeconds.
        double? gbRead = gb && l.Over ? req.Number("lfsmO.responseSeconds") : null;
        var notes = new List<string>();
        if (gb && !l.Over) notes.Add($"{clause} as shipped gives no response time for LFSM-U; the response is read at tEndSeconds");
        if (l.Events is { Count: > 0 } events)
        {
            if (gbRead is double rs)
            {
                double first = events.Min(e => e.AtSeconds);
                if (Math.Abs(l.TEndSeconds - (first + rs)) > 1e-9 * Math.Max(1.0, first + rs))
                    throw new InvalidDataException($"gb-ecc reads the LFSM-O response {R(rs)} s after the event ({clause}): with the first event at {R(first)} s, tEndSeconds must be {R(first + rs)}.");
            }
            double? prefSys = prefPmax ? (pmaxMw ?? throw new InvalidDataException($"{id}: pref pmax needs pmaxMw.")) / request.Model.BaseMva : null;
            GridCodeCheckResult net = GridCodeChecks.LfsmNetwork(l.Over, request.Model, pf(), data(), request.UnitIndex, request.PccBus, events, threshold, droop, prefSys,
                request.StepSeconds, l.TEndSeconds, clause);
            return notes.Count == 0 ? net : net with { Notes = net.Notes.Concat(notes).ToArray() };
        }
        if (gbRead is double r && l.TEndSeconds != r)
            throw new InvalidDataException($"gb-ecc reads the LFSM-O response at {R(r)} s after the step ({clause}); tEndSeconds must be {R(r)}.");
        double pm0 = Pm0(gen, mbase, l.Pm0Pu);
        double pref = prefPmax ? (pmaxMw ?? throw new InvalidDataException($"{id}: pref pmax needs pmaxMw.")) / mbase : pm0;
        return GridCodeChecks.Lfsm(l.Over, Governor(request, data), pm0, l.FrequencyHz, fn, threshold, droop, pref, l.StepSeconds, l.TEndSeconds, clause, notes);
    }

    private static GridCodeCheckResult Fsm(GridCodeRequest request, GridCodeRequirementSet req, bool gb, Generator gen, double mbase, FsmStepRequest f,
        Func<IReadOnlyList<SubtransientInit.MachineData>> data, string block)
    {
        const string id = "fsm-step";
        string clause = req.Clause(block);
        if (!req.Applies(block, request.ModuleType))
            return new GridCodeCheckResult(id, clause, RuleOutcome.NotApplicable, null, null, null, "pu", $"{clause} applies to type C and D modules only");
        double pmaxMw = f.PmaxMw ?? (double.IsFinite(gen.Pmax) ? gen.Pmax : throw new InvalidDataException("fsm-step needs pmaxMw (the unit's Pmax is not finite)."));
        double t1, t2, frac;
        var v = new List<string>();
        if (gb)
        {
            if (f.InsensitivityHz is not null || f.DeadbandHz is not null) throw new InvalidDataException("gb-ecc fixes the FSM insensitivity and deadband; they are for eu-rfg only.");
            frac = f.Dp1PerPmax ?? req.Number("fsm.dp1PerPmax");
            if (frac != req.Number("fsm.dp1PerPmax")) v.Add($"dp1PerPmax must be {R(req.Number("fsm.dp1PerPmax"))} ({clause})");
            double t1Max = f.WithInertia ? req.Number("fsm.t1WithInertiaMaxSeconds") : req.Number("fsm.t1WithoutInertiaMaxSeconds");
            t1 = f.T1Seconds ?? t1Max;
            if (!(t1 >= 0.0 && t1 <= t1Max)) v.Add($"t1 = {R(t1)} s must lie in 0 to {R(t1Max)} s ({clause})");
            t2 = f.T2Seconds ?? req.Number("fsm.t2Seconds");
            if (t2 != req.Number("fsm.t2Seconds")) v.Add($"t2 must be {R(req.Number("fsm.t2Seconds"))} s ({clause})");
            if (f.Droop is double dr) v.AddRange(RfgRanges.Range("droop", dr, req.Number("fsm.droopMin"), req.Number("fsm.droopMax"), "", clause).Select(s => s.Replace("RfG range", "GB range", StringComparison.Ordinal)));
        }
        else
        {
            if (f.Dp1PerPmax is not double fr || f.T1Seconds is not double a || f.T2Seconds is not double b)
                throw new InvalidDataException($"eu-rfg: fsm-step needs dp1PerPmax, t1Seconds and t2Seconds (the TSO's choices, {clause}).");
            frac = fr; t1 = a; t2 = b;
            v.AddRange(RfgRanges.Range("dp1PerPmax", fr, req.Number("fsm.dp1PerPmaxMin"), req.Number("fsm.dp1PerPmaxMax"), "", clause));
            if (f.WithInertia) v.AddRange(RfgRanges.Range("t1Seconds", a, 0.0, req.Number("fsm.t1WithInertiaMaxSeconds"), "s", clause));
            v.AddRange(RfgRanges.Range("t2Seconds", b, 0.0, req.Number("fsm.t2MaxSeconds"), "s", clause));
            if (f.InsensitivityHz is double ins) v.AddRange(RfgRanges.Range("insensitivityHz", ins, req.Number("fsm.insensitivityMinHz"), req.Number("fsm.insensitivityMaxHz"), "Hz", clause));
            if (f.DeadbandHz is double db) v.AddRange(RfgRanges.Range("deadbandHz", db, req.Number("fsm.deadbandMinHz"), req.Number("fsm.deadbandMaxHz"), "Hz", clause));
            if (f.Droop is double dr) v.AddRange(RfgRanges.Range("droop", dr, req.Number("fsm.droopMin"), req.Number("fsm.droopMax"), "", clause));
        }
        // A governor at droop s gives dP/Pmax = |df| / (fn s) in steady state, so a step smaller than dP1 fn s cannot reach dP1
        // even from a compliant governor. The step is sized for the given droop (the unit's setting), else the code's
        // largest droop.
        double sizingDroop = f.Droop ?? req.Number("fsm.droopMax");
        double minStep = frac * request.Model.FrequencyHz * sizingDroop;
        if (Math.Abs(f.FrequencyHz - request.Model.FrequencyHz) < minStep * (1.0 - 1e-12))
            v.Add($"the step to {R(f.FrequencyHz)} Hz is {R(Math.Abs(f.FrequencyHz - request.Model.FrequencyHz))} Hz from nominal; a governor at droop {R(sizingDroop)} needs at least dP1 fn s = {R(minStep)} Hz to reach dP1 ({clause})");
        if (v.Count > 0) throw new InvalidDataException(string.Join("; ", v));
        double dp1 = frac * pmaxMw / mbase;
        double pm0 = Pm0(gen, mbase, f.Pm0Pu);
        return GridCodeChecks.FsmStep(Governor(request, data), pm0, f.FrequencyHz, request.Model.FrequencyHz, t1, t2, dp1, f.StepSeconds, f.TEndSeconds ?? t2, clause);
    }

    // ------------------------------------------------------------------------------------------------ writer

    /// <summary>The <c>gridsim-gridcode/1</c> document: invariant culture, shortest round-trip numbers, a fixed key order.
    /// <paramref name="provenance"/> (string pairs, written in the order given) is placed first when given.</summary>
    public static string Write(GridCodeResult r, IReadOnlyList<KeyValuePair<string, string>>? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            if (provenance is not null)
            {
                w.WriteStartObject("provenance");
                foreach (KeyValuePair<string, string> kv in provenance) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
            }
            w.WriteString("label", r.Label);
            w.WriteString("code", r.Request.Code);
            w.WriteString("requirementSet", r.Requirements.Id);
            w.WriteString("title", r.Requirements.Title);
            w.WriteString("moduleType", r.Request.ModuleType.ToString());
            w.WriteString("technology", r.Request.Technology);
            Num(w, "connectionKv", r.Request.ConnectionKv);
            w.WriteNumber("pccBus", r.Request.PccBus);
            Generator gen = r.Request.Model.Generators[r.Request.UnitIndex];
            w.WriteStartObject("unit");
            w.WriteNumber("index", r.Request.UnitIndex);
            w.WriteNumber("bus", gen.Bus);
            if (gen.Name is not null) w.WriteString("name", gen.Name); else w.WriteNull("name");
            w.WriteEndObject();
            if (r.Profile is FrtProfile p)
            {
                w.WriteStartObject("frtProfile");
                w.WriteString("clause", p.Clause);
                w.WriteString("source", r.ProfileSource);
                w.WriteString("shape", r.Requirements.FrtShape);
                Num(w, "uret", p.Uret); Num(w, "uclear", p.Uclear); Num(w, "urec1", p.Urec1); Num(w, "urec2", p.Urec2);
                Num(w, "tclear", p.Tclear); Num(w, "trec1", p.Trec1); Num(w, "trec2", p.Trec2); Num(w, "trec3", p.Trec3);
                w.WriteEndObject();
            }
            else w.WriteNull("frtProfile");
            w.WriteStartArray("checks");
            foreach (GridCodeCheckResult c in r.Checks)
            {
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteString("case", c.Case);
                w.WriteString("clause", c.Clause);
                w.WriteString("outcome", c.Outcome.ToString());
                w.WriteString("status", c.Status);
                Opt(w, "value", c.Value); Opt(w, "limit", c.Limit); Opt(w, "margin", c.Margin);
                w.WriteString("unit", c.Unit);
                Opt(w, "atSeconds", c.AtSeconds);
                w.WriteString("reason", c.Reason);
                w.WriteStartArray("notes");
                foreach (string n in c.Notes) w.WriteStringValue(n);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("counts");
            w.WriteNumber("passed", r.Passed); w.WriteNumber("failed", r.Failed); w.WriteNumber("notEvaluated", r.NotEvaluated); w.WriteNumber("notApplicable", r.NotApplicable);
            w.WriteEndObject();
            w.WriteString("verdict", r.Verdict);
            w.WriteStartArray("notes");
            foreach (string n in r.Notes) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Num(Utf8JsonWriter w, string name, double v)
    {
        if (!double.IsFinite(v)) throw new InvalidOperationException($"'{name}' is not finite.");
        w.WriteNumber(name, v);
    }

    private static void Opt(Utf8JsonWriter w, string name, double? v)
    {
        if (v is double d && double.IsFinite(d)) w.WriteNumber(name, d);
        else w.WriteNull(name);
    }
}
