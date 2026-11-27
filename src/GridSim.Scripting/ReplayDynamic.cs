using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;

namespace GridSim.Scripting;

public sealed partial class ReplayEngine
{
    // ---- RMS SUBTRANSIENT and GOVERNED ----------------------------------------------------------------------------------

    /// <summary>The v2 schema folder as SELECT ENGINE RMS finds it (above the base folder, then above the program).</summary>
    internal static string? FindSchemasFor(string baseDir) => FindSchemas(baseDir);

    private static string? FindSchemas(string baseDir)
    {
        foreach (string start in new[] { baseDir, AppContext.BaseDirectory })
            for (string? d = start; d is not null; d = Path.GetDirectoryName(d))
            {
                string cand = Path.Combine(d, "schemas", "v2");
                if (File.Exists(Path.Combine(cand, "case.schema.json"))) return cand;
            }
        return null;
    }

    private static string ResolveSchemas(string written, string baseDir, string kind)
    {
        try { return GridSim.Core.Security.SideFile.Resolve(written, baseDir, $"{kind}: SCHEMAS"); }
        catch (InvalidDataException ex) { throw new ScriptException(ErrorPhase.Execution, default, ex.Message); }
    }

    private static Dyn LoadDynamics(ReplaySettings st, GridModel m, CaseInfo ci, string baseDir)
    {
        string kind = "RMS " + st.Model;
        // R1-02: the dynamic data file and the schemas folder a script names are refused on a network or device path.
        string full;
        try { full = GridSim.Core.Security.SideFile.Resolve(st.DynamicsPath!, baseDir, $"{kind}: the dynamic data file"); }
        catch (InvalidDataException ex) { throw new ScriptException(ErrorPhase.Execution, default, ex.Message); }
        if (!File.Exists(full))
            throw new ScriptException(ErrorPhase.Execution, default, $"{kind}: the dynamic data file \"{st.DynamicsPath}\" does not exist (relative paths are taken from {baseDir})");
        string schemas = st.SchemasDir is { } sd ? ResolveSchemas(sd, baseDir, kind)
            : FindSchemas(baseDir) ?? throw new ScriptException(ErrorPhase.Execution, default, $"{kind}: the v2 schemas (schemas/v2) were not found above the base folder; name them with SCHEMAS \"folder\"");
        MachineDynamicsSet set;
        try { set = MachineDynamicsLoader.Load(full, schemas, m); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ScriptException(ErrorPhase.Execution, default, $"{kind}: the dynamic data file is refused: {ex.Message}");
        }
        var names = new List<string>();
        for (int i = 0; i < m.AllGenerators.Count; i++)
            if (m.AllGenerators[i].InService) names.Add(ci.GeneratorNames[i]);
        var data = new List<SubtransientInit.MachineData>();
        var missing = new List<string>();
        for (int k = 0; k < m.Generators.Count; k++)
        {
            MachineDynamicsEntry? e = set.For(k);
            if (e is null) missing.Add("\"" + names[k] + "\"");
            else data.Add(e.Data);
        }
        if (missing.Count > 0)
            throw new ScriptException(ErrorPhase.Execution, default, $"{kind}: the dynamic data file has no machine for generator(s) {string.Join(", ", missing)}; every in-service generator needs one");
        if (set.Renewables.Count > 0)
            throw new ScriptException(ErrorPhase.Execution, default, $"{kind}: the dynamic data carries converter plant (WECC models), which the scripting engine does not integrate");
        var machines = new List<int>();
        for (int k = 0; k < m.Generators.Count; k++)
        {
            Generator g = m.Generators[k];
            if (SubtransientInit.IsConverterUnit(g, data[k]))
                throw new ScriptException(ErrorPhase.Execution, default, $"{kind}: generator \"{names[k]}\" is converter-interfaced, which the scripting engine does not integrate");
            if (g.PowerFlowOnly) continue;
            if (!(data[k].H > 0.0))
                throw new ScriptException(ErrorPhase.Execution, default, $"{kind}: generator \"{names[k]}\" has no inertia constant in the file or the case");
            machines.Add(k);
        }
        if (machines.Count == 0)
            throw new ScriptException(ErrorPhase.Execution, default, $"{kind}: no generator is a synchronous machine to integrate");
        if (st.Model == "GOVERNED" && !machines.Any(k => data[k].Governor is not null))
            throw new ScriptException(ErrorPhase.Execution, default, "RMS GOVERNED: no machine in the dynamic data file has a governor; use RMS SUBTRANSIENT for machines without controllers");
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
        return new Dyn(set, full, sha, data, machines.ToArray());
    }

