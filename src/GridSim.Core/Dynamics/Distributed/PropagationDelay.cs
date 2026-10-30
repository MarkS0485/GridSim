using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// First-order electromechanical propagation-delay model between speed domains. A disturbance does not
/// reach the whole system at once: it spreads at a finite wave speed, so distant domains respond later.
/// tau_ij = physical distance / wave speed, with the wave speed derived from the local inertia density
/// (slower where inertia is dense, per the LOS thesis) and CLAMPED to the electromechanical-wave
/// literature band (~500-1500 km/s).
///
/// PROVENANCE: this overlay is EXPERIMENTAL and UNVALIDATED AT SCALE. The clamp is the honest statement -
/// we assert only that the wave speed lies in the published band; the absolute GB-scale value is not
/// validated (it needs PMU/DFR field data). It is off by default (<see cref="DistributedConfig.EnablePropagation"/>).
/// </summary>
public static class PropagationDelay
{
    // GB bounding-box physical spans (km) for the normalized Bus.X/Y (0..1) map coordinates
    // (~10.1 deg lon at ~54 degN, ~9.2 deg lat).
    private const double SpanXKm = 660.0;
    private const double SpanYKm = 1020.0;

    // First-order wave-speed calibration (documented, clamped): a typical GB inertia density maps to
    // ~1000 km/s; denser -> slower (v ~ sqrt(rho_ref/rho)).
    private const double RefInertiaDensityMwSPerKm = 200.0;
    private const double RefWaveSpeedKmPerS = 1000.0;

    // Floor (MW.s per km) on the inertia density before the wave-speed proxy divides by it (October 2026 review, R159:
    // named, value unchanged). It only keeps sqrt(rho_ref / rho) finite for a fleet with no inertia; the result is
    // clamped to [MinWaveSpeedKmPerS, MaxWaveSpeedKmPerS] straight after.
    private const double MinInertiaDensityMwSPerKm = 1e-6;

    /// <summary>
    /// Compute the domain-pair delay matrix tau (seconds, in <paramref name="masses"/> order), the wave
    /// speed used (km/s), and the largest delay. Returns all-zero when propagation is disabled, there is
    /// <=1 domain, or the buses carry no map coordinates.
    /// </summary>
    public static (double[,] tauSeconds, double waveSpeedKmPerS, double maxDelaySeconds) Compute(
        GridModel model, IReadOnlyList<SpeedDomain> masses, DistributedConfig dist)
    {
        (double[,] tau, double v, double maxDelay, _) = ComputeWithLocation(model, masses, dist);
        return (tau, v, maxDelay);
    }

    /// <summary>
    /// As <see cref="Compute"/>, plus the indices (in <paramref name="masses"/> order) of the domains that have no located
    /// member bus. R240 (October 2026 review, stream B1; Ruling R1: Mark delegated EE calls 2026-10-04): Bus.X / Bus.Y are
    /// optional layout coordinates that default to (0, 0), so "no coordinate" could not be told from the origin, and every
    /// bus without one pulled its domain's centroid towards (0, 0) (the south-west corner of the normalised GB box, out at
    /// sea). A bus at exactly (0, 0) is now treated as having no position: it is left out of its domain's centroid, a domain
    /// with no located member gets zero delay to and from every other domain (the no-propagation value) and is listed
    /// here, and the extent is taken over located domains only. A model whose generator buses all carry a non-origin
    /// position computes exactly what it did before.
    /// </summary>
    public static (double[,] tauSeconds, double waveSpeedKmPerS, double maxDelaySeconds, IReadOnlyList<int> unlocatedDomains)
        ComputeWithLocation(GridModel model, IReadOnlyList<SpeedDomain> masses, DistributedConfig dist)
    {
        int n = masses.Count;
        var tau = new double[n, n];
        if (n <= 1 || !dist.EnablePropagation) return (tau, 0.0, 0.0, Array.Empty<int>());

        // R240: only buses with a real position (anything but exactly (0, 0)).
        Dictionary<int, (double X, double Y)> pos = model.Buses
            .Where(b => !(b.X == 0.0 && b.Y == 0.0))
            .ToDictionary(b => b.Id, b => (b.X, b.Y));
        var cx = new double[n];
        var cy = new double[n];
        var located = new bool[n];
        for (int d = 0; d < n; d++)
        {
            var buses = masses[d].MemberGenIndices.Select(gi => model.Generators[gi].Bus).Distinct();
            double sx = 0, sy = 0; int c = 0;
            foreach (int b in buses)
                if (pos.TryGetValue(b, out (double X, double Y) p)) { sx += p.X; sy += p.Y; c++; }
            if (c > 0) { cx[d] = sx / c; cy[d] = sy / c; located[d] = true; }
        }
        var unlocated = new List<int>();
        for (int d = 0; d < n; d++) if (!located[d]) unlocated.Add(d);

        double maxExtent = 0.0;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (located[i] && located[j])
                    maxExtent = Math.Max(maxExtent, DistanceKm(cx[i], cy[i], cx[j], cy[j]));
        if (maxExtent <= 0.0) return (tau, 0.0, 0.0, unlocated);   // no usable geography => no propagation

        double totalE = masses.Sum(m => m.AggregateInertiaMwS);
        double density = totalE / maxExtent;   // MW.s per km
        double vProxy = RefWaveSpeedKmPerS * Math.Sqrt(RefInertiaDensityMwSPerKm / Math.Max(density, MinInertiaDensityMwSPerKm));
        double v = Math.Clamp(vProxy, dist.MinWaveSpeedKmPerS, dist.MaxWaveSpeedKmPerS);

        // Representative bus per domain (its highest-inertia member) for looking up externally-provided tau
        // - e.g. GDA's propagation_delays lake product streamed over the bridge.
        var rep = new int[n];
        for (int d = 0; d < n; d++)
        {
            int best = -1; double bestE = -1.0;
            foreach (int gi in masses[d].MemberGenIndices)
            {
                double e = model.Generators[gi].H * model.Generators[gi].MBase;
                if (e > bestE) { bestE = e; best = model.Generators[gi].Bus; }
            }
            rep[d] = best;
        }
        IReadOnlyDictionary<(int, int), double>? provided = dist.ProvidedTauSeconds;

        double maxDelay = 0.0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j)
                {
                    // A supplied tau needs no position; the first-order model does, so a pair with an unlocated domain
                    // gets zero delay (R240) instead of a distance measured from the origin.
                    tau[i, j] = provided is not null && TryProvided(provided, rep[i], rep[j], out double pv)
                        ? pv
                        : located[i] && located[j] ? DistanceKm(cx[i], cy[i], cx[j], cy[j]) / v : 0.0;
                    maxDelay = Math.Max(maxDelay, tau[i, j]);
                }
        return (tau, v, maxDelay, unlocated);
    }

    /// <summary>Look up a provided tau for an unordered bus pair.</summary>
    private static bool TryProvided(IReadOnlyDictionary<(int, int), double> m, int a, int b, out double v)
        => m.TryGetValue((a, b), out v) || m.TryGetValue((b, a), out v);

    private static double DistanceKm(double x1, double y1, double x2, double y2)
    {
        double dx = (x1 - x2) * SpanXKm;
        double dy = (y1 - y2) * SpanYKm;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
