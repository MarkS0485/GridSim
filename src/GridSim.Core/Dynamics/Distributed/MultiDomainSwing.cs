using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Frequency;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// The domain-reduced multi-area centre-of-inertia swing kernel (M1 MVP). Each synchronous speed
/// domain is a coherent mass (E_d) with its own frequency and rotor angle, coupled to the others by
/// antisymmetric tie synchronising power. The system-level forcing (event, governor droop, load
/// damping, TSGB fast response, recorded system response services) is computed from the
/// inertia-weighted COI frequency EXACTLY as the lumped <see cref="FrequencyDynamics"/> does, then
/// distributed across domains so the per-domain updates re-aggregate to the identical COI trajectory.
///
/// Two provable properties follow from the construction:
///  - the tie power is antisymmetric (sum over domains = 0), so it moves energy between domains without
///    changing the COI - the COI frequency equals the lumped frequency to rounding (T1);
///  - with a single domain there is no tie and the forcing is the whole imbalance on the one mass -
///    identical to the lumped model (T0). (The single-domain case is delegated to FrequencyDynamics
///    by <see cref="DistributedInertiaModel"/> so that reduction is exact, not merely to rounding.)
///
/// Semi-implicit (symplectic) Euler on the same 20 ms step as the lumped model - energy-bounded, so it
/// stays stable under the stiff physically-derived (Kron-reduced) coupling where plain forward Euler
/// would blow up. The degenerate single-domain path delegates to the lumped forward-Euler model, so the
/// reduction stays exact. Inter-domain synchronising coefficients come from <see cref="DomainCoupling"/>
/// (Kron reduction of the network); RK4 for absolute first-swing accuracy vs published curves is a future
/// refinement alongside the per-machine classical model.
/// </summary>
public static class MultiDomainSwing
{
    public sealed record Result(
        FrequencyResponse Coi,
        IReadOnlyList<DomainTrace> Domains,
        IReadOnlyList<double> DomainArrivalSeconds);

