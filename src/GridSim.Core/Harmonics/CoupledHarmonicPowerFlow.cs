using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Diagnostics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics;

/// <summary>
/// A voltage-dependent harmonic-source coupling model: how a converter presents to the harmonic network. It
/// has two parts - a LINEAR harmonic admittance <see cref="ShuntAdmittance"/> (the converter's Norton
/// admittance, folded into Y so the solve stays stable and damps the resonance it would otherwise excite) and
/// a current-source <see cref="Injection"/> that may depend nonlinearly on the terminal harmonic voltage.
/// This is the ONE assumed part of the coupled solve - a converter's true behaviour is (vendor-proprietary)
/// control-dependent - so it carries a <see cref="ProvenanceTag"/>, honestly
/// <see cref="ProvenanceTag.CuratedPlaceholder"/>.
/// </summary>
public interface IHarmonicCouplingModel
{
    /// <summary>The converter's linear harmonic admittance at <paramref name="order"/>, folded into Y at its
    /// bus. A positive conductance damps the resonance.</summary>
    Complex ShuntAdmittance(int order);

    /// <summary>The current-source injection at <paramref name="order"/> given the base (open-circuit)
    /// injection and the present terminal harmonic voltage. Linear models ignore the voltage.</summary>
    Complex Injection(int order, Complex terminalVoltage, Complex baseInjection);

    /// <summary>Provenance of the coupling model (assumed by default).</summary>
    ProvenanceTag Provenance { get; }
}

/// <summary>
/// A Norton coupling: the converter is a current source <c>Is(h)</c> in parallel with a harmonic admittance
/// <c>Yc(h)</c>. The conductance damps the network resonance. A simple, honestly-assumed placeholder for a
/// real, control-dependent characteristic.
/// </summary>
public sealed class NortonCouplingModel : IHarmonicCouplingModel
{
    private readonly double _g;
    private readonly double _bPerOrder;

    public NortonCouplingModel(double couplingConductance = 0.05, double couplingSusceptancePerOrder = 0.0)
    {
        _g = couplingConductance;
        _bPerOrder = couplingSusceptancePerOrder;
    }

    public ProvenanceTag Provenance => ProvenanceTag.CuratedPlaceholder;

    public Complex ShuntAdmittance(int order) => new Complex(_g, _bPerOrder * order);

    public Complex Injection(int order, Complex terminalVoltage, Complex baseInjection) => baseInjection;
}

/// <summary>The outcome of a coupled harmonic power flow.</summary>
public sealed record CoupledHarmonicResult(
    HarmonicPenetrationResult Result,
    int Iterations,
    bool Converged,
    ProvenanceTag CouplingProvenance);

