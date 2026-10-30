using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Physics.Experimental;

/// <summary>
/// EXPERIMENTAL, opt-in physical-consistency law: network RECIPROCITY. A network built only from
/// passive RLC elements (lines, shunts, in-phase transformers) has a SYMMETRIC bus admittance matrix,
/// Y = Y^T - a consequence of Lorentz reciprocity. Phase-shifting transformers are the deliberate
/// exception: they are non-reciprocal by design (Yft = -ys/conj(tap), Ytf = -ys/tap differ once the
/// phase shift is non-zero), so the check EXCLUDES phase-shifted branches and asserts symmetry over the
/// rest. A residual there is not a physics violation of the state - it is a MODEL/BUILD error (a
/// mis-stamped branch, a duplicated one-directional entry), which is exactly what makes it a useful
/// build-time audit.
///
/// PROVENANCE: this law is EXPERIMENTAL and OFF BY DEFAULT. It is not one of the ten headline laws and
/// is not part of <see cref="PhysicsLaws.Audit"/>; a caller opts in (the CLI's <c>physics-check
/// --experimental</c>). It COLLAPSES to a trivial pass in its degenerate limit: a phase-shifter-free
/// passive network is exactly reciprocal (residual at machine epsilon), and an empty/single-bus network
/// is vacuously reciprocal. It never gates a state (a reciprocity break is a data defect, not an
/// impossible state), so it is reported as Constitutive/Violated, never in <see cref="PhysicsCheckRegistry.HardGateLaws"/>.
/// </summary>
public static class ReciprocityCheck
{
    /// <summary>The experimental reciprocity verdict for <paramref name="model"/>: max |Y_ij - Y_ji|
    /// (pu) over branch pairs with NO phase shift. Holds within <paramref name="tol"/>; a residual is a
    /// model/build defect. Reports Constitutive (a note) when the network carries phase-shifters, since
    /// reciprocity is then only partial by design.</summary>
    public static LawResult Evaluate(GridModel model, double tol = 1e-9)
    {
        // Accumulate off-diagonal admittance per ordered (from,to) pair, and record which unordered
        // pairs a phase-shifting branch touched (those are legitimately non-reciprocal).
        var yij = new Dictionary<(int, int), Complex>();
        var phaseShifted = new HashSet<(int, int)>();
        int shifters = 0;

        foreach (Branch br in model.Branches)
        {
            // Off-diagonal stamp only (this audit tests Y_ft vs Y_tf reciprocity; the diagonals and line
            // charging are irrelevant here). The stamp arithmetic is pinned by AdmittanceStampPinTests, so
            // routing through the shared stamp keeps this check independent of a defect in that arithmetic.
            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);

            Accumulate(yij, br.From, br.To, s.Yft);
            Accumulate(yij, br.To, br.From, s.Ytf);

            if (br.PhaseShiftDeg != 0.0)
            {
                phaseShifted.Add(Key(br.From, br.To));
                shifters++;
            }
        }

        double maxAsym = 0.0;
        var seen = new HashSet<(int, int)>();
        foreach (((int i, int j), Complex v) in yij)
        {
            (int, int) key = Key(i, j);
            if (i == j || phaseShifted.Contains(key) || !seen.Add(key)) continue;
            Complex vji = yij.TryGetValue((j, i), out Complex back) ? back : Complex.Zero;
            double asym = (v - vji).Magnitude;
            // A NaN asymmetry (a NaN stamp) must win the max, not be skipped: "x > max" is false for NaN, so a NaN
            // network used to read residual 0 and "Holds". Infinity propagates the same way.
            if (!(asym <= maxAsym)) maxAsym = double.IsNaN(asym) ? double.PositiveInfinity : asym;
        }

        bool holds = maxAsym <= tol;
        LawStatus status = shifters > 0 && holds ? LawStatus.Constitutive
                          : holds ? LawStatus.Holds
                          : LawStatus.Violated;
        string detail = shifters > 0
            ? $"reciprocal part symmetric to {maxAsym:E2} pu; {shifters} phase-shifter(s) non-reciprocal by design (excluded)"
            : $"admittance symmetric (Y = Y^T) to {maxAsym:E2} pu";

        return new LawResult(PhysicsCheckRegistry.ReciprocityLaw, PhysicsCheckRegistry.ExperimentalLaws[PhysicsCheckRegistry.ReciprocityLaw],
            "passive network is reciprocal: Y = Y^T (phase-shifters excepted)",
            status, maxAsym, "pu", tol, detail);
    }

    private static void Accumulate(Dictionary<(int, int), Complex> map, int i, int j, Complex y)
        => map[(i, j)] = map.TryGetValue((i, j), out Complex cur) ? cur + y : y;

    private static (int, int) Key(int a, int b) => a <= b ? (a, b) : (b, a);
}
