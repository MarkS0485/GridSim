using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// The experimental distributed / multi-domain inertia model (M1 MVP). Assigns generators to speed
/// domains (<see cref="DomainAssignment"/>) and integrates the domain-reduced multi-area swing
/// (<see cref="MultiDomainSwing"/>), returning the COI-collapsed <see cref="FrequencyResponse"/> so every
/// existing scalar consumer keeps working, and stashing the per-domain detail in
/// <see cref="LastDistributedResult"/>.
///
/// The degenerate case (<= 1 synchronous swing mass, or a forced single domain) delegates to the lumped
/// <see cref="FrequencyDynamics"/> so the reduction to the lumped model is exact - the T0 gate.
/// <para><b>One instance, one caller at a time</b> (GR-544): <see cref="Simulate"/> stores its spatial detail for
/// <see cref="LastDistributedResult"/>, which describes the most recent call on this instance. Use one instance per
/// thread (or per concurrent study), and read <see cref="LastDistributedResult"/> straight after the call it belongs to.</para>
/// </summary>
public sealed class DistributedInertiaModel : IInertiaModel
{
    private DistributedResult? _last;

    /// <inheritdoc/>
    public DistributedResult? LastDistributedResult => _last;

    /// <inheritdoc/>
    public double CoiInertiaGvaS(GridModel model) => model.SystemInertiaGvaS;

    /// <inheritdoc/>
    public FrequencyResponse Simulate(
        GridModel model, double baseDemandMw, IEnumerable<FrequencyEvent> events, DynamicsConfig? config = null)
    {
        // N275 (October 2026, stream B1): the model's nominal (see SwingKernel.ConfigForModel).
        DynamicsConfig cfg = SwingKernel.ConfigForModel(model, config, "DistributedInertiaModel.Simulate");
        DistributedConfig dist = cfg.Distributed ?? new DistributedConfig();
        FrequencyEvent[] given = events.ToArray();   // enumerated once: placed below, then integrated

        // R060: an event placed at a bus the model does not have is refused on every path (the degenerate and forming
        // paths have nowhere else to put it, but a wrong bus id is still a wrong input).
        foreach (FrequencyEvent x in given)
            if (x.AtBus is { } bus && !model.Buses.Any(b => b.Id == bus))
                throw new ArgumentException(
                    $"A frequency event (t = {x.AtSeconds} s, {x.DeltaPMw} MW) is placed at bus {bus}, which the model '{model.Name}' does not have.",
                    nameof(events));

        (IReadOnlyList<SpeedDomain> domains, DominanceReport dominance) = DomainAssignment.Assign(model, dist);
        List<SpeedDomain> swingMasses = domains.Where(d => d.IsSwingMass).ToList();

        // Grid-forming has no multi-domain analogue yet (MultiDomainSwing knows nothing of the virtual
        // former), so ANY forming run delegates to the lumped forming integrator - placed BEFORE the
        // swing-mass count branch so a >= 2-domain network cannot silently reach MultiDomainSwing and
        // drop the former. Multi-domain forming is A2. Virtual inertia is NEVER distributed as real mass.
        if (cfg.Forming != FormingMode.None)
        {
            FrequencyResponse formingResp = FrequencyDynamics.Simulate(
                model.SystemInertiaGvaS * 1000.0, baseDemandMw, given, cfg);
            int fid = swingMasses.Count >= 1 ? swingMasses[0].Id : 0;
            var formingTrace = new DomainTrace(
                fid, formingResp.InitialRocofHzPerS, formingResp.NadirHz, formingResp.NadirSeconds, formingResp.SettleHz, formingResp.Points);
            _last = new DistributedResult(new[] { formingTrace }, dominance, BusRocofMap(model, new[] { formingTrace }, swingMasses), null,
                LumpedPlacements(given, fid, "a grid-forming run is integrated on the lumped centre-of-inertia swing"));
            return formingResp;
        }

        // Degenerate limit: delegate to the lumped model for an exact (bitwise) reduction.
        if (swingMasses.Count <= 1)
        {
            FrequencyResponse resp = FrequencyDynamics.Simulate(
                model.SystemInertiaGvaS * 1000.0, baseDemandMw, given, cfg);
            int id = swingMasses.Count == 1 ? swingMasses[0].Id : 0;
            var trace = new DomainTrace(id, resp.InitialRocofHzPerS, resp.NadirHz, resp.NadirSeconds, resp.SettleHz, resp.Points);
            _last = new DistributedResult(new[] { trace }, dominance, BusRocofMap(model, new[] { trace }, swingMasses), null,
                LumpedPlacements(given, id, swingMasses.Count == 1
                    ? "the network has one swing-mass speed domain, so the run is the lumped centre-of-inertia swing"
                    : "the network has no swing-mass speed domain, so the run is the lumped centre-of-inertia swing"));
            return resp;
        }

        // Physically-derived inter-domain coupling (Kron-reduced synchronising susceptance), in swing-mass order.
        // R025: the coupling comes with where it came from (Kron, or the uniform heuristic and why), carried on the result.
        (double[,] couplingK, CouplingReport coupling) = DomainCoupling.Synchronising(model, swingMasses, dist);
        (double[,] tau, double waveSpeed, double maxDelay, IReadOnlyList<int> unlocated) = PropagationDelay.ComputeWithLocation(model, swingMasses, dist);

        // R060: where each event lands, resolved from the model (see PlaceEvents).
        IReadOnlyList<EventPlacement> placements = PlaceEvents(model, swingMasses, given);
        double[][] shares = placements.Select(p => p.Shares.ToArray()).ToArray();

        MultiDomainSwing.Result result = MultiDomainSwing.Simulate(domains, couplingK, tau, baseDemandMw, given, cfg, dist, shares);

        PropagationReport? propagation = dist.EnablePropagation
            ? new PropagationReport(waveSpeed, maxDelay, result.DomainArrivalSeconds, unlocated.Select(d => swingMasses[d].Id).ToArray())
            : null;
        _last = new DistributedResult(result.Domains, dominance, BusRocofMap(model, result.Domains, swingMasses), propagation, placements, coupling);
        return result.Coi;
    }