/// <summary>
/// Coupled iterative harmonic power flow. The converter's LINEAR harmonic admittance is folded into Y(h) (so
/// the system is always stable and the converter damps, rather than only excites, the resonance), and any
/// NONLINEAR voltage dependence of the injection is resolved by fixed-point iteration on that stable system:
/// solve, update the injection from the coupling model, re-solve, until the voltage settles. The network
/// physics stays grounded; only the coupling model is assumed, and it is provenance-tagged.
///
/// <para><b>Experimental</b> (R062, October 2026): Core API and tests only; no CLI verb or production caller. Still
/// needs: a measured or vendor converter coupling model (the only model, NortonCouplingModel, is a
/// CuratedPlaceholder), a CLI surface, and validation against an independent coupled harmonic solver.</para>
/// </summary>
public static class CoupledHarmonicPowerFlow
{
    public static CoupledHarmonicResult Solve(
        GridModel model,
        IReadOnlyList<HarmonicSource> sources,
        IHarmonicCouplingModel coupling,
        HarmonicModelOptions? options = null,
        IComplexLinearSolver? solver = null,
        double tol = 1e-10,
        int maxIter = 50)
    {
        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        HarmonicPenetration.RefuseTriplensIfBalanced(sources, opt);   // R029: same contract as penetration
        HarmonicPenetration.RefuseUnknownSourceBuses(model, sources, "Coupled harmonic power flow");
        solver ??= new RealExpandedComplexSolver();

        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        var busIds = new int[n];
        for (int i = 0; i < n; i++) { index[model.Buses[i].Id] = i; busIds[i] = model.Buses[i].Id; }

        // The bus indices that carry a source (where the coupling admittance/injection apply).
        var sourceBusIdx = new HashSet<int>();
        foreach (HarmonicSource src in sources)
            if (index.TryGetValue(src.Bus, out int bi)) sourceBusIdx.Add(bi);

        IReadOnlyList<int> orders = opt.Orders;
        var voltage = new Complex[orders.Count][];
        int worstIters = 0;
        bool allConverged = true;

        for (int oi = 0; oi < orders.Count; oi++)
        {
            int h = orders[oi];
            var v = new Complex[n];
            if (h != 1)
            {
                var baseInj = new Complex[n];
                bool any = false;
                foreach (HarmonicSource src in sources)
                    if (index.TryGetValue(src.Bus, out int bi))
                    {
                        Complex ih = src.InjectionAt(h);
                        if (ih != Complex.Zero) { baseInj[bi] += ih; any = true; }
                    }

                if (any)
                {
                    // Y(h) only for an order that carries current (in balanced mode a triplen never does; R029).
                    ComplexCsr y = FrequencyDependentYBus.Build(model, h, opt);

                    // Fold the converter Norton admittance into Y at each source bus (damps + stabilises).
                    Complex ysh = coupling.ShuntAdmittance(h);
                    ComplexCsr yc = ysh == Complex.Zero ? y : WithDiagonalShunts(y, sourceBusIdx, ysh);

                    // yc is fixed for this order; the fixed-point loop solves it against a fresh injection
                    // vector each iteration - so factor it ONCE and back-substitute per iteration instead of
                    // re-factoring up to maxIter+1 times.
                    // A singular Y(h) used to become a zero response and then report Converged=true because
                    // every update was zero (R001). Fail loud instead.
                    IComplexLinearFactorization fac = solver.Factor(yc)
                        ?? throw new SingularHarmonicNetworkException("Coupled harmonic power flow", h);

                    var inj = new Complex[n];
                    foreach (int bi in sourceBusIdx) inj[bi] = coupling.Injection(h, Complex.Zero, baseInj[bi]);
                    v = fac.Solve(inj) ?? throw new SingularHarmonicNetworkException("Coupled harmonic power flow", h);

                    int iter = 0;
                    for (; iter < maxIter; iter++)
                    {
                        var newInj = new Complex[n];
                        foreach (int bi in sourceBusIdx) newInj[bi] = coupling.Injection(h, v[bi], baseInj[bi]);
                        Complex[] vNew = fac.Solve(newInj)
                            ?? throw new SingularHarmonicNetworkException("Coupled harmonic power flow", h);
                        double maxDelta = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double d = (vNew[i] - v[i]).Magnitude;
                            // GR-576: a NaN difference never compares greater, so a NaN solution used to read as a
                            // change of 0 and "converge" on the first pass. A non-finite voltage is refused.
                            if (!double.IsFinite(d))
                                throw new InvalidOperationException(
                                    $"Coupled harmonic power flow: order {h} produced a non-finite voltage at bus {model.Buses[i].Id} (iteration {iter + 1}).");
                            if (d > maxDelta) maxDelta = d;
                        }
                        v = vNew;
                        if (maxDelta < tol) break;
                    }
                    if (iter > worstIters) worstIters = iter;
                    if (iter >= maxIter) allConverged = false;
                }
            }
            voltage[oi] = v;
        }

        // The same disclosure as HarmonicPenetration.Solve: which source components fall outside the analysed orders.
        var result = new HarmonicPenetrationResult(orders, busIds, voltage)
        {
            ComponentsOutsideOrders = HarmonicPenetration.ComponentsOutsideOrders(sources, opt),
        };
        return new CoupledHarmonicResult(result, worstIters, allConverged, coupling.Provenance);
    }

    /// <summary>A copy of <paramref name="y"/> with an extra shunt admittance added to the given buses' diagonals.</summary>
    private static ComplexCsr WithDiagonalShunts(ComplexCsr y, HashSet<int> busIdx, Complex shunt)
    {
        var b = new ComplexCsr.Builder(y.N);
        for (int i = 0; i < y.N; i++)
            for (int k = y.RowPtr[i]; k < y.RowPtr[i + 1]; k++)
                b.Add(i, y.ColIdx[k], y.G[k], y.B[k]);
        foreach (int i in busIdx) b.Add(i, i, shunt.Real, shunt.Imaginary);
        return b.Build();
    }
}
