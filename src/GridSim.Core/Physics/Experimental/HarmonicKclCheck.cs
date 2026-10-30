using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Physics.Experimental;

/// <summary>
/// EXPERIMENTAL, opt-in physical-consistency law: harmonic KIRCHHOFF CURRENT balance. A harmonic penetration
/// state must satisfy the network equations at every order - the nodal current computed from the solved
/// harmonic voltages, <c>Y(h) V(h)</c>, must equal the injected harmonic current <c>I(h)</c> at every bus.
/// A residual is not an impossible state, it is a SOLVE/MODEL defect (a mis-built Y(h), a lost injection),
/// which is what makes it a useful audit of the harmonic engine.
///
/// PROVENANCE: EXPERIMENTAL and OFF BY DEFAULT - like <see cref="ReciprocityCheck"/> it is not one of the
/// headline laws, not part of <see cref="PhysicsLaws.Audit"/>, and never in
/// <see cref="PhysicsCheckRegistry.HardGateLaws"/>. It COLLAPSES to a trivial pass in the degenerate limit
/// (a correctly-solved system balances to machine epsilon; an empty source set is vacuously balanced).
/// </summary>
public static class HarmonicKclCheck
{
    /// <summary>The harmonic-KCL verdict for a penetration result: the max |Y(h)V(h) - I(h)| (pu) over every
    /// analysed order and bus. Holds within <paramref name="tol"/>; a residual is a solve/model defect.</summary>
    public static LawResult Evaluate(
        GridModel model,
        IReadOnlyList<HarmonicSource> sources,
        HarmonicPenetrationResult result,
        HarmonicModelOptions? options = null,
        double tol = 1e-8)
    {
        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        double maxRes = 0.0;
        int worstOrder = 0;
        for (int oi = 0; oi < result.Orders.Count; oi++)
        {
            int h = result.Orders[oi];
            if (h == 1) continue;

            var inj = new Complex[n];
            bool any = false;
            foreach (HarmonicSource src in sources)
                if (index.TryGetValue(src.Bus, out int bi))
                {
                    Complex ih = src.InjectionAt(h);
                    if (ih != Complex.Zero) { inj[bi] += ih; any = true; }
                }
            if (!any) continue;

            ComplexCsr y = FrequencyDependentYBus.Build(model, h, opt);
            Complex[] v = result.Voltage[oi];
            for (int i = 0; i < n; i++)
            {
                Complex yv = Complex.Zero;
                for (int k = y.RowPtr[i]; k < y.RowPtr[i + 1]; k++)
                    yv += new Complex(y.G[k], y.B[k]) * v[y.ColIdx[k]];
                double res = (yv - inj[i]).Magnitude;
                // NaN wins (as +Infinity): a diverged harmonic solve must not read "balanced to 0".
                if (!(res <= maxRes)) { maxRes = double.IsNaN(res) ? double.PositiveInfinity : res; worstOrder = h; }
            }
        }

        bool holds = maxRes <= tol;
        return new LawResult(PhysicsCheckRegistry.HarmonicKclLaw, PhysicsCheckRegistry.ExperimentalLaws[PhysicsCheckRegistry.HarmonicKclLaw],
            "harmonic penetration balances: Y(h) V(h) = I(h) at every order",
            holds ? LawStatus.Holds : LawStatus.Violated, maxRes, "pu", tol,
            holds ? $"harmonic KCL balanced to {maxRes:E2} pu"
                  : $"harmonic KCL residual {maxRes:E2} pu at order {worstOrder}");
    }
}
