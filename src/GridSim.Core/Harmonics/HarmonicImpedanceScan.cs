using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics;

/// <summary>
/// A frequency scan of the driving-point harmonic impedance seen at one bus, plus the resonance it reveals.
/// <see cref="Z"/>[oi] is the driving-point impedance Z_kk at <see cref="BusId"/> at <see cref="Orders"/>[oi];
/// a large |Z| is a parallel resonance (a small injected harmonic current there produces a large voltage).
/// <see cref="ResonanceOrder"/> / <see cref="PeakMagnitude"/> name the worst peak, and
/// <see cref="Participation"/> gives, at that order, the transfer impedance to every bus - the buses that
/// "participate" in the resonance (a proxy for the modal participation factor without a complex eigensolve).
/// </summary>
public sealed record ImpedanceScanResult(
    IReadOnlyList<int> Orders,
    int BusId,
    Complex[] Z,
    int ResonanceOrder,
    double PeakMagnitude,
    IReadOnlyList<int> BusIds,
    Complex[] Participation);

/// <summary>
/// The harmonic impedance / frequency scan and resonance identification. The driving-point impedance at a bus
/// is the k-th entry of the solution of <c>Y(h) x = e_k</c> (a unit current injected at bus k), i.e. the
/// k-th column of <c>Y(h)^-1</c>; scanning it across orders reveals where the network resonates. This is the
/// tool that turns "harmonic-damping loss" from a claim into a measurement: as synchronous machines retire,
/// the resonance peaks sharpen and move into the ordered-harmonic range.
/// </summary>
public static class HarmonicImpedanceScan
{
    /// <summary>Scan the driving-point impedance at <paramref name="busId"/> across the option's orders.</summary>
    public static ImpedanceScanResult DrivingPoint(
        GridModel model,
        int busId,
        HarmonicModelOptions? options = null,
        IComplexLinearSolver? solver = null)
    {
        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        // N348 / N149 (end pass, 2026-10-11): refuse a non-physical active-front-end impedance once, up front, so the
        // refusal is a plain ArgumentException and not one wrapped in an AggregateException by the parallel loop below
        // (as HarmonicPenetration.Solve). Validation only: no number changes.
        if (opt.ActiveFrontEndReactancePu is double xAfe)
            FrequencyDependentYBus.ValidateActiveFrontEndImpedance(opt.ActiveFrontEndResistancePu, xAfe);
        bool ownSolver = solver is null; // only the default solver may be parallelised (per-worker instance)

        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        var busIds = new int[n];
        for (int i = 0; i < n; i++) { index[model.Buses[i].Id] = i; busIds[i] = model.Buses[i].Id; }
        if (!index.TryGetValue(busId, out int k))
            throw new ArgumentException($"bus {busId} is not in the model.");

        // Balanced-harmonic mode (R029): a triplen's driving-point impedance is a zero-sequence quantity this
        // positive-sequence model does not have, so the scan covers only the sequence-representable orders.
        IReadOnlyList<int> orders = opt.BalancedHarmonicSequences
            ? opt.Orders.Where(h => !HarmonicSequence.IsTriplen(h)).ToArray()
            : opt.Orders;
        // Nothing left to scan (every order a triplen in balanced mode): refuse, instead of returning peak 0 at order
        // 0, which read as "no resonance".
        if (orders.Count == 0)
            throw new ArgumentException(
                "Harmonic impedance scan: no order left to scan (in balanced mode every triplen order is removed, and these orders " +
                $"were all triplens: {string.Join(", ", opt.Orders)}).", nameof(options));
        var z = new Complex[orders.Count];
        var columns = new Complex[orders.Count][]; // full Y(h)^-1 column per order (null if singular)

        // Each order solves an independent Y(h) x = e_k; compute them (in parallel when we own the solver),
        // then reduce for the resonance peak sequentially so the tie-break is identical to the serial scan.
        void ScanOrder(int oi, IComplexLinearSolver s)
        {
            ComplexCsr y = FrequencyDependentYBus.Build(model, orders[oi], opt);
            var e = new Complex[n];
            e[k] = Complex.One;                        // unit current injection at bus k
            Complex[]? x = s.Solve(y, e);              // x = column k of Y(h)^-1 (null = singular)
            z[oi] = x is null ? Complex.Zero : x[k];   // driving-point impedance Z_kk; singular is thrown below
            columns[oi] = x!;
        }

        if (ownSolver && orders.Count > 1)
        {
            using var pool = new ThreadLocal<IComplexLinearSolver>(() => new RealExpandedComplexSolver());
            // Unwrapped and deterministic: a failing order surfaces as the serial loop would raise it (lowest order index).
            DeterministicParallel.For(orders.Count, oi => ScanOrder(oi, pool.Value!));
        }
        else
        {
            IComplexLinearSolver s = solver ?? new RealExpandedComplexSolver();
            for (int oi = 0; oi < orders.Count; oi++) ScanOrder(oi, s);
        }

        // Fail loud on the LOWEST singular order (R001): a singular Y(h) used to read as Z = 0 - "no
        // resonance here" - which is the opposite of the truth (an ungrounded network has no finite impedance).
        int lowest = int.MaxValue;
        for (int oi = 0; oi < orders.Count; oi++)
            if (columns[oi] is null && orders[oi] < lowest) lowest = orders[oi];
        if (lowest != int.MaxValue) throw new SingularHarmonicNetworkException("Harmonic impedance scan", lowest);

        Complex[]? bestColumn = null;
        double peak = -1;
        int resonanceOrder = orders.Count > 0 ? orders[0] : 0;
        for (int oi = 0; oi < orders.Count; oi++)
        {
            if (columns[oi] is null) continue;
            double mag = z[oi].Magnitude;
            if (mag > peak) { peak = mag; resonanceOrder = orders[oi]; bestColumn = columns[oi]; }
        }

        Complex[] participation = bestColumn ?? new Complex[n];
        return new ImpedanceScanResult(orders, busId, z, resonanceOrder, Math.Max(peak, 0.0), busIds, participation);
    }
}
