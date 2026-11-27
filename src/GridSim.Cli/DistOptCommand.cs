using System.Globalization;
using System.Text.Json;
using GridSim.Core.Analysis.DistributionOptimisation;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Distribution.Meshed;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP33: the <c>dist-opt</c> verb, distribution optimisation in three modes: <c>capplace</c> (shunt capacitor
/// placement and sizing, <see cref="CapacitorPlacement"/>), <c>vvo</c> (volt/VAr optimisation and CVR over OLTC taps and
/// switched capacitors, <see cref="VoltVarOptimisation"/>) and <c>phase-balance</c> (moving single-phase loads on a
/// dist3ph-mesh case, <see cref="PhaseBalancing"/>). The settings come from a <c>gridsim-distopt/1</c> JSON file
/// (<c>--params</c>). The CLI reads and prints; the arithmetic is in Core. <c>--out</c> writes the result table (CSV or
/// JSON by extension, <see cref="OptimisationReport"/>). Not on the shared --json/--csv writer list.
/// Exit codes: 0 done; 1 a file cannot be read or is refused; 2 usage; 3 the result is outside the voltage band, the
/// search stopped at its round cap, or a phase-balance verification solve did not converge.
/// </summary>
internal static class DistOptCommand
{
    /// <summary>The format name of the settings file.</summary>
    public const string ParamsFormat = "gridsim-distopt/1";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v, string fmt) => v.ToString(fmt, Inv);
    private static readonly string[] ValueFlags = ["--mode", "--params", "--out"];

    private sealed class UsageException(string message) : Exception(message);

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        try
        {
            string? positional = null;
            var flags = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < a.Count; i++)
            {
                string t = a[i];
                if (!t.StartsWith("--", StringComparison.Ordinal))
                {
                    if (positional is not null) throw new UsageException($"dist-opt takes one case but got: {positional} {t}.");
                    positional = t;
                    continue;
                }
                if (Array.IndexOf(ValueFlags, t) < 0) throw new UsageException($"dist-opt does not use {t}. dist-opt accepts: {string.Join(", ", ValueFlags)}.");
                if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{t} expected a value but none was given");
                if (!flags.TryAdd(t, a[++i])) throw new UsageException($"{t} was given twice");
            }
            if (positional is null) throw new UsageException("usage: gridsim dist-opt <case> --mode capplace|vvo|phase-balance [--params <file.json>] [--out <file.csv|file.json>] (see 'gridsim dist-opt --help')");
            if (!flags.TryGetValue("--mode", out string? mode)) throw new UsageException("--mode is required: capplace, vvo or phase-balance");
            mode = mode.ToLowerInvariant();
            if (mode is not ("capplace" or "vvo" or "phase-balance")) throw new UsageException($"--mode expected capplace, vvo or phase-balance but got '{mode}'");
            string? outPath = flags.GetValueOrDefault("--out");
            if (outPath is not null)
            {
                string ext = Path.GetExtension(outPath).ToLowerInvariant();
                if (ext != ".csv" && ext != ".json") throw new UsageException($"--out expected a .csv or .json file but got '{outPath}'");
            }

            JsonElement? section = null, common = null;
            JsonDocument? doc = null;
            try
            {
                if (flags.TryGetValue("--params", out string? pp))
                {
                    if (!File.Exists(pp)) { Console.Error.WriteLine($"error: dist-opt: params file not found: {pp}"); return CliExit.InputError; }
                    doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(pp)), new JsonDocumentOptions { AllowTrailingCommas = false });
                    TransmissionSecurityCommand.CheckDuplicates(doc.RootElement);
                    (section, common) = Sections(doc.RootElement, mode);
                }
                return mode switch
                {
                    "capplace" => CapPlace(positional, load, section, common, outPath),
                    "vvo" => Vvo(positional, load, section, common, outPath),
                    _ => PhaseBalance(positional, section, outPath),
                };
            }
            finally { doc?.Dispose(); }
        }
        catch (UsageException ex) { Console.Error.WriteLine($"error: dist-opt: {ex.Message}"); return CliExit.Usage; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or Dist3phMeshCommand.InputException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { Console.Error.WriteLine($"error: dist-opt: {ex.Message}"); return CliExit.InputError; }
        catch (ArgumentException ex) when (ex is not UnknownCaseException) { Console.Error.WriteLine($"error: dist-opt: {CliExit.UserMessage(ex)}"); return CliExit.InputError; }
    }

    // ---- the settings file -------------------------------------------------------------------------------------------

    private static (JsonElement? Section, JsonElement? Common) Sections(JsonElement root, string mode)
    {
        string key = mode switch { "capplace" => "capplace", "vvo" => "vvo", _ => "phaseBalance" };
        Keys(root, "params", "format", "common", "capplace", "vvo", "phaseBalance");
        if (!root.TryGetProperty("format", out JsonElement f) || f.ValueKind != JsonValueKind.String || f.GetString() != ParamsFormat)
            throw new InvalidDataException($"params: \"format\" must be \"{ParamsFormat}\".");
        foreach (string other in new[] { "capplace", "vvo", "phaseBalance" })
            if (other != key && root.TryGetProperty(other, out _))
                throw new InvalidDataException($"params: the file has a \"{other}\" section, but the mode is {mode} (it reads \"{key}\"); remove the other section.");
        if (mode == "phase-balance" && root.TryGetProperty("common", out _))
            throw new InvalidDataException("params: \"common\" (voltage band, power flow, confirmation) is for capplace and vvo; phase-balance does not read it.");
        JsonElement? s = root.TryGetProperty(key, out JsonElement se) ? se : null;
        JsonElement? c = root.TryGetProperty("common", out JsonElement ce) ? ce : null;
        if (s is { } sv && sv.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"params: \"{key}\" must be an object.");
        if (c is { } cv && cv.ValueKind != JsonValueKind.Object) throw new InvalidDataException("params: \"common\" must be an object.");
        return (s, c);
    }

    private static void Keys(JsonElement o, string where, params string[] allowed)
    {
        if (o.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where} must be a JSON object.");
        foreach (JsonProperty p in o.EnumerateObject())
            if (Array.IndexOf(allowed, p.Name) < 0)
                throw new InvalidDataException($"{where}: unknown member \"{p.Name}\" (known: {string.Join(", ", allowed)}).");
    }

    private static double Num(JsonElement o, string key, string where, double fallback)
    {
        if (!o.TryGetProperty(key, out JsonElement v)) return fallback;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d))
            throw new InvalidDataException($"{where}: \"{key}\" must be a finite number.");
        return d;
    }

    private static int Int(JsonElement o, string key, string where, int fallback)
    {
        if (!o.TryGetProperty(key, out JsonElement v)) return fallback;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int d)) throw new InvalidDataException($"{where}: \"{key}\" must be a whole number.");
        return d;
    }

    private static bool Bool(JsonElement o, string key, string where, bool fallback)
    {
        if (!o.TryGetProperty(key, out JsonElement v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{where}: \"{key}\" must be true or false."),
        };
    }

    private static string Str(JsonElement o, string key, string where)
    {
        if (!o.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString()))
            throw new InvalidDataException($"{where}: \"{key}\" must be a non-empty string.");
        return v.GetString()!;
    }

    private static JsonElement Arr(JsonElement o, string key, string where)
    {
        JsonElement v = o.GetProperty(key);
        if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{where}: \"{key}\" must be an array.");
        return v;
    }

    private static double[] Numbers(JsonElement o, string key, string where)
    {
        var list = new List<double>();
        foreach (JsonElement e in Arr(o, key, where).EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double d) || !double.IsFinite(d))
                throw new InvalidDataException($"{where}: \"{key}\" must hold finite numbers.");
            list.Add(d);
        }
        return list.ToArray();
    }

    private static string[] Strings(JsonElement o, string key, string where)
    {
        var list = new List<string>();
        foreach (JsonElement e in Arr(o, key, where).EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(e.GetString())) throw new InvalidDataException($"{where}: \"{key}\" must hold non-empty strings.");
            list.Add(e.GetString()!);
        }
        return list.ToArray();
    }

    private static DistOptCommon Common(JsonElement? c)
    {
        var d = new DistOptCommon();
        if (c is not { } o) return d;
        Keys(o, "params.common", "vminPu", "vmaxPu", "tolerancePu", "maxIterations", "confirm", "maxRounds");
        return new DistOptCommon
        {
            VminPu = Num(o, "vminPu", "params.common", d.VminPu),
            VmaxPu = Num(o, "vmaxPu", "params.common", d.VmaxPu),
            TolerancePu = Num(o, "tolerancePu", "params.common", d.TolerancePu),
            MaxIterations = Int(o, "maxIterations", "params.common", d.MaxIterations),
            Confirm = Bool(o, "confirm", "params.common", d.Confirm),
            MaxRounds = Int(o, "maxRounds", "params.common", d.MaxRounds),
        };
    }

    private static GridModel? LoadCase(string path, Func<string, GridModel> load, out int exit)
    {
        exit = CliExit.Ok;
        try { return load(path); }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            Console.Error.WriteLine($"error loading case '{path}': {ex.Message}");
            exit = CliExit.CaseLoadFailure(ex);
            return null;
        }
    }

    private static void WriteOut(string? path, IReadOnlyList<OptimisationReport.Row> rows)
    {
        if (path is null) return;
        string text = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? OptimisationReport.Json("dist-opt", rows) : OptimisationReport.Csv(rows);
        File.WriteAllText(CliFiles.Output(path, "dist-opt result table"), text, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"  result written to {path}");
    }

    // ---- capplace ----------------------------------------------------------------------------------------------------

    private static int CapPlace(string casePath, Func<string, GridModel> load, JsonElement? section, JsonElement? common, string? outPath)
    {
        var opt = new CapPlaceOptions { Common = Common(common) };
        if (section is { } s)
        {
            const string w = "params.capplace";
            Keys(s, w, "candidates", "defaultStepsMvar", "fixedCostPerBank", "costPerMvar", "energyCostPerMwh", "hoursPerYear");
            List<CapacitorCandidate>? cands = null;
            if (s.TryGetProperty("candidates", out _))
            {
                cands = new List<CapacitorCandidate>();
                int k = 0;
                foreach (JsonElement c in Arr(s, "candidates", w).EnumerateArray())
                {
                    string cw = $"{w}.candidates[{k++}]";
                    Keys(c, cw, "bus", "stepsMvar");
                    if (!c.TryGetProperty("bus", out _)) throw new InvalidDataException($"{cw}: \"bus\" is required.");
                    if (!c.TryGetProperty("stepsMvar", out _)) throw new InvalidDataException($"{cw}: \"stepsMvar\" is required.");
                    cands.Add(new CapacitorCandidate(Int(c, "bus", cw, 0), Numbers(c, "stepsMvar", cw)));
                }
            }
            opt = opt with
            {
                Candidates = cands,
                DefaultStepsMvar = s.TryGetProperty("defaultStepsMvar", out _) ? Numbers(s, "defaultStepsMvar", w) : null,
                FixedCostPerBank = Num(s, "fixedCostPerBank", w, 0.0),
                CostPerMvar = Num(s, "costPerMvar", w, 0.0),
                EnergyCostPerMwh = Num(s, "energyCostPerMwh", w, opt.EnergyCostPerMwh),
                HoursPerYear = Num(s, "hoursPerYear", w, opt.HoursPerYear),
            };
        }
        GridModel? m = LoadCase(casePath, load, out int exit);
        if (m is null) return exit;
        CapPlaceResult r = CapacitorPlacement.Run(m, opt);
        Console.WriteLine($"dist-opt capplace - {m.Name} ({m.BusCount} buses): greedy by marginal benefit, then neighbour confirmation (heuristic)");
        Console.WriteLine($"  base losses {F(r.BaseLossMw, "F6")} MW  objective {F(r.BaseObjective, "F4")}");
        foreach (CapPlaceStep st in r.Steps)
            Console.WriteLine($"  step {st.Round}: bus {st.BusId} to {F(st.SizeMvar, "R")} Mvar: losses {F(st.LossMw, "F6")} MW, objective {F(st.Objective, "F4")}");
        foreach (CapPlaceBank b in r.Banks.Where(b => b.Step > 0))
            Console.WriteLine($"  bank at bus {b.BusId}: {F(b.SizeMvar, "R")} Mvar (size {b.Step})");
        Console.WriteLine($"  losses {F(r.LossMw, "F6")} MW  loss cost {F(r.LossCostPerYear, "F4")}  capital {F(r.CapitalCost, "F4")}  objective {F(r.Objective, "F4")}");
        Console.WriteLine($"  voltages {F(r.VminPu, "F5")} to {F(r.VmaxPu, "F5")} pu{(r.Feasible ? "" : $"  OUTSIDE THE BAND by {F(r.ViolationPu, "E3")} pu in total")}");
        if (r.UnconfirmedBanks.Count > 0)
            Console.WriteLine($"  confirmation covered the {DistOpt.MaxConfirmControls} banks placed last; not confirmed: bus {string.Join(", ", r.UnconfirmedBanks.Select(b => b.ToString(CultureInfo.InvariantCulture)))}");
        Console.WriteLine($"  {r.Status}: {r.Message}; {r.PowerFlows} power flows");
        WriteOut(outPath, DistOptReport.Rows(r));
        return r.Feasible && r.Status == DistOptStatus.LocalOptimum ? CliExit.Ok : CliExit.Verdict;
    }

    // ---- vvo ---------------------------------------------------------------------------------------------------------

    private static int Vvo(string casePath, Func<string, GridModel> load, JsonElement? section, JsonElement? common, string? outPath)
    {
        var opt = new VvoOptions { Common = Common(common) };
        if (section is { } s)
        {
            const string w = "params.vvo";
            Keys(s, w, "objective", "taps", "capacitors");
            VvoObjective obj = VvoObjective.Losses;
            if (s.TryGetProperty("objective", out _))
            {
                string os = Str(s, "objective", w);
                obj = os switch
                {
                    "losses" => VvoObjective.Losses,
                    "demand" => VvoObjective.Demand,
                    _ => throw new InvalidDataException($"{w}: \"objective\" must be \"losses\" or \"demand\" but is \"{os}\"."),
                };
            }
            var caps = new List<VvoCapacitor>();
            if (s.TryGetProperty("capacitors", out _))
            {
                int k = 0;
                foreach (JsonElement c in Arr(s, "capacitors", w).EnumerateArray())
                {
                    string cw = $"{w}.capacitors[{k++}]";
                    Keys(c, cw, "name", "bus", "mvar", "on");
                    if (!c.TryGetProperty("bus", out _) || !c.TryGetProperty("mvar", out _)) throw new InvalidDataException($"{cw}: \"bus\" and \"mvar\" are required.");
                    caps.Add(new VvoCapacitor(Str(c, "name", cw), Int(c, "bus", cw, 0), Num(c, "mvar", cw, 0.0), Bool(c, "on", cw, false)));
                }
            }
            opt = opt with
            {
                Objective = obj,
                Taps = s.TryGetProperty("taps", out _) ? Strings(s, "taps", w) : null,
                Capacitors = caps,
            };
        }
        GridModel? m = LoadCase(casePath, load, out int exit);
        if (m is null) return exit;
        VvoResult r = VoltVarOptimisation.Run(m, opt);
        Console.WriteLine($"dist-opt vvo - {m.Name} ({m.BusCount} buses): objective {r.Objective.ToString().ToLowerInvariant()}, coordinate descent then neighbour confirmation");
        foreach (string n in r.Notes) Console.WriteLine("  note: " + n);
        Console.WriteLine($"  start: losses {F(r.BaseLossMw, "F6")} MW  demand {F(r.BaseDemandMw, "F6")} MW  violation {F(r.BaseViolationPu, "E3")} pu");
        foreach (VvoControlState c in r.Controls)
            Console.WriteLine($"  {c.Kind} {c.Name}: position {c.StartPosition} ({F(c.StartValue, "R")}) to {c.Position} ({F(c.Value, "R")})");
        Console.WriteLine($"  result: losses {F(r.LossMw, "F6")} MW  demand {F(r.DemandMw, "F6")} MW  voltages {F(r.VminPu, "F5")} to {F(r.VmaxPu, "F5")} pu{(r.Feasible ? "" : $"  OUTSIDE THE BAND by {F(r.ViolationPu, "E3")} pu in total")}");
        Console.WriteLine($"  {r.Status}: {r.Message}; {r.Sweeps} sweep(s), {r.PowerFlows} power flows");
        WriteOut(outPath, DistOptReport.Rows(r));
        return r.Feasible && r.Status == DistOptStatus.LocalOptimum ? CliExit.Ok : CliExit.Verdict;
    }

    // ---- phase-balance -----------------------------------------------------------------------------------------------

    private static int PhaseBalance(string casePath, JsonElement? section, string? outPath)
    {
        var opt = new PhaseBalanceOptions();
        if (section is { } s)
        {
            const string w = "params.phaseBalance";
            Keys(s, w, "movable", "phases", "headBranch", "neutralWire");
            List<MovableLoad>? movable = null;
            if (s.TryGetProperty("movable", out _))
            {
                movable = new List<MovableLoad>();
                int k = 0;
                foreach (JsonElement e in Arr(s, "movable", w).EnumerateArray())
                {
                    string ew = $"{w}.movable[{k++}]";
                    Keys(e, ew, "load", "phases");
                    movable.Add(new MovableLoad(Str(e, "load", ew), e.TryGetProperty("phases", out _) ? Strings(e, "phases", ew) : null));
                }
            }
            opt = opt with
            {
                Movable = movable,
                Phases = s.TryGetProperty("phases", out _) ? Strings(s, "phases", w) : opt.Phases,
                HeadBranch = s.TryGetProperty("headBranch", out _) ? Str(s, "headBranch", w) : null,
                NeutralWire = s.TryGetProperty("neutralWire", out _) ? Str(s, "neutralWire", w) : opt.NeutralWire,
            };
        }
        if (!File.Exists(casePath)) throw new InvalidDataException($"case file not found: {casePath} (phase-balance reads a dist3ph-mesh case file).");
        Dist3phMeshCommand.ParsedCase parsed;
        using (JsonDocument cd = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(casePath)), new JsonDocumentOptions { AllowTrailingCommas = false }))
        {
            TransmissionSecurityCommand.CheckDuplicates(cd.RootElement);
            parsed = Dist3phMeshCommand.Parse(cd.RootElement);
        }
        RegulatedFrame frame = parsed.Frame;
        PhaseFrameModel model = frame.Build(frame.InitialTaps());
        PhaseBalanceResult r = PhaseBalancing.Run(model, opt);
        Console.WriteLine($"dist-opt phase-balance - {model.Name}: {r.Moves.Count} movable load(s), {r.Method} search on the phase-power proxy, verified by dist3ph-mesh solves");
        if (frame.Regulators.Count > 0) Console.WriteLine("  note: regulators are held at the taps written in the case.");
        foreach (string n in r.Notes) Console.WriteLine("  note: " + n);
        for (int p = 0; p < r.Phases.Count; p++)
            Console.WriteLine($"  phase {r.Phases[p]}: {F(r.PhasePowerBeforeW[p], "F1")} W to {F(r.PhasePowerAfterW[p], "F1")} W");
        foreach (PhaseLoadMove mv in r.Moves)
            Console.WriteLine($"  load {mv.Load} ({F(mv.PowerW, "F1")} W): {mv.FromPhase} to {mv.ToPhase}");
        Console.WriteLine($"  proxy (largest phase deviation from the mean) {F(r.ProxyBeforeW, "F1")} W to {F(r.ProxyAfterW, "F1")} W; {r.ProxyEvaluations} assignment(s) evaluated");
        Console.WriteLine($"  head branch {r.HeadBranch}: solve before {r.StatusBefore}, after {r.StatusAfter}; neutral {F(r.NeutralBeforeA, "F3")} A to {F(r.NeutralAfterA, "F3")} A; unbalance factor at {r.HeadNode} {F(r.VufBefore, "E3")} to {F(r.VufAfter, "E3")}");
        WriteOut(outPath, DistOptReport.Rows(r));
        return r.StatusBefore == MeshedStatus.Converged && r.StatusAfter == MeshedStatus.Converged ? CliExit.Ok : CliExit.Verdict;
    }
}