    private Run RunRmsDynamic(long to)
    {
        Dyn d = _dyn!;
        bool governed = Settings.Model == "GOVERNED";
        double dt = Seconds(StepUs);
        List<SubtransientInit.MachineData> data = governed ? d.Data.ToList() : d.Data.Select(x => x with { Governor = null }).ToList();
        List<RmsExciter?>? exciters = null;
        if (governed)
        {
            exciters = [];
            for (int k = 0; k < _model.Generators.Count; k++) exciters.Add(d.Set.For(k)!.Exciter?.Create());
            if (exciters.All(x => x is null)) exciters = null;
        }
        var events = new List<RmsEvent>();
        List<ReplayEvent> faults = Events.Where(e => e.Effect is FaultEffect).OrderBy(e => e.TimeUs).ThenBy(e => e.Order).ToList();
        foreach (ReplayEvent e in faults)
        {
            var f = (FaultEffect)e.Effect;
            // WP2: a typed fault is a sequence fault; an untyped one is the bolted fault, exactly as before.
            events.Add(!f.On ? new RmsFaultClear(Seconds(e.TimeUs), f.BusId)
                : f.Type is GridSim.Core.Protection.FaultType t ? new RmsSequenceFault(Seconds(e.TimeUs), f.BusId, t, f.FaultRPu, f.FaultXPu)
                : new RmsBusFault(Seconds(e.TimeUs), f.BusId));
        }
        MultiMachineSubtransient.Result res;
        double[] h;
        try
        {
            RmsTransientStability.ObservedStudy os = RmsTransientStability.StudyEventsObserved(_model, _solved!, data, events, [],
                null, dt, Seconds(to), (double)Settings.SlipDeg * Math.PI / 180.0, exciters);
            res = os.Result;
            h = os.Prepared.H;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new ScriptException(ErrorPhase.Execution, default, $"RMS {Settings.Model}: {ex.Message}");
        }
        int n = res.Machines.Count;
        int steps = checked((int)(to / StepUs));
        var vectors = new double[steps + 1][];
        for (int k = 0; k <= steps; k++)
        {
            var v = new double[6 * n];
            for (int i = 0; i < n; i++)
            {
                MultiMachineSubtransient.MachineTrace t = res.Machines[i];
                v[6 * i] = t.DeltaRad[k]; v[6 * i + 1] = t.SpeedPu[k]; v[6 * i + 2] = t.EqpPu[k];
                v[6 * i + 3] = t.EdpPu[k]; v[6 * i + 4] = t.EqppPu[k]; v[6 * i + 5] = t.EddpPu[k];
            }
            vectors[k] = v;
        }
        string[] names = InServiceGeneratorNames();
        double hSum = 0, hDelta = 0;
        for (int i = 0; i < n; i++) { hSum += h[i]; hDelta += h[i] * vectors[steps][6 * i]; }
        double coi = hDelta / hSum;
        var final = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        var report = ImmutableArray.CreateBuilder<string>();
        report.Add($"t {Clock(to)}, {n} machine(s) ({Settings.Model.ToLowerInvariant()}), rotor angle against the inertia-weighted centre (deg) and speed deviation (pu)");
        double maxSpread = 0;
        for (int i = 0; i < n; i++)
        {
            string name = names[d.MachineGenerators[i]];
            double ang = (vectors[steps][6 * i] - coi) * 180.0 / Math.PI;
            double spd = vectors[steps][6 * i + 1];
            maxSpread = Math.Max(maxSpread, Math.Abs(ang));
            final.Add(new($"angleDeg:{name}", R(ang)));
            final.Add(new($"speedPu:{name}", R(spd)));
            report.Add($"  \"{name}\" angle {ang.ToString("F4", CultureInfo.InvariantCulture)} deg, speed {spd.ToString("E4", CultureInfo.InvariantCulture)} pu");
        }
        string sync = res.StayedInSynchronism ? "in synchronism" : "LOSS OF SYNCHRONISM";
        final.Add(new("synchronism", res.StayedInSynchronism ? "ok" : "lost"));
        report.Add($"since t = 0: {sync}; faults applied or cleared: {faults.Count}; events so far: {Events.Count}");
        return new Run(vectors, final.ToImmutable(), $"largest angle from centre {maxSpread.ToString("F4", CultureInfo.InvariantCulture)} deg, {sync}", report.ToImmutable());
    }

    // ---- EMT THREE_PHASE -------------------------------------------------------------------------------------------------