    public static Result Simulate(
        IReadOnlyList<SpeedDomain> allDomains,
        double[,] couplingK,
        double[,] tauSeconds,
        double baseDemandMw,
        IEnumerable<FrequencyEvent> events,
        DynamicsConfig cfg,
        DistributedConfig dist,
        IReadOnlyList<double[]>? eventShares = null)
    {
        SpeedDomain[] masses = allDomains.Where(d => d.IsSwingMass).ToArray();
        if (masses.Length == 0)
            throw new InvalidOperationException("The distributed swing needs at least one synchronous domain (E > 0).");
        if (couplingK.GetLength(0) != masses.Length || couplingK.GetLength(1) != masses.Length)
            throw new ArgumentException("couplingK must be a massesxmasses matrix.", nameof(couplingK));

        // R080 (October 2026, stream B1): grid-forming has no multi-domain analogue (this kernel knows nothing of the
        // virtual former). DistributedInertiaModel delegates every forming run to the lumped forming integrator before it
        // gets here; a direct caller is refused instead of having the former silently dropped.
        if (cfg.Forming != FormingMode.None)
            throw new ArgumentException(
                $"MultiDomainSwing does not model grid-forming formation (Forming = {cfg.Forming}); run it through "
                + "DistributedInertiaModel, which delegates a forming run to the lumped forming integrator.", nameof(cfg));

        int n = masses.Length;
        // The one swing kernel (R080): governor, TSGB device, recorded services and the equilibrium start, keyed off the
        // COI deviation exactly as the lumped model keys them, so the per-domain swing re-aggregates to the lumped COI.
        var resp = new SwingResponders(cfg, baseDemandMw);
        double f0 = resp.NominalHz;
        double dt = cfg.StepSeconds;
        int steps = SwingKernel.BatchStepCount(cfg);
        // The events with their per-domain shares, in the time order every engine sums them in (a stable sort, so equal
        // times keep the given order, exactly as SwingKernel.SortedEvents orders them).
        FrequencyEvent[] given = events.ToArray();
        if (eventShares is not null)
        {
            if (eventShares.Count != given.Length)
                throw new ArgumentException($"eventShares has {eventShares.Count} entries for {given.Length} events.", nameof(eventShares));
            for (int x = 0; x < eventShares.Count; x++)
                if (eventShares[x] is null || eventShares[x].Length != masses.Length)
                    throw new ArgumentException($"eventShares[{x}] must hold one share per swing-mass domain ({masses.Length}).", nameof(eventShares));
        }
        int[] evOrder = Enumerable.Range(0, given.Length).OrderBy(x => given[x].AtSeconds).ToArray();
        FrequencyEvent[] ev = evOrder.Select(x => given[x]).ToArray();

        // Per-mass inertia (MW.s). Fold the TSGB device rotational inertia into the fleet proportionally
        // so the COI total equals the lumped E = system inertia + device inertia (keeps the COI exact).
        double[] eMass = masses.Select(m => m.AggregateInertiaMwS).ToArray();
        double eSync = eMass.Sum();
        double eTot = Math.Max(SwingKernel.MinInertiaMwS, eSync + cfg.DeviceRotationalInertiaMwS);
        if (eSync > 0 && cfg.DeviceRotationalInertiaMwS > 0)
        {
            double scale = eTot / eSync;
            for (int i = 0; i < n; i++) eMass[i] *= scale;
        }
        double eMassTot = eMass.Sum();
        // Bug hunt 2026-10-29, finding 20 (decided, documented): with no device inertia the masses are not scaled, so
        // when the whole fleet carries under SwingKernel.MinInertiaMwS (1 MW.s) this kernel integrates on eSync while
        // the lumped twin integrates on the 1 MW.s floor, and "COI equals lumped to rounding" does not hold there. A
        // multi-domain fleet with less than 1 MW.s of rotating mass in total is not a physical case (the floor exists
        // only to keep the lumped swing finite), so the invariant is stated for E >= 1 MW.s and nothing is changed.
        double[] share = eMass.Select(e => e / eMassTot).ToArray();   // response allocation ~ inertia (sum = 1)

        // Where each event lands (R060, October 2026 review, stream B1). DistributedInertiaModel resolves every event from
        // the model (the bus it happened at, or the domain hosting the largest single unit when the study gives none) and
        // passes the per-domain shares in. A kernel caller with no model passes none: every event then lands wholly on
        // LegacyLossDomain (the domain with the largest summed rated MVA, the pre-R060 rule, kept for such callers only).
        var evShares = new double[ev.Length][];
        int legacyLossDomain = LegacyLossDomain(masses);
        for (int x = 0; x < ev.Length; x++)
        {
            if (eventShares is not null) evShares[x] = eventShares[evOrder[x]];
            else { evShares[x] = new double[n]; evShares[x][legacyLossDomain] = 1.0; }
        }
        var pEventDomain = new double[n];

        // Propagation retardation: tie i<->j reads j's angle delayed by tau_ij, via a per-step angle history.
        // tau = 0 => delay 0 => current angle => identical to the no-propagation model. Guard the consumption:
        // a negative / NaN / infinite tau collapses to zero delay, and any finite tau is capped at the run length
        // so a bad value can't index past the angle history (nor silently freeze the domain worse than "all
        // t=0"). The protocol boundary already rejects implausible tau (BridgeProtocol.MaxPlausibleTauSeconds).
        var delaySteps = new int[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double tau = tauSeconds[i, j];
                int ds = double.IsFinite(tau) && tau > 0.0 ? (int)Math.Round(tau / dt) : 0;
                delaySteps[i, j] = Math.Clamp(ds, 0, steps);
            }
        var deltaHist = new List<double[]>(steps + 1);
        double[] arrivalT = Enumerable.Repeat(-1.0, n).ToArray();
        const double arrivalThreshold = 1e-3;   // Hz departure from nominal = "the wave has arrived"

        var baseTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double fStart = resp.StartFrequencyHz;   // the recorded pre-event frequency when set, else f0
        double[] fd = Enumerable.Repeat(fStart, n).ToArray();
        double[] delta = new double[n];
        var pts = new List<FrequencyPoint>[n];
        for (int i = 0; i < n; i++) pts[i] = new List<FrequencyPoint>(steps + 1);
        var coiPts = new List<FrequencyPoint>(steps + 1);

        // Recorded system response services (DC/DM/DR) and the equilibrium start live in the kernel: keyed off the COI
        // deviation exactly like the governor and device terms, so the sum over domains re-aggregates to the lumped
        // model's trajectory (T0/T1 preserved); the standing term rides the response (share-allocated, sum = standing).
        // Per-domain LOCAL service response is a future refinement gated on regional (PMU) trace data.
        double[] peakRocof = new double[n];
        double[] nadir = Enumerable.Repeat(fStart, n).ToArray();
        double[] nadirT = new double[n];
        double coiPeak = 0.0, coiNadir = fStart, coiNadirT = 0.0;

        for (int k = 0; k <= steps; k++)
        {
            double t = k * dt;
            deltaHist.Add((double[])delta.Clone());   // deltaHist[k] = angles at the start of step k

            double fCoi = 0.0;
            for (int i = 0; i < n; i++) fCoi += eMass[i] * fd[i];
            fCoi /= eMassTot;
            double dfCoi = fCoi - f0;

            // Each domain's share of every event that has happened by t, added left to right from +0.0 in time order. A
            // whole event on one domain is 1.0 * deltaP = deltaP exactly, so a one-domain placement gives that domain the
            // same sum the lumped model forms (0 + dP1 + dP2 ...) and every other domain exactly +0.0, the pre-R060 values.
            Array.Clear(pEventDomain);
            for (int x = 0; x < ev.Length; x++)
            {
                if (!SwingKernel.EventReached(ev[x].AtSeconds, t, dt)) continue;   // GR-540: the shared tolerant rule
                double[] sh = evShares[x];
                for (int i = 0; i < n; i++)
                    if (sh[i] != 0.0) pEventDomain[i] += sh[i] * ev[x].DeltaPMw;
            }

            // R080: the one swing kernel advances the governor (R159 / R242: SwingKernel.GovernorTarget through the
            // guarded lag), the TSGB device and the recorded services at the COI deviation, and adds them in the lumped
            // model's order: standing + governor + device + services - D * dfCoi. Same operands, same order as the
            // inline copy that used to live here, so the bits are unchanged and the COI cannot drift from the lumped.
            resp.Advance(dfCoi, dt);
            double pResponse = resp.CoiResponseMw(dfCoi);   // aggregate response terms, keyed off the COI

            double[] rocof = new double[n];
            double sumErocof = 0.0;
            for (int i = 0; i < n; i++)
            {
                double tie = 0.0;
                for (int j = 0; j < n; j++)
                    if (j != i)
                    {
                        double dj = deltaHist[Math.Max(0, k - delaySteps[i, j])][j];   // j's retarded angle
                        tie += couplingK[i, j] * (delta[i] - dj);
                    }
                double fEvent = pEventDomain[i];
                // GR-101: with no propagation delay the ties cancel in sum_i fi (= pEvent + pResponse, shares add to 1). With
                // a delay they do not: domain i sees delta_j(t - tau) while j sees delta_i(t - tau), so the ties carry a net
                // power while the angles move and the COI drifts from the lumped trajectory (technical ch. 36).
                double fi = fEvent + share[i] * pResponse - tie;
                rocof[i] = f0 / (2.0 * eMass[i]) * fi;
                // Fail loud on divergence, exactly as the lumped twin does (FrequencyDynamics.Simulate). The
                // symplectic step on the tie term is only stable while omega * dt < 2; a stiff Kron-reduced K between
                // two small domains blows it up to Infinity then NaN. Without this check the nadir and peak RoCoF
                // froze at the last finite values (NaN compares false) and a NaN trace came back as a normal result.
                if (!double.IsFinite(fd[i]) || !double.IsFinite(rocof[i]))
                    throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"Multi-domain frequency swing diverged at t = {t:R} s in domain {masses[i].Id} (f = {fd[i]:R} Hz, " +
                        $"RoCoF = {rocof[i]:R} Hz/s); the tie coupling is too stiff for the {dt:R} s step. No result."));
                sumErocof += eMass[i] * rocof[i];

                pts[i].Add(new FrequencyPoint(baseTime.AddSeconds(t), fd[i], rocof[i]));
                if (fd[i] < nadir[i]) { nadir[i] = fd[i]; nadirT[i] = t; }
                if (Math.Abs(rocof[i]) > Math.Abs(peakRocof[i])) peakRocof[i] = rocof[i];
            }

