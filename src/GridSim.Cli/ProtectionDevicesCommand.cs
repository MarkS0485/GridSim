using System.Globalization;
using System.Text.Json;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;
using GridSim.Core.Protection.Coordination;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.Workflow;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-07: the verbs <c>devices-check</c>, <c>fault-line</c>, <c>sc-trace</c> and <c>protection-audit</c> over placed
/// protection devices (<c>gridsim-devices/1</c>, GridSim.Core/Protection/Devices and /Workflow). A positional case (or, for
/// <c>protection-audit</c>, a strict <c>gridsim-protection-audit/1</c> study file naming the case and the device file),
/// strict flags, results through the shared writer. All arithmetic is in Core. Exit codes: 0 done (and, for the audit, every
/// pair coordinated); 1 the case, devices or curve data cannot be loaded or are invalid; 2 usage; 3 the study ran and a
/// pair is not coordinated or a device would not operate at its minimum fault.
/// </summary>
internal static class ProtectionDevicesCommand
{
    public const string AuditFormat = "gridsim-protection-audit/1";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v) => double.IsFinite(v) ? v.ToString("0.####", Inv) : (double.IsNaN(v) ? "n/a" : (v > 0 ? "inf" : "-inf"));

    private sealed class UsageException(string message) : Exception(message);

    private sealed record Args(string? Positional, Dictionary<string, string> Flags)
    {
        public string? Str(string n) => Flags.TryGetValue(n, out string? v) ? v : null;
        public double Num(string n, double d)
        {
            if (!Flags.TryGetValue(n, out string? v)) return d;
            if (double.TryParse(v, NumberStyles.Float, Inv, out double x) && double.IsFinite(x)) return x;
            throw new UsageException($"{n} expected a finite number but got '{v}'");
        }
    }

    private static Args Parse(string verb, List<string> a, params string[] allowed)
    {
        string? pos = null;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (!t.StartsWith("--", StringComparison.Ordinal))
            {
                if (pos is not null) throw new UsageException($"{verb} takes one positional argument but got: {pos} {t}.");
                pos = t; continue;
            }
            if (Array.IndexOf(allowed, t) < 0) throw new UsageException($"{verb} does not use {t}. {verb} accepts: {string.Join(", ", allowed)}.");
            if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{t} expected a value but none was given");
            if (!flags.TryAdd(t, a[++i])) throw new UsageException($"{t} was given twice");
        }
        return new Args(pos, flags);
    }

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        string verb = a[0].ToLowerInvariant();
        try
        {
            return verb switch
            {
                "devices-check" => DevicesCheck(Parse(verb, a, "--devices", "--curves"), load),
                "fault-line" => FaultLine(Parse(verb, a, "--devices", "--curves", "--branch", "--fractions", "--objective", "--cti"), load),
                "sc-trace" => Trace(Parse(verb, a, "--devices", "--curves", "--bus", "--branch", "--fraction", "--objective", "--cti"), load),
                "protection-audit" => Audit(Parse(verb, a, "--curves"), load),
                _ => throw new UsageException($"unknown verb '{verb}'"),
            };
        }
        catch (UsageException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.Usage; }
        catch (StudyCaseException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }
        catch (ArgumentException ex) when (ex is not UnknownCaseException) { Console.Error.WriteLine($"error: {verb}: {CliExit.UserMessage(ex)}"); return CliExit.Usage; }
    }

    private static CurveLibrary Curves(string? dir)
    {
        if (dir is not null)
        {
            if (!Directory.Exists(dir)) throw new StudyCaseException($"curve directory '{dir}' does not exist.");
            return CurveLibrary.LoadDirectory(dir);
        }
        string? d = AppContext.BaseDirectory;
        for (int up = 0; up < 8 && d is not null; up++, d = Path.GetDirectoryName(d))
        {
            string cand = Path.Combine(d, "data", "protection-curves");
            if (Directory.Exists(cand)) return CurveLibrary.LoadDirectory(cand);
        }
        return new CurveLibrary();   // built-in equation families only
    }

    private static BoundDeviceSet BindFile(string devicesPath, GridModel m, string? curves)
    {
        if (!File.Exists(devicesPath)) throw new StudyCaseException($"devices file '{devicesPath}' does not exist.");
        return DeviceSetValidator.Bind(DeviceSet.Parse(File.ReadAllBytes(devicesPath)), m, Curves(curves));
    }

    private static GridModel? LoadCase(string verb, string? path, Func<string, GridModel> load, out int exit)
    {
        exit = CliExit.Ok;
        if (path is null) { Console.Error.WriteLine($"usage: gridsim {verb} <case> ... (see 'gridsim {verb} --help')"); exit = CliExit.Usage; return null; }
        try { return load(path); }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            Console.Error.WriteLine($"error loading case '{path}': {ex.Message}");
            exit = CliExit.CaseLoadFailure(ex);
            return null;
        }
    }

    private static ShortCircuitObjective Objective(Args a) => (a.Str("--objective") ?? "max").ToLowerInvariant() switch
    {
        "max" or "maximum" => ShortCircuitObjective.Maximum,
        "min" or "minimum" => ShortCircuitObjective.Minimum,
        var o => throw new UsageException($"--objective expected max or min but got '{o}'"),
    };

    private static double Cti(Args a)
    {
        double c = a.Num("--cti", OvercurrentCoordination.DefaultCoordinationTimeIntervalS);
        if (c < 0.0) throw new UsageException($"--cti expected seconds >= 0 but got '{c}'");
        return c;
    }

    // ---- devices-check ----------------------------------------------------------------------------------------------

    private static int DevicesCheck(Args a, Func<string, GridModel> load)
    {
        string dev = a.Str("--devices") ?? throw new UsageException("--devices <set.json> is required");
        GridModel? m = LoadCase("devices-check", a.Positional, load, out int exit);
        if (m is null) return exit;
        BoundDeviceSet b = BindFile(dev, m, a.Str("--curves"));
        Console.WriteLine($"devices-check - {m.Name}: {b.Devices.Count} device(s) valid and bound ({DeviceSet.FormatVersion}); {b.Library.Tabulated.Count} tabulated curve(s) loaded.");
        CliResult? rep = CliOutput.Report;
        rep?.Set("case", m.Name).Set("format", DeviceSet.FormatVersion).Set("devices", b.Devices.Count).Set("tabulated_curves", b.Library.Tabulated.Count);
        CliResultTable? t = rep?.Table("devices", "device", "type", "function", "branch", "from", "to", "end", "in_service");
        foreach (PlacedDevice d in b.Devices)
        {
            Console.WriteLine($"  {d.Id,-10} {d.Type.ToString().ToLowerInvariant(),-8} {(d.Function == "" ? "-" : d.Function),-4} branch {d.Location.BranchIndex} ({d.Location.From}-{d.Location.To}) {d.Location.End}{(b.OutOfService(d) ? "  [branch out of service]" : "")}");
            t?.Row(d.Id, d.Type.ToString().ToLowerInvariant(), d.Function, d.Location.BranchIndex, d.Location.From, d.Location.To, d.Location.End, !b.OutOfService(d));
        }
        rep?.Complete();
        return CliExit.Ok;
    }

    // ---- sc-trace and fault-line ------------------------------------------------------------------------------------

    private static int Trace(Args a, Func<string, GridModel> load)
    {
        string dev = a.Str("--devices") ?? throw new UsageException("--devices <set.json> is required");
        bool hasBus = a.Str("--bus") is not null, hasBr = a.Str("--branch") is not null;
        if (hasBus == hasBr) throw new UsageException("give exactly one of --bus <id> or --branch <i> --fraction <d>");
        if (hasBus && a.Str("--fraction") is not null) throw new UsageException("--fraction goes with --branch");
        FaultSpec fault = hasBus ? FaultSpec.AtBus((int)Whole(a, "--bus")) : FaultSpec.AlongBranch((int)Whole(a, "--branch"), a.Num("--fraction", 0.5));
        GridModel? m = LoadCase("sc-trace", a.Positional, load, out int exit);
        if (m is null) return exit;
        BoundDeviceSet b = BindFile(dev, m, a.Str("--curves"));
        TraceResult t = ShortCircuitTrace.Run(b, fault, Objective(a), Cti(a));
        CliResult? rep = CliOutput.Report;
        rep?.Set("case", m.Name).Set("fault", t.Label).Set("fault_bus", t.FaultBusId).Set("objective", t.Objective.ToString().ToLowerInvariant());
        if (t.NotEvaluated is not null)
        {
            Console.WriteLine($"sc-trace - {m.Name}: fault at {t.Label} not evaluated: {t.NotEvaluated}");
            rep?.Set("not_evaluated", t.NotEvaluated);
            rep?.Complete();
            return CliExit.Verdict;
        }
        PrintTrace(m.Name, t);
        FillTrace(rep, t);
        rep?.Complete();
        return CliExit.Ok;
    }

    private static long Whole(Args a, string n)
    {
        string v = a.Str(n)!;
        if (!long.TryParse(v, NumberStyles.Integer, Inv, out long x) || x < int.MinValue || x > int.MaxValue) throw new UsageException($"{n} expected a whole number but got '{v}'");
        return x;
    }

    private static int FaultLine(Args a, Func<string, GridModel> load)
    {
        string dev = a.Str("--devices") ?? throw new UsageException("--devices <set.json> is required");
        int branch = (int)Whole(a, "--branch");
        var fractions = new List<double>();
        foreach (string p in (a.Str("--fractions") ?? "0,0.25,0.5,0.75,1").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(p, NumberStyles.Float, Inv, out double d) || !(d >= 0.0 && d <= 1.0)) throw new UsageException($"--fractions expected numbers in [0, 1] but got '{p}'");
            fractions.Add(d);
        }
        if (fractions.Count == 0) throw new UsageException("--fractions expected at least one number");
        fractions.Sort();
        GridModel? m = LoadCase("fault-line", a.Positional, load, out int exit);
        if (m is null) return exit;
        if (branch < 0 || branch >= m.Branches.Count) throw new UsageException($"--branch {branch}: case '{m.Name}' has branches 0..{m.Branches.Count - 1}.");
        BoundDeviceSet b = BindFile(dev, m, a.Str("--curves"));
        ShortCircuitObjective obj = Objective(a);
        double cti = Cti(a);
        CliResult? rep = CliOutput.Report;
        rep?.Set("case", m.Name).Set("branch", branch).Set("objective", obj.ToString().ToLowerInvariant());
        CliResultTable? tab = rep?.Table("fault_line", "fraction", "fault_bus", "fault_current_ka", "first_device", "first_time_s", "second_device", "worst_pair_margin_s", "coordinated");
        int bad = 0;
        foreach (double d in fractions)
        {
            TraceResult t = ShortCircuitTrace.Run(b, FaultSpec.AlongBranch(branch, d), obj, cti);
            if (t.NotEvaluated is not null)
            {
                Console.WriteLine($"fault-line branch {branch} d = {F(d)}: not evaluated: {t.NotEvaluated}");
                tab?.Row(d, null, null, null, null, null, null, null);
                continue;
            }
            Iec60909.Duty duty = Iec60909.ThreePhase(Faulted(m, branch, d), t.FaultBusId, obj);
            double worst = t.Pairs.Where(p => p.Evaluated).Select(p => p.MarginS).DefaultIfEmpty(double.NaN).Min();
            bool coordinated = t.Pairs.Where(p => p.Evaluated).All(p => p.Coordinated);
            if (!coordinated) bad++;
            double? first = t.PrimaryId is null ? null : t.Rows.First(r => r.DeviceId == t.PrimaryId).ClearingTimeS;
            Console.WriteLine($"fault-line branch {branch} d = {F(d)}: Ik'' {F(duty.IkppKa)} kA; first {t.PrimaryId ?? "none"} {(first is double f ? F(f) + " s" : "")}; second {t.BackupId ?? "none"}; worst pair margin {F(worst)} s {(coordinated ? "" : "NOT COORDINATED")}");
            tab?.Row(d, t.FaultBusId, duty.IkppKa, t.PrimaryId, first, t.BackupId, double.IsNaN(worst) ? null : worst, coordinated);
        }
        rep?.Complete();
        return bad > 0 ? CliExit.Verdict : CliExit.Ok;
    }

    private static GridModel Faulted(GridModel m, int branch, double d) => FaultAlongLine.Build(m, branch, d).Model;

    private static void PrintTrace(string name, TraceResult t)
    {
        Console.WriteLine($"sc-trace - {name}: three-phase fault at {t.Label} ({t.Objective.ToString().ToLowerInvariant()}):");
        foreach (DeviceTraceRow r in t.Rows)
        {
            if (!r.Evaluated) Console.WriteLine($"  {r.DeviceId,-10} not evaluated: {r.Reason}");
            else Console.WriteLine($"  {r.DeviceId,-10} {F(r.PrimaryCurrentA)} A primary ({F(r.SecondaryCurrentA)} A secondary)  time {F(r.ClearingTimeS)} s  [{r.Confidence}]{(r.Direction == true ? "  forward" : r.Direction == false ? "  behind" : "  meshed")}{(r.Reason is null ? "" : "  " + r.Reason)}");
        }
        Console.WriteLine($"  sequence: {(t.Sequence.Count == 0 ? "no device operates" : string.Join(" > ", t.Sequence))}; primary {t.PrimaryId ?? "none"}, backup {t.BackupId ?? "none"}");
        foreach (PairRow p in t.Pairs)
            Console.WriteLine($"  pair {p.PrimaryId} -> {p.BackupId} shot {p.Shot}: " + (p.Evaluated ? $"margin {F(p.MarginS)} s vs {F(p.RequiredS)} s {(p.Coordinated ? "ok" : "NOT COORDINATED")}{(p.Reason is null ? "" : " (" + p.Reason + ")")}" : p.Reason));
    }

    private static void FillTrace(CliResult? rep, TraceResult t)
    {
        if (rep is null) return;
        rep.Set("primary", t.PrimaryId).Set("backup", t.BackupId).Set("sequence", string.Join(">", t.Sequence));
        CliResultTable d = rep.Table("devices", "device", "function", "evaluated", "reason", "confidence", "primary_current_a", "secondary_current_a", "direction", "clearing_time_s", "earliest_time_s");
        foreach (DeviceTraceRow r in t.Rows)
            d.Row(r.DeviceId, r.Function, r.Evaluated, r.Reason, r.Confidence.ToString(), Fin(r.PrimaryCurrentA), Fin(r.SecondaryCurrentA),
                r.Direction is bool f ? (f ? "forward" : "behind") : "meshed", Fin(r.ClearingTimeS), Fin(r.EarliestTimeS));
        CliResultTable p = rep.Table("pairs", "primary", "backup", "shot", "primary_time_s", "backup_time_s", "margin_s", "required_s", "evaluated", "coordinated", "reason");
        foreach (PairRow r in t.Pairs) p.Row(r.PrimaryId, r.BackupId, r.Shot, Fin(r.PrimaryTimeS), Fin(r.BackupTimeS), Fin(r.MarginS), r.RequiredS, r.Evaluated, r.Coordinated, r.Reason);
    }

    private static object? Fin(double v) => double.IsFinite(v) ? v : null;   // never operates / not applicable is an empty cell

    // ---- protection-audit -------------------------------------------------------------------------------------------

    private sealed record AuditRequest(string CasePath, string DevicesPath, string? Curves, double Cti, IReadOnlyList<FaultSpec>? Faults);

    private static AuditRequest ReadStudy(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        using JsonDocument doc = StudyCaseJson.Parse(File.ReadAllBytes(path), "The protection-audit study");
        JsonElement root = doc.RootElement;
        StudyCaseJson.Obj(root, "study", ["manifestVersion", "case", "devices", "curves", "coordinationTimeIntervalS", "faults"], ["manifestVersion", "case", "devices"]);
        string v = StudyCaseJson.Str(root.GetProperty("manifestVersion"), "manifestVersion");
        if (v != AuditFormat) throw new StudyCaseException($"manifestVersion must be \"{AuditFormat}\", not \"{v}\".");
        string Rel(string p) => Path.IsPathRooted(p) ? p : Path.Combine(dir, p);
        string casePath = StudyCaseJson.Str(root.GetProperty("case"), "case");
        if (!File.Exists(Rel(casePath)) && !Directory.Exists(Rel(casePath))) { /* a built-in or search-path case name: pass through */ } else casePath = Rel(casePath);
        string devices = Rel(StudyCaseJson.Str(root.GetProperty("devices"), "devices"));
        string? curves = root.TryGetProperty("curves", out JsonElement c) ? Rel(StudyCaseJson.Str(c, "curves")) : null;
        double cti = root.TryGetProperty("coordinationTimeIntervalS", out JsonElement ct) ? StudyCaseJson.Num(ct, "coordinationTimeIntervalS") : OvercurrentCoordination.DefaultCoordinationTimeIntervalS;
        if (!(cti >= 0.0)) throw new StudyCaseException("coordinationTimeIntervalS must be >= 0.");
        List<FaultSpec>? faults = null;
        if (root.TryGetProperty("faults", out JsonElement fl))
        {
            StudyCaseJson.Obj(fl, "faults", ["buses", "alongLine"], []);
            faults = new List<FaultSpec>();
            if (fl.TryGetProperty("buses", out JsonElement bs))
                foreach ((JsonElement e, int i) in StudyCaseJson.Arr(bs, "faults.buses")) faults.Add(FaultSpec.AtBus(StudyCaseJson.Int(e, $"faults.buses[{i}]")));
            if (fl.TryGetProperty("alongLine", out JsonElement al))
                foreach ((JsonElement e, int i) in StudyCaseJson.Arr(al, "faults.alongLine"))
                {
                    string at = $"faults.alongLine[{i}]";
                    StudyCaseJson.Obj(e, at, ["branch", "fractions"], ["branch", "fractions"]);
                    int br = StudyCaseJson.Int(e.GetProperty("branch"), at + ".branch");
                    foreach ((JsonElement fe, int fi) in StudyCaseJson.Arr(e.GetProperty("fractions"), at + ".fractions"))
                    {
                        double d = StudyCaseJson.Num(fe, $"{at}.fractions[{fi}]");
                        if (!(d >= 0.0 && d <= 1.0)) throw new StudyCaseException($"{at}.fractions[{fi}] must be in [0, 1].");
                        faults.Add(FaultSpec.AlongBranch(br, d));
                    }
                }
        }
        return new AuditRequest(casePath, devices, curves, cti, faults);
    }

    private static int Audit(Args a, Func<string, GridModel> load)
    {
        if (a.Positional is null) { Console.Error.WriteLine("usage: gridsim protection-audit <study.json> (see 'gridsim protection-audit --help')"); return CliExit.Usage; }
        if (!File.Exists(a.Positional)) { Console.Error.WriteLine($"error: protection-audit: study file '{a.Positional}' does not exist."); return CliExit.InputError; }
        AuditRequest req = ReadStudy(a.Positional);
        GridModel? m = LoadCase("protection-audit", req.CasePath, load, out int exit);
        if (m is null) return exit;
        BoundDeviceSet b = BindFile(req.DevicesPath, m, a.Str("--curves") ?? req.Curves);
        if (req.Faults is not null)
            foreach (FaultSpec f in req.Faults.Where(f => f.Bus is not null))
                if (!m.Buses.Any(x => x.Id == f.Bus)) throw new StudyCaseException($"faults.buses: bus {f.Bus} is not in the case.");
        ProtectionAuditResult r = ProtectionAudit.Run(b, req.Faults, req.Cti);
        Console.WriteLine($"protection-audit - {m.Name}: {b.Devices.Count} device(s), {r.Faults.Count} fault(s), required interval {F(r.RequiredMarginS)} s: {r.Verdict.ToString().ToUpperInvariant()}");
        foreach (AuditPair p in r.Pairs)
            Console.WriteLine($"  pair {p.PrimaryId} -> {p.BackupId} shot {p.Shot}: worst margin {F(p.WorstMarginS)} s at {p.WorstFault} ({p.FaultsEvaluated} fault(s)) {(p.Coordinated ? "ok" : "NOT COORDINATED")}");
        foreach (AuditDevice d in r.Devices)
            Console.WriteLine($"  {d.DeviceId,-10} " + (d.Evaluated ? $"min fault {F(d.MinFaultCurrentA)} A at {d.MinFault}, {F(d.SensitivityRatio)} x pickup {(d.Operates ? "operates" : "DOES NOT OPERATE")}{(double.IsNaN(d.InstantaneousReachRatio) ? "" : $"; inst reach {F(d.InstantaneousReachRatio)}{(d.InstantaneousOverreaches ? " OVERREACHES" : "")}")}  [{d.Confidence}]" : "not evaluated: " + d.Reason));
        foreach (AuditBreaker br in r.Breakers)
            Console.WriteLine($"  {br.DeviceId,-10} breaker at bus {br.Bus}: " + (br.Evaluated ? $"{(br.Adequate ? "adequate" : "NOT ADEQUATE")} (making {F(br.MakingMarginKa)}, breaking {F(br.BreakingMarginKa)}, thermal {F(br.ThermalMarginKa)} kA)" : "not evaluated: " + br.Reason));
        foreach (NotEvaluatedCheck n in r.NotEvaluated) Console.WriteLine($"  NOT EVALUATED {n.Subject}: {n.Check}: {n.Reason}");

        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("format", AuditFormat).Set("case", m.Name).Set("verdict", r.Verdict.ToString()).Set("required_margin_s", r.RequiredMarginS)
               .Set("devices", b.Devices.Count).Set("faults", r.Faults.Count).Set("not_evaluated", r.NotEvaluated.Count);
            CliResultTable pt = rep.Table("pairs", "primary", "backup", "shot", "faults_evaluated", "worst_margin_s", "worst_fault", "primary_time_s", "backup_time_s", "required_s", "coordinated");
            foreach (AuditPair p in r.Pairs) pt.Row(p.PrimaryId, p.BackupId, p.Shot, p.FaultsEvaluated, Fin(p.WorstMarginS), p.WorstFault, Fin(p.PrimaryTimeS), Fin(p.BackupTimeS), p.RequiredS, p.Coordinated);
            CliResultTable dt = rep.Table("device_sensitivity", "device", "function", "confidence", "evaluated", "min_fault", "min_fault_current_a", "pickup_a", "sensitivity_ratio", "operates", "inst_reach_ratio", "inst_overreaches");
            foreach (AuditDevice d in r.Devices) dt.Row(d.DeviceId, d.Function, d.Confidence.ToString(), d.Evaluated, d.MinFault, Fin(d.MinFaultCurrentA), Fin(d.PickupA), Fin(d.SensitivityRatio), d.Operates, Fin(d.InstantaneousReachRatio), d.InstantaneousOverreaches);
            CliResultTable bt = rep.Table("breakers", "device", "bus", "evaluated", "adequate", "making_margin_ka", "breaking_margin_ka", "thermal_margin_ka");
            foreach (AuditBreaker x in r.Breakers) bt.Row(x.DeviceId, x.Bus, x.Evaluated, x.Adequate, Fin(x.MakingMarginKa), Fin(x.BreakingMarginKa), Fin(x.ThermalMarginKa));
            CliResultTable nt = rep.Table("not_evaluated", "subject", "check", "reason");
            foreach (NotEvaluatedCheck n in r.NotEvaluated) nt.Row(n.Subject, n.Check, n.Reason);
            rep.Complete();
        }
        bool devicesFail = r.Devices.Any(d => d.Evaluated && !d.Operates) || r.Devices.Any(d => d.InstantaneousOverreaches);
        return r.Verdict == AuditVerdict.NotCoordinated || devicesFail ? CliExit.Verdict : CliExit.Ok;
    }
}
