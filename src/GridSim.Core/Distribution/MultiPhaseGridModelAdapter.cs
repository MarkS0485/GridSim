using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Distribution;

/// <summary>
/// N062 / R059 (October 2026): an adapter from a positive-sequence <see cref="GridModel"/> to a balanced
/// <see cref="MultiPhaseFeeder"/>, so a radial distribution case held as a GridModel (a MATPOWER feeder such as
/// case33bw, a JSON case) can be solved by the three-phase stack.
///
/// The lift is exact for a balanced network: in per-unit on the three-phase base, a positive-sequence quantity is the
/// per-phase quantity on the per-phase base, so each phase carries exactly the positive-sequence data. So:
/// <list type="bullet">
/// <item>a branch r + jx becomes a diagonal 3x3 series impedance (r + jx on every phase, no mutual coupling), and its
/// line charging B a diagonal shunt jB (half at each end, the same pi model);</item>
/// <item>a bus load Pd + jQd becomes the per-phase load (Pd + jQd) / BaseMva on every phase; a bus ZIP load
/// P(V) = Pd (Ap V^2 + Bp V + Cp) becomes three loads, constant-impedance Ap Pd + j Aq Qd, constant-current
/// Bp Pd + j Bq Qd and constant-power Cp Pd + j Cq Qd, which draw exactly the same power at every voltage;</item>
/// <item>a generator at a PQ bus is a constant-power injection (a negative load) Pg + jQg;</item>
/// <item>a bus shunt capacitor Bs above 0 becomes a capacitor bank of Bs / BaseMva per phase;</item>
/// <item>the slack bus is the source, at its voltage setpoint (<see cref="VoltageSetpoint"/>, else its Vm) and its
/// angle VaDeg, rotated by -120 and +120 degrees on phases b and c.</item>
/// </list>
/// What the three-phase sweep cannot represent is REFUSED (an <see cref="ArgumentException"/> naming it), never
/// approximated: a voltage-controlled (PV) bus, an off-nominal tap or a phase shift, a bus shunt conductance or a
/// shunt reactor (Bs below 0), a model that is not one radial tree from a single slack. Out-of-service branches are
/// left out (they carry nothing), as the power flow leaves them out.
/// </summary>
public static class MultiPhaseGridModelAdapter
{
    /// <summary>Lift <paramref name="model"/> into a balanced three-phase feeder (see the class summary).</summary>
    public static MultiPhaseFeeder FromGridModel(GridModel model, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(model);
        string who = $"three-phase adapter ({model.Name})";
        List<Bus> slacks = model.Buses.Where(b => b.Type == BusType.Slack).ToList();
        if (slacks.Count != 1)
            throw new ArgumentException($"{who}: needs exactly one slack bus to be the feeder source (found {slacks.Count}).");
        Bus slack = slacks[0];
        Bus? pv = model.Buses.FirstOrDefault(b => b.Type == BusType.Pv);
        if (pv is not null)
            throw new ArgumentException($"{who}: bus {pv.Id} is voltage-controlled (PV); the three-phase sweep has no voltage control, so it cannot hold that bus's setpoint.");

        double baseMva = model.BaseMva;
        var buses = new List<PhaseBus>(model.Buses.Count);
        var baseKv = new Dictionary<int, double>(model.Buses.Count);
        var loads = new List<PhaseLoad>();
        var loadIds = new List<string>();
        var caps = new List<PhaseCapacitor>();
        var capIds = new List<string>();
        foreach (Bus b in model.Buses)
        {
            // The feeder reports volts and amps from each bus's kV base, so a bus without one is refused rather than
            // shown as 0 V.
            if (!(b.BaseKv > 0))
                throw new ArgumentException($"{who}: bus {b.Id} has no base kV (BaseKv = {b.BaseKv}); the three-phase feeder needs it to report volts and amps.");
            baseKv[b.Id] = b.BaseKv;
            if (b.Id == slack.Id)
            {
                double vm = VoltageSetpoint.ForBus(model, b.Id) ?? b.Vm;
                double va = b.VaDeg * Math.PI / 180.0;
                buses.Add(new PhaseBus(b.Id, true, new[]
                {
                    DetMath.FromPolar(vm, va),
                    DetMath.FromPolar(vm, va - 2.0 * Math.PI / 3.0),
                    DetMath.FromPolar(vm, va + 2.0 * Math.PI / 3.0),
                }));
                continue;   // the slack's own load and shunt are supplied by the infinite source: they change no voltage
            }
            buses.Add(new PhaseBus(b.Id, false));
            if (b.Gs != 0)
                throw new ArgumentException($"{who}: bus {b.Id} has a shunt conductance Gs = {b.Gs} MW, which the three-phase sweep does not model.");
            if (b.Bs < 0)
                throw new ArgumentException($"{who}: bus {b.Id} has a shunt reactor Bs = {b.Bs} Mvar, which the three-phase sweep does not model (only capacitors).");
            if (b.Bs > 0)
            {
                double q = b.Bs / baseMva;
                caps.Add(new PhaseCapacitor(b.Id, new[] { q, q, q }));
                capIds.Add($"bus {b.Id} shunt");
            }
            if (b.Pd == 0 && b.Qd == 0) continue;
            ZipLoad zip = b.Zip ?? new ZipLoad();
            void Add(double p, double q, PhaseLoadModel m, string tag)
            {
                if (p == 0 && q == 0) return;
                var s = new Complex(p / baseMva, q / baseMva);
                loads.Add(m == PhaseLoadModel.ConstantPower ? new PhaseLoad(b.Id, new[] { s, s, s }) : new PhaseLoad(b.Id, new[] { s, s, s }, m));
                loadIds.Add($"bus {b.Id}{tag}");
            }
            Add(zip.Cp * b.Pd, zip.Cq * b.Qd, PhaseLoadModel.ConstantPower, "");
            Add(zip.Bp * b.Pd, zip.Bq * b.Qd, PhaseLoadModel.ConstantCurrent, " (I)");
            Add(zip.Ap * b.Pd, zip.Aq * b.Qd, PhaseLoadModel.ConstantImpedance, " (Z)");
        }
        foreach (Generator g in model.Generators)
        {
            if (g.Bus == slack.Id) continue;
            // A unit at a PQ bus injects its scheduled output: a constant-power load of minus that.
            var s = new Complex(-g.Pg / baseMva, -g.Qg / baseMva);
            if (s == Complex.Zero) continue;
            loads.Add(new PhaseLoad(g.Bus, new[] { s, s, s }));
            loadIds.Add($"generator at bus {g.Bus}");
        }

        var branches = new List<PhaseBranch>();
        var branchIds = new List<string>();
        var branchKinds = new List<string>();
        for (int k = 0; k < model.Branches.Count; k++)
        {
            Branch br = model.Branches[k];
            if (!br.InService) continue;
            // The power flow's own tap rule: a ratio of 0 or below means 1 (AdmittanceStamp).
            double tap = br.TapRatio <= 0 ? 1.0 : br.TapRatio;
            if (tap != 1.0 || br.PhaseShiftDeg != 0)
                throw new ArgumentException($"{who}: branch {k + 1} ({br.From}-{br.To}) has tap {br.TapRatio} / phase shift {br.PhaseShiftDeg} deg; " +
                                            "the three-phase sweep models rated-ratio branches only.");
            var z = new Complex(br.R, br.X);
            PhaseImpedance? y = br.B != 0 ? PhaseImpedance.Diagonal(new Complex(0.0, br.B)) : null;
            branches.Add(new PhaseBranch(br.From, br.To, PhaseImpedance.Diagonal(z), Phase.Abc, y));
            branchIds.Add(br.Name ?? $"#{k + 1}");
            branchKinds.Add(br.IsTransformer ? "transformer" : "line");
        }

        var mp = new MultiPhaseModel(buses, branches, loads, baseMva, caps);
        // The sweep reports a non-radial or disconnected model; refuse it here so the adapter never hands one on.
        int n = buses.Count;
        if (branches.Count != n - 1)
            throw new ArgumentException($"{who}: the in-service network is not one radial tree ({n} buses need {n - 1} branches, found {branches.Count}).");
        var adj = new Dictionary<int, List<int>>();
        foreach (PhaseBus b in buses) adj[b.Id] = new List<int>();
        foreach (PhaseBranch br in branches)
        {
            if (!adj.TryGetValue(br.From, out List<int>? fromList) || !adj.TryGetValue(br.To, out List<int>? toList))
                throw new ArgumentException($"{who}: branch {br.From}-{br.To} names a bus that is not in the case.");
            fromList.Add(br.To);
            toList.Add(br.From);
        }
        var seen = new HashSet<int> { slack.Id };
        var queue = new Queue<int>();
        queue.Enqueue(slack.Id);
        while (queue.Count > 0)
            foreach (int o in adj[queue.Dequeue()])
                if (seen.Add(o)) queue.Enqueue(o);
        if (seen.Count != n)
            throw new ArgumentException($"{who}: {n - seen.Count} bus(es) are not reached from the slack {slack.Id} (with n - 1 branches, a loop elsewhere); the sweep needs one radial tree.");
        return new MultiPhaseFeeder(mp, sourceName, model.Name,
            $"Balanced three-phase lift of the positive-sequence case {model.Name} (MultiPhaseGridModelAdapter).",
            baseMva, baseKv, branchIds, branchKinds, loadIds)
        {
            CapacitorIds = capIds,
        };
    }
}
