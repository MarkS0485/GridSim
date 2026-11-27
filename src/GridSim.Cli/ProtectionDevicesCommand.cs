using System.Globalization;
using System.Text.Json;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Coordination;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.Workflow;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-07: the verbs <c>devices-check</c>, <c>fault-line</c>, <c>sc-trace</c> and <c>protection-audit</c> over placed
/// protection devices (<c>gridsim-devices/1</c>, GridSim.Core/Protection/Devices and /Workflow). A positional case (or, for
/// <c>protection-audit</c>, a strict <c>gridsim-protection-audit/1</c> study file naming the case and the device file),
/// strict flags, results through the shared writer. All arithmetic is in Core. Exit codes: 0 done (and, for the audit, every
/// pair coordinated); 1 the case, devices or curve data cannot be loaded or are invalid; 2 usage; 3 the study ran and a
/// pair is not coordinated or a device would not operate at its minimum fault (for the audit also: an instantaneous element
/// overreaches, a breaker is not adequate, or no pair could be graded, ANA-21).
/// RC5 WP4 adds <c>protection-eval</c> (every placed element, 21, 67, 50N/51N and 87L included, on the phasors of one
/// three-phase or unbalanced fault) and <c>protection-grade</c> (coordination checks G1 to G5 over a fault list), both
/// over GridSim.Core/Protection/Placed; the four verbs above are unchanged.
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
                "protection-eval" => Eval(Parse(verb, a, "--devices", "--curves", "--bus", "--branch", "--fraction", "--type", "--xf", "--energised", "--swing"), load),
                "protection-grade" => Grade(Parse(verb, a, "--devices", "--curves", "--types", "--fractions", "--cti"), load),
                _ => throw new UsageException($"unknown verb '{verb}'"),
            };
        }
        catch (UsageException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.Usage; }
        catch (StudyCaseException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }
        catch (ArgumentException ex) when (ex is not UnknownCaseException) { Console.Error.WriteLine($"error: {verb}: {CliExit.UserMessage(ex)}"); return CliExit.Usage; }
    }

    // ---- protection-eval and protection-grade (RC5 WP4) --------------------------------------------------------------

    private static FaultType FaultTypeOf(string s)
    {
        try { return FaultPhasors.ParseType(s); }
        catch (ArgumentException ex) { throw new UsageException(ex.Message); }
    }

    private static int Eval(Args a, Func<string, GridModel> load)
    {
        string dev = a.Str("--devices") ?? throw new UsageException("--devices <set.json> is required");
        // RC5 WP17: the power-swing mode reads an RMS trace instead of applying a fault.
        if (a.Str("--swing") is string swing)
        {
            foreach (string f in new[] { "--bus", "--branch", "--fraction", "--type", "--xf", "--energised" })
                if (a.Str(f) is not null) throw new UsageException($"--swing takes no fault, so {f} is not used with it");
            return Swing(a, load, dev, swing);
        }
        bool energised = (a.Str("--energised") ?? "false") switch
        {
            "true" => true, "false" => false,
            var e => throw new UsageException($"--energised expected true or false but got '{e}'"),
        };
        bool hasBus = a.Str("--bus") is not null, hasBr = a.Str("--branch") is not null;
        if (hasBus == hasBr) throw new UsageException("give exactly one of --bus <id> or --branch <i> --fraction <d>");
        if (hasBus && a.Str("--fraction") is not null) throw new UsageException("--fraction goes with --branch");
        if (hasBus && energised) throw new UsageException("--energised true marks a line closed onto a fault, so it needs --branch, not --bus");
        double frac = a.Num("--fraction", 0.5);
        if (!(frac >= 0.0 && frac <= 1.0)) throw new UsageException($"--fraction expected a number in [0, 1] but got '{a.Str("--fraction")}'");
        FaultSpec fault = hasBus ? FaultSpec.AtBus((int)Whole(a, "--bus")) : FaultSpec.AlongBranch((int)Whole(a, "--branch"), frac);
        if (energised) fault = fault with { Energisation = true };
        FaultType type = FaultTypeOf(a.Str("--type") ?? "3ph");
        double xf = a.Num("--xf", 0.0);
        if (xf < 0.0) throw new UsageException($"--xf expected a fault reactance of 0 pu or more but got '{a.Str("--xf")}'");
        GridModel? m = LoadCase("protection-eval", a.Positional, load, out int exit);
        if (m is null) return exit;
        if (fault.Branch is int br && (br < 0 || br >= m.Branches.Count)) throw new UsageException($"--branch {br}: case '{m.Name}' has branches 0..{m.Branches.Count - 1}.");
        BoundDeviceSet b = BindFile(dev, m, a.Str("--curves"));
        FaultPhasors ph = FaultPhasors.Build(m, fault, type, xf);
        CliResult? rep = CliOutput.Report;
        rep?.Set("case", m.Name).Set("fault", fault.Label).Set("type", FaultPhasors.TypeName(type)).Set("xf_pu", xf);
        if (ph.NotEvaluated is not null)
        {
            Console.WriteLine($"protection-eval - {m.Name}: {FaultPhasors.TypeName(type)} fault at {fault.Label} not evaluated: {ph.NotEvaluated}");
            rep?.Set("not_evaluated", ph.NotEvaluated);
            rep?.Complete();
            return CliExit.Verdict;
        }
        IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, ph);
        rep?.Set("fault_bus", ph.Network!.FaultBusId);
        Console.WriteLine($"protection-eval - {m.Name}: {FaultPhasors.TypeName(type)} fault at {fault.Label} (xf {F(xf)} pu):");
        foreach (PlacedRow r in rows) Console.WriteLine("  " + Describe(r));
        foreach (ConverterWithoutFaultInfeed c in ph.ConvertersWithoutFaultInfeed)
            Console.WriteLine($"  note: converter unit {c.Name} at bus {c.Bus} keeps its prefault current in the unbalanced study (no fault infeed).");
        // RC5 WP17: the scheme lines and columns appear only when a device carries a scheme.
        IReadOnlyList<SchemeRow?> schemes = SchemeLogic.Apply(b, rows);
        bool anyScheme = schemes.Any(x => x is not null);
        foreach (SchemeRow? sr in schemes)
        {
            if (sr is null) continue;
            string head = $"  scheme {sr.DeviceId} {SchemeLogic.Name(sr.Type)} with {sr.RemoteId}: ";
            Console.WriteLine(head + (!sr.Evaluated ? "not evaluated: " + sr.Reason
                : double.IsInfinity(sr.AidedTripS) ? "does not trip"
                : $"trips in {F(sr.AidedTripS)} s{(sr.Aided ? $" (aided; zones alone {(double.IsInfinity(sr.PlainTimeS) ? "do not operate" : F(sr.PlainTimeS) + " s")})" : " (zone timers)")}"));
        }
        string[] cols = ["device", "function", "element", "evaluated", "reason", "measured", "unit", "loop", "zone", "direction",
            "angle_deg", "z_real_ohm", "z_imag_ohm", "id_a", "ib_a", "threshold_a", "time_s", "operates"];
        if (anyScheme) cols = [.. cols, "scheme", "aided", "aided_trip_s"];
        CliResultTable? t = rep?.Table("devices", cols);
        for (int k = 0; k < rows.Count; k++)
        {
            PlacedRow r = rows[k];
            object?[] cells = [r.DeviceId, r.Function, r.Element, r.Evaluated, r.Reason, Fin(r.MeasuredValue), r.Unit, r.Loop, r.Zone, r.Direction, Fin(r.AngleDifferenceDeg),
                Fin(r.ApparentImpedanceOhms.Real), Fin(r.ApparentImpedanceOhms.Imaginary), Fin(r.DifferentialA), Fin(r.BiasA), Fin(r.ThresholdA), Fin(r.TimeS), r.Operates];
            if (anyScheme)
            {
                SchemeRow? sr = schemes[k];
                cells = [.. cells, sr is null ? null : SchemeLogic.Name(sr.Type), sr?.Aided, sr is null ? null : Fin(sr.AidedTripS)];
            }
            t?.Row(cells);
        }
        rep?.Complete();
        return CliExit.Ok;
    }

    /// <summary>RC5 WP17: power-swing blocking on an RMS bus-voltage trace, for every 21 with a powerSwing setting.</summary>
    private static int Swing(Args a, Func<string, GridModel> load, string dev, string tracePath)
    {
        GridModel? m = LoadCase("protection-eval", a.Positional, load, out int exit);
        if (m is null) return exit;
        BoundDeviceSet b = BindFile(dev, m, a.Str("--curves"));
        if (!File.Exists(tracePath)) throw new StudyCaseException($"swing trace '{tracePath}' does not exist.");
        SwingTrace trace = SwingTrace.Parse(File.ReadAllText(tracePath));
        CliResult? rep = CliOutput.Report;
        rep?.Set("case", m.Name).Set("mode", "swing").Set("samples", trace.TimeS.Count);
        Console.WriteLine($"protection-eval - {m.Name}: power-swing blocking over {trace.TimeS.Count} sample(s), {F(trace.TimeS[0])} s to {F(trace.TimeS[^1])} s:");
        CliResultTable? t = rep?.Table("swing", "device", "pass", "outer_entry_s", "inner_entry_s", "swing", "block_from_s", "block_to_s", "release", "block_zones", "reason");
        foreach (PlacedDevice d in b.Devices)
        {
            if (d.Distance?.PowerSwing is not { } ps) continue;
            string zones = string.Join(" ", ps.BlockZones.Select(z => z.ToString(Inv)));
            IReadOnlyList<System.Numerics.Complex>? z = b.OutOfService(d) ? null : trace.Impedance(m, d);
            if (z is null)
            {
                string why = b.OutOfService(d) ? "the device's branch is out of service" : $"the trace has no voltage for bus {d.Location.From} or {d.Location.To}, or the bus has no base kV";
                Console.WriteLine($"  {d.Id,-10} not evaluated: {why}");
                t?.Row(d.Id, null, null, null, null, null, null, null, zones, why);
                continue;
            }
            SwingResult r = SwingBlocking.Evaluate(trace.TimeS, z, ps);
            if (r.Passes.Count == 0) Console.WriteLine($"  {d.Id,-10} the impedance never enters the outer blinder");
            for (int k = 0; k < r.Passes.Count; k++)
            {
                SwingPass p = r.Passes[k];
                string inner = p.InnerEntryS is double ie ? $", inner at {F(ie)} s" : ", inner not reached";
                Console.WriteLine($"  {d.Id,-10} pass {k + 1}: outer at {F(p.OuterEntryS)} s{inner}: "
                    + (p.IsSwing ? $"SWING, zones {zones} blocked from {F(p.BlockFromS!.Value)} s to {(p.BlockToS is double bt ? F(bt) + " s" : "the end of the trace")} ({p.Release})" : p.Release));
                t?.Row(d.Id, k + 1, p.OuterEntryS, p.InnerEntryS, p.IsSwing, p.BlockFromS, p.BlockToS, p.Release, zones, null);
            }
        }
        rep?.Complete();
        return CliExit.Ok;
    }

    private static string Describe(PlacedRow r)
    {
        if (!r.Evaluated) return $"{r.DeviceId,-10} {r.Element,-8} not evaluated: {r.Reason}";
        string what = r.Element switch
        {
            "21" => double.IsNaN(r.ApparentImpedanceOhms.Real) ? "no loop measured"
                : $"loop {r.Loop} Z {F(r.ApparentImpedanceOhms.Real)} {(r.ApparentImpedanceOhms.Imaginary < 0 ? "-" : "+")} j{F(Math.Abs(r.ApparentImpedanceOhms.Imaginary))} ohm sec, {(r.Zone is int z ? "zone " + z.ToString(Inv) : "no zone")}",
            "87L" => $"phase {r.Loop} Id {F(r.DifferentialA)} A, Ib {F(r.BiasA)} A, threshold {F(r.ThresholdA)} A",
            "87T" => $"phase {r.Loop} Id {F(r.DifferentialA)} pu, Ib {F(r.BiasA)} pu, threshold {F(r.ThresholdA)} pu",
            "87B" => $"phase {r.Loop} Id {F(r.DifferentialA)} A, Ib {F(r.BiasA)} A, threshold {F(r.ThresholdA)} A",
            "67" or "67N" or "67Q" => $"{r.Loop} {F(r.MeasuredValue)} A sec, {r.Direction}{(double.IsNaN(r.AngleDifferenceDeg) ? "" : $" (Iop - Vpol {F(r.AngleDifferenceDeg)} deg)")}",
            _ => $"{F(r.MeasuredValue)} {r.Unit}",
        };
        string time = r.Operates ? $"operates in {F(r.TimeS)} s" : "does not operate";
        return $"{r.DeviceId,-10} {r.Element,-8} {what}; {time}{(r.Reason is null || r.Element == "67" || r.Element == "67N" || r.Element == "67Q" ? "" : " (" + r.Reason + ")")}";
    }

    private static int Grade(Args a, Func<string, GridModel> load)
    {
        string dev = a.Str("--devices") ?? throw new UsageException("--devices <set.json> is required");
        var types = new List<FaultType>();
        foreach (string p in (a.Str("--types") ?? "3ph,slg").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            FaultType ft = FaultTypeOf(p);
            if (!types.Contains(ft)) types.Add(ft);
        }
        if (types.Count == 0) throw new UsageException("--types expected at least one of 3ph, slg, ll, llg");
        IReadOnlyList<double> fractions = PlacedCoordination.DefaultFractions;
        if (a.Str("--fractions") is string fs)
        {
            var list = new List<double>();
            foreach (string p in fs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!double.TryParse(p, NumberStyles.Float, Inv, out double d) || !(d > 0.0 && d < 1.0)) throw new UsageException($"--fractions expected numbers inside (0, 1) but got '{p}'");
                list.Add(d);
            }
            if (list.Count == 0) throw new UsageException("--fractions expected at least one number");
            list.Sort();
            fractions = list;
        }
        double cti = Cti(a);
        GridModel? m = LoadCase("protection-grade", a.Positional, load, out int exit);
        if (m is null) return exit;
        BoundDeviceSet b = BindFile(dev, m, a.Str("--curves"));
        PlacedCoordinationResult r = PlacedCoordination.Run(b, null, types, cti, fractions);
        Console.WriteLine($"protection-grade - {m.Name}: {b.Devices.Count} device(s), {r.Faults.Count} fault(s) ({string.Join(",", types.Select(FaultPhasors.TypeName))}), required interval {F(r.RequiredMarginS)} s: {r.Verdict.ToString().ToUpperInvariant()}");
        foreach (PlacedPairRow p in r.Pairs)
            Console.WriteLine($"  G1 {p.Fault}: {p.PrimaryId} -> {p.BackupId}: " + (p.Evaluated ? $"margin {F(p.MarginS)} s vs {F(p.RequiredS)} s {(p.Coordinated ? "ok" : "NOT COORDINATED")}" : "not graded: " + p.Reason));
        foreach (PlacedCheck c in r.Checks) Console.WriteLine($"  {c.Check} {c.DeviceId}: {(c.Passed ? "pass" : "FAIL")}: {c.Detail}");
        foreach (NotEvaluatedCheck n in r.NotEvaluated) Console.WriteLine($"  NOT EVALUATED {n.Subject}: {n.Check}: {n.Reason}");
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", m.Name).Set("verdict", r.Verdict.ToString()).Set("required_margin_s", r.RequiredMarginS).Set("devices", b.Devices.Count)
               .Set("faults", r.Faults.Count).Set("types", string.Join(",", types.Select(FaultPhasors.TypeName))).Set("not_evaluated", r.NotEvaluated.Count);
            CliResultTable pt = rep.Table("pairs", "fault", "primary", "backup", "primary_time_s", "backup_time_s", "margin_s", "required_s", "evaluated", "coordinated", "reason");
            foreach (PlacedPairRow p in r.Pairs) pt.Row(p.Fault, p.PrimaryId, p.BackupId, Fin(p.PrimaryTimeS), Fin(p.BackupTimeS), Fin(p.MarginS), p.RequiredS, p.Evaluated, p.Coordinated, p.Reason);
            CliResultTable ct = rep.Table("checks", "check", "device", "passed", "value", "detail");
            foreach (PlacedCheck c in r.Checks) ct.Row(c.Check, c.DeviceId, c.Passed, Fin(c.Value), c.Detail);
            CliResultTable nt = rep.Table("not_evaluated", "subject", "check", "reason");
            foreach (NotEvaluatedCheck n in r.NotEvaluated) nt.Row(n.Subject, n.Check, n.Reason);
            rep.Complete();
        }
        return r.Verdict == PlacedVerdict.Coordinated ? CliExit.Ok : CliExit.Verdict;
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
        // A file the study names is a side file (GridSim.Core.Security.SideFile): a network or device path is refused before
        // anything is opened, and a relative path is resolved against the study's folder.
        string Rel(string p, string member)
        {
            try { return GridSim.Core.Security.SideFile.Resolve(p, dir, "the protection-audit study: " + member); }
            catch (InvalidDataException ex) { throw new StudyCaseException(ex.Message); }
        }
        string casePath = StudyCaseJson.Str(root.GetProperty("case"), "case");
        string caseFull = Rel(casePath, "case");
        if (!File.Exists(caseFull) && !Directory.Exists(caseFull)) { /* a built-in or search-path case name: pass through */ } else casePath = caseFull;
        string devices = Rel(StudyCaseJson.Str(root.GetProperty("devices"), "devices"), "devices");
        string? curves = root.TryGetProperty("curves", out JsonElement c) ? Rel(StudyCaseJson.Str(c, "curves"), "curves") : null;
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
        // ANA-21: a breaker printed NOT ADEQUATE and an audit with no gradable pair (Incomplete) are not a pass either; both
        // exited 0, so a script reading the exit code passed an under-rated breaker.
        bool breakersFail = r.Breakers.Any(x => x.Evaluated && !x.Adequate);
        return r.Verdict != AuditVerdict.Coordinated || devicesFail || breakersFail ? CliExit.Verdict : CliExit.Ok;
    }
}