            double coiRocof = sumErocof / eMassTot;
            coiPts.Add(new FrequencyPoint(baseTime.AddSeconds(t), fCoi, coiRocof));
            if (fCoi < coiNadir) { coiNadir = fCoi; coiNadirT = t; }
            if (Math.Abs(coiRocof) > Math.Abs(coiPeak)) coiPeak = coiRocof;

            // Semi-implicit (symplectic) Euler: advance frequency first, then angle with the UPDATED
            // frequency. Symplectic integration is energy-bounded, so it stays stable under the stiff
            // physical (Kron-derived) coupling where plain forward Euler would grow without bound.
            for (int i = 0; i < n; i++)
            {
                fd[i] += rocof[i] * dt;
                delta[i] += 2.0 * Math.PI * (fd[i] - f0) * dt;
                // GR-556: fd[i] now holds the frequency at the END of this step, time (k + 1) * dt, so the crossing is
                // stamped there; stamping t (the step start) put every arrival one step early. (k + 1) * dt is formed
                // the same way t is, so the stamp sits exactly on the time grid.
                if (arrivalT[i] < 0.0 && Math.Abs(fd[i] - fStart) > arrivalThreshold) arrivalT[i] = (k + 1) * dt;
            }
        }

        // Settle = the post-final-update frequency, matching FrequencyDynamics' convention.
        double settleCoi = 0.0;
        for (int i = 0; i < n; i++) settleCoi += eMass[i] * fd[i];
        settleCoi /= eMassTot;

        var domainTraces = new List<DomainTrace>(n);
        for (int i = 0; i < n; i++)
            domainTraces.Add(new DomainTrace(masses[i].Id, peakRocof[i], nadir[i], nadirT[i], fd[i], pts[i]));

        var coi = new FrequencyResponse(coiPeak, coiNadir, coiNadirT, settleCoi, coiPts);
        return new Result(coi, domainTraces, arrivalT);
    }

    /// <summary>The pre-R060 placement, kept only for a kernel caller that passes no event shares (it has no model to
    /// resolve a location from): the swing-mass index with the largest summed rated MVA. The R060 finding: this was
    /// documented as "the domain hosting the single largest unit" but is not that; DistributedInertiaModel now resolves
    /// the documented rule (and any event location) from the model and passes the shares in.</summary>
    private static int LegacyLossDomain(SpeedDomain[] masses)
    {
        int best = 0;
        for (int i = 1; i < masses.Length; i++)
            if (masses[i].RatedMva > masses[best].RatedMva) best = i;
        return best;
    }
}