    private Run RunEmt3(long to)
    {
        double dt = Seconds(StepUs);
        GridModel m = _model;
        double f0 = m.FrequencyHz, w = 2.0 * Math.PI * f0, baseMva = m.BaseMva;
        var index = new Dictionary<int, int>();
        for (int i = 0; i < m.Buses.Count; i++) index[m.Buses[i].Id] = i;
        var branches = new List<ThreePhaseBranch>();
        var shunts = new List<ThreePhaseShunt>();
        for (int bi = 0; bi < m.Branches.Count; bi++)
        {
            Branch br = m.Branches[bi];
            if (!br.InService) continue;
            if ((br.TapRatio != 0 && br.TapRatio != 1.0) || br.PhaseShiftDeg != 0)
                throw new ScriptException(ErrorPhase.Execution, default, $"EMT THREE_PHASE: line \"{_case.LineNames[bi]}\" has a tap or phase shift, which the three-phase engine has no element for");
            double rs = br.R, ls = br.X / w, rm = 0, lm = 0;
            if (br.R0 != 0 || br.X0 != 0)
            {
                // Sequence impedances to the coupled form: Z1 = Zs - Zm, Z0 = Zs + 2 Zm.
                rs = (2 * br.R + br.R0) / 3; rm = (br.R0 - br.R) / 3;
                ls = (2 * br.X + br.X0) / 3 / w; lm = (br.X0 - br.X) / 3 / w;
            }
            branches.Add(new ThreePhaseBranch(index[br.From], index[br.To], rs, ls, rm, lm));
            if (br.B != 0)
            {
                shunts.Add(new ThreePhaseShunt(index[br.From], C: br.B / 2 / w));
                shunts.Add(new ThreePhaseShunt(index[br.To], C: br.B / 2 / w));
            }
        }
        foreach (Bus b in m.Buses)
        {
            if (b.Pd < 0)
                throw new ScriptException(ErrorPhase.Execution, default, $"EMT THREE_PHASE: bus \"{_case.BusNames[index[b.Id]]}\" has negative demand, which a constant-impedance load cannot represent");
            if (baseMva > 0 && (b.Pd != 0 || b.Qd != 0))
            {
                double q = Math.Abs(b.Qd) / baseMva;
                shunts.Add(new ThreePhaseShunt(index[b.Id], G: b.Pd / baseMva,
                    C: b.Qd < 0 && q > 0 ? q / w : 0, L: b.Qd > 0 && q > 0 ? 1.0 / (q * w) : 0));
            }
            if (baseMva > 0 && (b.Gs != 0 || b.Bs != 0))
                shunts.Add(new ThreePhaseShunt(index[b.Id], G: b.Gs / baseMva, C: b.Bs > 0 ? b.Bs / baseMva / w : 0, L: b.Bs < 0 ? 1.0 / (-b.Bs / baseMva * w) : 0));
        }
        int src = Settings.SourceBus is { } sb ? _case.BusIndex[sb]
            : index[m.Generators.Count > 0 ? m.Generators[0].Bus : m.Buses[0].Id];
        int probe = Settings.ProbeBus is { } pb ? _case.BusIndex[pb] : 0;

        // A fault at every bus, never applied until the script says so, so the network is the same whatever faults come later.
        const double never = 1e30;
        var faultTimes = new (double On, double Off)[m.Buses.Count];
        for (int i = 0; i < faultTimes.Length; i++) faultTimes[i] = (never, 2 * never);
        foreach (ReplayEvent e in Events.OrderBy(x => x.TimeUs).ThenBy(x => x.Order))
        {
            var f = (FaultEffect)e.Effect;
            int bi = index[f.BusId];
            // An event acts from the step that begins at its time; the engine applies a fault while computing the step that ends at t.
            if (f.On) faultTimes[bi] = (Seconds(e.TimeUs) + dt, 2 * never);
            else faultTimes[bi] = (faultTimes[bi].On, Seconds(e.TimeUs) + dt);
        }
        var faults = new List<ThreePhaseFault>();
        for (int i = 0; i < faultTimes.Length; i++)
            faults.Add(new ThreePhaseFault(i, [0, 1, 2], 1e-6, faultTimes[i].On, faultTimes[i].Off));
        var net = new ThreePhaseNetwork(m.Buses.Count, [new ThreePhaseSource(src, 1.0, f0)], branches, shunts, faults);
        var engine = new ThreePhaseEmtEngine(net, dt, f0);
        (double[] _, double[][] vabc) = engine.Simulate(Seconds(to), probe);

        int steps = checked((int)(to / StepUs));
        var vectors = new double[steps + 1][];
        for (int k = 0; k <= steps; k++) vectors[k] = vabc[k];
        var final = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        string probeName = _case.BusNames[probe];
        string[] ph = ["a", "b", "c"];
        var peaks = new double[3];
        for (int k = 0; k <= steps; k++) for (int c = 0; c < 3; c++) peaks[c] = Math.Max(peaks[c], Math.Abs(vectors[k][c]));
        for (int c = 0; c < 3; c++) final.Add(new($"v{ph[c]}:{probeName}", R(vectors[steps][c])));
        var report = ImmutableArray.CreateBuilder<string>();
        report.Add($"t {Clock(to)}, three-phase EMT at {Clock(StepUs)} steps, source at bus \"{_case.BusNames[src]}\"; phase voltages (pu) at bus \"{probeName}\" and their largest magnitude since t = 0");
        for (int c = 0; c < 3; c++)
            report.Add($"  phase {ph[c]} v {vectors[steps][c].ToString("F6", CultureInfo.InvariantCulture)} pu, peak {peaks[c].ToString("F6", CultureInfo.InvariantCulture)} pu");
        report.Add($"events so far: {Events.Count}");
        string pk = string.Join(", ", peaks.Select(x => x.ToString("F6", CultureInfo.InvariantCulture)));
        return new Run(vectors, final.ToImmutable(), $"bus \"{probeName}\" phase peaks {pk} pu", report.ToImmutable());
    }
}
