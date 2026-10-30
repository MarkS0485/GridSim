using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// Partitions a grid's generators into speed domains (LOS sec. 2.7/sec. 5.5) and computes the GVad dominance
/// metric. GridSim-owned physics: reads per-generator H / MBase / Type / Pg already on the model
/// (supplied by GDA), maps type -> pole/speed via <see cref="PoleMap"/>, groups by (poles, kind), and
/// aggregates. No new GDA data is required.
/// </summary>
public static class DomainAssignment
{
    /// <summary>
    /// Assign speed domains for <paramref name="model"/>. When <c>cfg.SingleDomain</c> is set, every
    /// machine collapses into one synchronous domain - the lumped degenerate limit used by the T0 gate.
    /// </summary>
    public static (IReadOnlyList<SpeedDomain> domains, DominanceReport dominance) Assign(
        GridModel model, DistributedConfig? cfg = null)
    {
        cfg ??= new DistributedConfig();
        var gens = model.Generators;

        var groups = new Dictionary<(int poles, DomainKind kind), List<int>>();
        for (int i = 0; i < gens.Count; i++)
        {
            PoleMap.MachineSpeed sp = PoleMap.ForType(gens[i].Type);
            int poles = sp.Poles;
            DomainKind kind = sp.Kind;
            // A machine that carries rotational inertia (H > 0) is synchronous even if its type string
            // is unknown/blank (e.g. the WSCC case9 generators): default it to a 2-pole turbo-alternator.
            // R239 (October 2026 review, stream B1; Ruling R1: Mark delegated EE calls 2026-10-04): ONLY for an unknown
            // type. The override used to fire for every type PoleMap mapped to zero poles, including the ones it classifies
            // explicitly as converters (storage / battery / bess as synthetic inertia; wind, solar, interconnector as PLL),
            // so a battery whose data carried a synthetic-inertia H was merged into the turbo-alternator domain as real
            // rotating mass. A known converter type now stays in its own (non-swing) domain whatever its H. No bundled
            // case has a converter type with H > 0 (gb-full, gb-synthetic checked), so no shipped number moves.
            if (gens[i].H > 0.0 && poles == 0 && !sp.IsKnownType)
            {
                poles = 2;
                kind = DomainKind.Synchronous;
            }
            (int poles, DomainKind kind) key = cfg.SingleDomain
                ? (2, DomainKind.Synchronous)               // force one coherent domain
                : (poles, kind);
            if (!groups.TryGetValue(key, out List<int>? list))
                groups[key] = list = new List<int>();
            list.Add(i);
        }

        var domains = new List<SpeedDomain>();
        int id = 0;
        foreach (KeyValuePair<(int poles, DomainKind kind), List<int>> kv in
                 groups.OrderBy(k => k.Key.poles).ThenBy(k => (int)k.Key.kind))
        {
            List<int> members = kv.Value;
            double e = members.Sum(i => gens[i].H * gens[i].MBase);       // MW.s
            double s = members.Sum(i => gens[i].MBase);                    // MVA
            double gvad = members.Sum(i => gens[i].MBase * Availability(gens[i]));
            double c = s > 0 ? gvad / s : 0.0;
            // Synchronous speed n = 120 * f / poles (rpm). R196 remainder (D2 nominal frequency, October 2026; Ruling R1:
            // Mark delegated EE calls 2026-10-04): this was 6000 / poles, the 50 Hz value, whatever the model ran at, so a
            // 60 Hz two-pole machine was labelled 3000 rpm instead of 3600. It now takes the model's FrequencyHz. At 50 Hz
            // 120.0 * 50.0 is exactly 6000.0, so the value is bit-identical for every 50 Hz model. The rpm is a label
            // (the domain grouping is by pole count and kind, not by speed), so no frequency trajectory moves.
            double rpm = kv.Key.poles > 0 ? 120.0 * model.FrequencyHz / kv.Key.poles : 0.0;
            domains.Add(new SpeedDomain(id++, kv.Key.poles, rpm, kv.Key.kind, members, e, s, gvad, c));
        }

        return (domains, BuildDominance(domains, cfg.DominanceThreshold));
    }

    /// <summary>
    /// Availability / coupling coefficient C  in  [0,1] from dispatch (|Pg| / MBase) - a proxy for the
    /// LOS "availability coefficient reflecting online status and coupling strength". Derived from data
    /// already on the generator; no new GDA field.
    /// </summary>
    private static double Availability(Generator g)
    {
        if (g.MBase <= 0.0) return 0.0;
        return Math.Clamp(Math.Abs(g.Pg) / g.MBase, 0.0, 1.0);
    }

    private static DominanceReport BuildDominance(IReadOnlyList<SpeedDomain> domains, double threshold)
    {
        List<SpeedDomain> ordered = domains.OrderByDescending(d => d.Gvad).ToList();
        double delta = 0.0;
        int? dominant = null;

        if (ordered.Count >= 1 && ordered[0].Gvad > 0.0)
        {
            double runnerUp = ordered.Count >= 2 ? ordered[1].Gvad : 0.0;
            if (runnerUp > 0.0)
            {
                delta = (ordered[0].Gvad - runnerUp) / runnerUp;
                if (delta >= threshold) dominant = ordered[0].Id;
            }
            else
            {
                // The runner-up (or the only domain) carries zero inertial dispatch, so the top domain
                // trivially dominates. The old guard required ordered[1].Gvad > 0 and so declared NO
                // dominant domain in exactly this case - inverting the metric.
                delta = double.PositiveInfinity;
                dominant = ordered[0].Id;
            }
        }

        return new DominanceReport(ordered, delta, threshold, dominant);
    }
}