    /// <summary>
    /// Bug hunt 2026-10-29, finding 21: the placements of the delegating (forming and single-domain) paths. They have one
    /// mass, so every event acts wholly on it and an event's <see cref="FrequencyEvent.AtBus"/> cannot change anything;
    /// the location used to be checked against the bus list and then dropped with nothing in the result saying so
    /// (EventPlacements was null). Each event is now recorded with the single domain taking the whole step and a rule
    /// that says why, and that a given location was not used. The integration itself is untouched.
    /// </summary>
    private static IReadOnlyList<EventPlacement> LumpedPlacements(IReadOnlyList<FrequencyEvent> events, int domainId, string why)
    {
        var list = new List<EventPlacement>(events.Count);
        foreach (FrequencyEvent x in events)
        {
            string rule = x.AtBus is { } bus
                ? $"{why}: the whole step acts on speed domain {domainId}; its location (bus {bus}) is not used"
                : $"{why}: the whole step acts on speed domain {domainId}";
            list.Add(new EventPlacement(x.AtSeconds, x.DeltaPMw, x.AtBus, new[] { domainId }, new[] { 1.0 }, rule));
        }
        return list;
    }

    /// <summary>
    /// R060 (October 2026 review, stream B1; Ruling R1: Mark delegated EE calls 2026-10-04): place every event on the
    /// swing-mass domains. Before R060 every event landed on the domain with the largest summed rated MVA, whatever was
    /// lost and wherever, although the code called it "the domain hosting the single largest unit"; all per-domain
    /// nadirs and RoCoFs and the per-bus RoCoF map depend on this choice.
    /// <list type="bullet">
    /// <item>An event with <see cref="FrequencyEvent.AtBus"/> is placed by <see cref="DomainCoupling.ImpactShares"/>:
    ///   wholly on the domain whose machines sit at that bus, else shared by synchronising coupling to the impact bus
    ///   (the distribution of a power impact, Anderson and Fouad). Fails loud when it cannot be placed.</item>
    /// <item>An event with no location lands wholly on the domain hosting the single largest unit (the largest MBase among
    ///   the swing-mass members; the first in domain then member order on a tie), the credible-loss home the code always
    ///   documented. On every bundled and test model with one unit per domain, or where the largest unit sits in the
    ///   domain with the largest total MVA, this is the same domain as before, so those numbers do not move.</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<EventPlacement> PlaceEvents(
        GridModel model, IReadOnlyList<SpeedDomain> masses, IReadOnlyList<FrequencyEvent> events)
    {
        int n = masses.Count;
        int[] ids = masses.Select(m => m.Id).ToArray();

        // The credible-loss home: the domain of the single largest unit.
        int home = 0, homeGen = -1;
        double homeMBase = double.NegativeInfinity;
        for (int d = 0; d < n; d++)
            foreach (int gi in masses[d].MemberGenIndices)
                if (model.Generators[gi].MBase > homeMBase) { homeMBase = model.Generators[gi].MBase; home = d; homeGen = gi; }
        string homeRule = homeGen >= 0
            ? $"no location given: the step lands on speed domain {ids[home]}, which hosts the largest single unit "
              + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({homeMBase:0.###} MVA at bus {model.Generators[homeGen].Bus}), the credible-loss home")   // GR-543: same text on every locale
            : $"no location given: the step lands on speed domain {ids[home]}";

        var placements = new List<EventPlacement>(events.Count);
        foreach (FrequencyEvent x in events)
        {
            double[] shares;
            string rule;
            if (x.AtBus is { } bus)
                (shares, rule) = DomainCoupling.ImpactShares(model, masses, bus);
            else
            {
                shares = new double[n];
                shares[home] = 1.0;
                rule = homeRule;
            }
            placements.Add(new EventPlacement(x.AtSeconds, x.DeltaPMw, x.AtBus, ids, shares, rule));
        }
        return placements;
    }

    /// <summary>Map each generator bus to the event RoCoF of the domain that owns it (COI value in the degenerate case).</summary>
    private static IReadOnlyDictionary<int, double> BusRocofMap(
        GridModel model, IReadOnlyList<DomainTrace> traces, IReadOnlyList<SpeedDomain> masses)
    {
        var map = new Dictionary<int, double>();
        if (masses.Count == traces.Count && masses.Count > 0)
        {
            for (int d = 0; d < masses.Count; d++)
                foreach (int gi in masses[d].MemberGenIndices)
                    map[model.Generators[gi].Bus] = traces[d].InitialRocofHzPerS;
        }
        else if (traces.Count > 0)
        {
            double r = traces[0].InitialRocofHzPerS;
            foreach (Generator g in model.Generators) map[g.Bus] = r;
        }
        return map;
    }
}
