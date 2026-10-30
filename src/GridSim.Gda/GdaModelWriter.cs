using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Model;

namespace GridSim.Gda;

/// <summary>
/// Materialises a built <see cref="GridModel"/> to the standard GridSim multi-file
/// JSON case format (the same files <c>JsonGridLoader</c> reads), so the full GDA
/// replica is usable by every existing tool  -  <c>--dir</c>, the live loop, the
/// visualiser, N-1 screening. A grid-infeed slack generator is added at the chosen
/// slack bus to represent the transmission supergrid feeding the DNO network.
///
/// Output is written to a git-ignored directory: the derived grid data never enters
/// the repository.
///
/// <para>N212 (Ruling R1 follow-up, 2026-10-04: lossless). Every generator field a JSON v1 case can carry is written
/// when it differs from what the loader assumes when the key is absent, so a written model reloads with the same
/// generators: <c>pMinMw</c> (absent = 0), <c>pMaxMw</c> (absent = the capacity), <c>qOutputMvar</c> (absent = 0),
/// <c>dynamicsClass</c>, <c>converterInterfaced</c>, <c>powerFlowOnly</c>, <c>unitTransformerXPu</c>,
/// <c>gridForming</c>, and <c>inService: false</c> for an out-of-service unit (written with the whole fleet,
/// <see cref="GridModel.AllGenerators"/>, as JsonCaseWriter does). An unlimited limit is written as the string
/// "Infinity" / "-Infinity", which the loader reads (it used to stop the writer with an exception). At their defaults
/// none of these keys is written, so every existing GDA and synth output is byte-identical to before.
/// What JSON v1 cannot hold at all (a generator's subtransient X''d and negative-sequence reactance, and gencost
/// curves) is never dropped silently: <see cref="WriteDirectory"/> returns a note for it, which the CLI prints.</para>
/// </summary>
public static class GdaModelWriter
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="model">The model to materialise.</param>
    /// <param name="dir">Output case directory.</param>
    /// <param name="addGridInfeed">
    /// True (GDA networks): insert a grid-infeed slack generator at each slack bus  - 
    /// the transmission supergrid feeding a DNO footprint. False (self-contained
    /// models, e.g. the synthetic GB case): the model's own generator on the slack
    /// bus is written with the slack role instead.
    /// </param>
    /// <returns>Notes on what JSON v1 cannot carry (empty when nothing was lost); see the class remarks (N212).</returns>
    public static IReadOnlyList<string> WriteDirectory(GridModel model, string dir, bool addGridInfeed = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        // GR-331: every file is serialised in memory first, so a refusal (a NaN the serialiser or Limit refuses) touches
        // nothing on disk; it used to leave this run's first files beside the previous run's others, a case that never
        // existed. Each file is then written to a temporary name and moved over its target.
        var pending = new List<(string File, string Json)>();
        var notes = new List<string>();

        Stage(pending, "system.json", new { name = model.Name, baseMva = model.BaseMva, frequencyHz = model.FrequencyHz });

        var buses = model.Buses.Select(b => new
        {
            id = b.Id,
            name = b.Name,
            baseKv = b.BaseKv,
            type = b.Type switch { BusType.Slack => "slack", BusType.Pv => "pv", _ => "pq" },
            vm = b.Vm,
            vaDeg = b.VaDeg,
            gs = b.Gs,
            bs = b.Bs,
            x = b.X,
            y = b.Y,
            // GR-332: the voltage-dependent load and the per-bus voltage limits a v1 bus carries, written only when the
            // model sets them (null is not written: WhenWritingNull), so a GDA build that sets none is byte-identical.
            // Before, they were dropped silently and the written case judged and solved differently from the model.
            zip = b.Zip is ZipLoad z ? new { ap = z.Ap, bp = z.Bp, cp = z.Cp, aq = z.Aq, bq = z.Bq, cq = z.Cq } : null,
            vminPu = b.VminPu,
            vmaxPu = b.VmaxPu,
        });
        Stage(pending, "buses.json", buses);

        // Embedded generators (dispatched at nameplate x the fuel capacity factor, GdaModelBuilder; GR-833: not at
        // nameplate); with addGridInfeed, a grid-infeed
        // slack at every slack bus (one per island  -  a whole DNO footprint is several
        // islands). Without it, the model's own first generator at each slack bus takes
        // the slack role.
        var slackBuses = model.Buses.Where(b => b.Type == BusType.Slack).Select(b => b.Id).ToHashSet();
        var pqBuses = model.Buses.Where(b => b.Type == BusType.Pq).Select(b => b.Id).ToHashSet();
        var slackTaken = new HashSet<int>();
        // N212: the whole fleet, out-of-service units included (written with inService: false). With every unit in
        // service AllGenerators is Generators, in the same order, so the output is unchanged.
        var gens = model.AllGenerators.Select(g => new
        {
            name = g.Name,
            bus = g.Bus,
            // N203 (October 2026): the converter fault-current factor when the model sets one (0 = a line-commutated
            // HVDC link: no fault current), so the synthetic GB's LCC markings survive `gridsim synth` (the folder
            // data/gb-synthetic carried them by hand). Null is not written (WhenWritingNull), so every GDA export, whose
            // builder never sets it, is byte-identical to before.
            converterFaultCurrentPu = g.ConverterFaultCurrentPu,
            type = g.Type,
            capacityMva = g.MBase,
            outputMw = g.Pg,
            voltageSetpoint = g.Vg,
            // N212: a finite limit is the same number as before; an unlimited one is "Infinity" / "-Infinity".
            qMinMvar = Limit(g.Qmin),
            qMaxMvar = Limit(g.Qmax),
            inertiaH = g.H,
            // a generator on a PQ bus is a fixed injection (embedded plant)  -  keep it that way on reload. An
            // out-of-service unit never takes the slack role (the loader ignores its role, so the slack must be a
            // unit that is in service).
            role = !addGridInfeed && g.InService && slackBuses.Contains(g.Bus) && slackTaken.Add(g.Bus) ? "slack"
                 : pqBuses.Contains(g.Bus) ? "pq" : "pv",
            // ---- N212: the remaining fields a v1 case carries, written only when not at the loader's default -------
            // (null is not written: WhenWritingNull). Bit comparisons, so even -0.0 is kept exactly.
            qOutputMvar = SameBits(g.Qg, 0.0) ? (double?)null : g.Qg,
            pMinMw = SameBits(g.Pmin, 0.0) ? (double?)null : g.Pmin,
            pMaxMw = SameBits(g.Pmax, g.MBase) ? null : Limit(g.Pmax),
            dynamicsClass = g.DynamicsClass,
            converterInterfaced = g.ConverterInterfaced,
            powerFlowOnly = g.PowerFlowOnly ? true : (bool?)null,
            unitTransformerXPu = SameBits(g.UnitTransformerXPu, 0.0) ? (double?)null : g.UnitTransformerXPu,
            gridForming = g.GridForming ? true : (bool?)null,
            inService = g.InService ? (bool?)null : false,
        }).ToList<object>();

        // N212: what a v1 generation.json cannot hold is reported, never dropped silently (the JsonCaseWriter rule).
        int lostReactances = model.AllGenerators.Count(g => g.SubtransientXdPu != 0 || g.NegativeSeqXPu != 0);
        int lostCosts = model.AllGenerators.Count(g => g.Cost is not null || g.ReactiveCost is not null);
        if (lostReactances > 0)
            notes.Add($"{lostReactances} generator(s) carry a subtransient (X''d) or negative-sequence reactance, which a JSON v1 case cannot hold; the fault studies of the written case use the synthesised defaults for them.");
        if (lostCosts > 0)
            notes.Add($"{lostCosts} generator(s) carry a MATPOWER cost curve (gencost), which a JSON v1 case cannot hold; no solver uses it today.");
        if (addGridInfeed)
        {
            // R200 (ruled 2026-10-02 evening, Q10): each infeed is sized on the demand of ITS OWN island (the
            // connected part of the network its slack bus sits in), not the whole network. Before, every GSP slack
            // got 5 x max(1,000 MW, total network demand), so a ten-island DNO footprint wrote ten infeeds each
            // sized for the whole licence area. A single-island model is unchanged (island demand = total demand).
            IReadOnlyDictionary<int, double> islandDemand = IslandDemandBySlack(model);
            int inserted = 0;
            foreach (Bus s in model.Buses.Where(b => b.Type == BusType.Slack))
            {
                double infeed = GridInfeedSizingMw(islandDemand[s.Id]);
                gens.Insert(inserted++, new
                {
                    name = $"Grid infeed @ {s.Name}",
                    bus = s.Id,
                    type = "grid",
                    capacityMva = infeed * GdaBuildDefaults.GridInfeedHeadroomFactor,
                    outputMw = 0.0,
                    voltageSetpoint = 1.0,
                    qMinMvar = -infeed * GdaBuildDefaults.GridInfeedHeadroomFactor,
                    qMaxMvar = infeed * GdaBuildDefaults.GridInfeedHeadroomFactor,
                    inertiaH = 0.0,
                    role = "slack",
                });
            }
        }
        Stage(pending, "generation.json", gens);

        var demand = model.Buses.Where(b => b.Pd != 0 || b.Qd != 0).Select(b => new
        {
            name = b.Name,
            bus = b.Id,
            pMw = b.Pd,
            qMvar = b.Qd,
        });
        Stage(pending, "demand.json", demand);

        // GR-822: JSON v1 has no in-service flag on a branch, so an out-of-service branch used to be written (and reloaded)
        // as in service. It is left out instead, and said, as the Core JSON case writer does.
        int outOfService = model.Branches.Count(b => !b.InService);
        if (outOfService > 0)
            notes.Add($"{outOfService} out-of-service branch(es) not written (JSON v1 has no in-service flag on a branch, so they would load as in service).");
        var lines = model.Branches.Where(b => b.InService && !b.IsTransformer).Select(b => new
        {
            name = b.Name,
            from = b.From,
            to = b.To,
            rPu = b.R,
            xPu = b.X,
            bPu = b.B,
            ratingMva = b.RateAMva,
            circuits = 1,
            // GR-332: the remaining v1 line fields, written only when not at the loader's default (bit comparisons).
            ratingBMva = SameBits(b.RateBMva, 0.0) ? (double?)null : b.RateBMva,
            ratingCMva = SameBits(b.RateCMva, 0.0) ? (double?)null : b.RateCMva,
            tapRatio = SameBits(b.TapRatio, 1.0) ? (double?)null : b.TapRatio,
            phaseShiftDeg = SameBits(b.PhaseShiftDeg, 0.0) ? (double?)null : b.PhaseShiftDeg,
            rPu0 = SameBits(b.R0, 0.0) ? (double?)null : b.R0,
            xPu0 = SameBits(b.X0, 0.0) ? (double?)null : b.X0,
        });
        Stage(pending, "transmission.json", lines);

        var txs = model.Branches.Where(b => b.InService && b.IsTransformer).Select(b => new
        {
            name = b.Name,
            from = b.From,
            to = b.To,
            rPu = b.R,
            xPu = b.X,
            tapRatio = b.TapRatio,
            phaseShiftDeg = b.PhaseShiftDeg,
            ratingMva = b.RateAMva,
            // GR-332: short-term and emergency ratings, written only when given.
            ratingBMva = SameBits(b.RateBMva, 0.0) ? (double?)null : b.RateBMva,
            ratingCMva = SameBits(b.RateCMva, 0.0) ? (double?)null : b.RateCMva,
        });
        Stage(pending, "transformer.json", txs);
        // GR-332: what a v1 transformer.json row cannot hold is reported, never dropped silently.
        int lostTxShunt = model.Branches.Count(b => b.InService && b.IsTransformer && !SameBits(b.B, 0.0));
        int lostZeroSeq = model.Branches.Count(b => b.InService && (b.FromWinding is not null || b.ToWinding is not null
            || b.FromNeutralXPu != 0 || b.ToNeutralXPu != 0 || (b.IsTransformer && (b.R0 != 0 || b.X0 != 0))));
        if (lostTxShunt > 0)
            notes.Add($"{lostTxShunt} transformer(s) carry a shunt susceptance (B), which a JSON v1 transformer row cannot hold; the written case has none for them.");
        if (lostZeroSeq > 0)
            notes.Add($"{lostZeroSeq} branch(es) carry transformer winding connections, neutral reactances or transformer zero-sequence impedances, which JSON v1 cannot hold; earth-fault studies of the written case will differ.");

        var oltcs = model.Oltcs.Select(o => new
        {
            transformer = o.Transformer,
            controlsBus = o.ControlsBus,
            targetVpu = o.TargetVpu,
            minTap = o.MinTap,
            maxTap = o.MaxTap,
            stepPu = o.StepPu,
            deadbandPu = o.DeadbandPu,
        });
        Stage(pending, "onloadtapchanger.json", oltcs);
        Directory.CreateDirectory(dir);
        foreach ((string file, string json) in pending)
        {
            string target = Path.Combine(dir, file);
            string temp = target + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, target, overwrite: true);
        }
        return notes;
    }

    /// <summary>N212: a generator limit as the writer serialises it: the number itself when finite, the strings
    /// "Infinity" / "-Infinity" (which the loader's InfinityTolerantDoubleConverter reads) when unlimited. NaN is
    /// refused: it is not a limit, and writing it would hand the loader a value it rejects.</summary>
    private static object Limit(double v)
    {
        if (double.IsNaN(v)) throw new InvalidDataException("A generator limit is NaN; the GDA writer refuses to write it.");
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        return v;
    }

    /// <summary>Exact (bit-pattern) equality, so -0.0 is not mistaken for the default +0.0.</summary>
    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>
    /// The demand basis (MW) a grid infeed is sized on: max(<see cref="GdaBuildDefaults.GridInfeedFloorMw"/>, the
    /// demand of the island it feeds). The written capacity and +/- reactive limit are this times
    /// <see cref="GdaBuildDefaults.GridInfeedHeadroomFactor"/>. Fails loud on a non-finite island demand.
    /// </summary>
    public static double GridInfeedSizingMw(double islandDemandMw)
    {
        if (!double.IsFinite(islandDemandMw))
            throw new ArgumentOutOfRangeException(nameof(islandDemandMw), islandDemandMw, "Island demand must be finite.");
        return Math.Max(GdaBuildDefaults.GridInfeedFloorMw, islandDemandMw);
    }

    /// <summary>
    /// For every slack bus, the total demand (sum of bus Pd, MW) of the island it sits in. Islands are the connected
    /// components of the bus graph over EVERY branch the writer writes (it writes all branches, so the reloaded model
    /// has exactly these islands). Several slacks in one island (one per GSP group) each get that island's whole
    /// demand: any one GSP may have to carry the island, and the infeed must never bind.
    /// Fails loud on a branch that names a bus the model does not have.
    /// </summary>
    public static IReadOnlyDictionary<int, double> IslandDemandBySlack(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Union-find over bus ids (path halving; union by smaller root id, so the result is deterministic).
        var parent = new Dictionary<int, int>(model.Buses.Count);
        foreach (Bus b in model.Buses) parent[b.Id] = b.Id;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (Branch br in model.Branches)
        {
            if (!parent.ContainsKey(br.From) || !parent.ContainsKey(br.To))
                throw new InvalidOperationException($"Branch '{br.Name}' ({br.From} -> {br.To}) names a bus the model does not have.");
            int a = Find(br.From), c = Find(br.To);
            if (a != c) { if (a < c) parent[c] = a; else parent[a] = c; }
        }

        // Island demand: sum Pd per root, in bus order (a fixed summation order, so the figure is reproducible).
        var demandByRoot = new Dictionary<int, double>();
        foreach (Bus b in model.Buses)
        {
            int r = Find(b.Id);
            demandByRoot[r] = (demandByRoot.TryGetValue(r, out double d) ? d : 0.0) + b.Pd;
        }

        var result = new Dictionary<int, double>();
        foreach (Bus b in model.Buses.Where(b => b.Type == BusType.Slack))
            result[b.Id] = demandByRoot[Find(b.Id)];
        return result;
    }

    private static void Stage(List<(string File, string Json)> pending, string file, object payload) =>
        pending.Add((file, JsonSerializer.Serialize(payload, Opt)));
}
