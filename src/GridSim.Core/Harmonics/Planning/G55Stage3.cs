using System.Numerics;
using GridSim.Core.Model;

namespace GridSim.Core.Harmonics.Planning;

/// <summary>The inputs of a G5/5 Stage 3 assessment (clause 10) on a harmonic network model.</summary>
/// <param name="Model">The network (the harmonic model; PCC and remote nodes are its buses).</param>
/// <param name="PccBus">The point of connection bus id.</param>
/// <param name="NewUserSources">The new user's emissions (<see cref="HarmonicSource"/>, % of the fundamental current of the source).</param>
/// <param name="Options">The harmonic model options (orders, load shunts, generator sources, balanced sequences).</param>
/// <param name="PccBackgroundPct">The measured background at the PCC, per order, % of the fundamental (clause 6.3.5).</param>
/// <param name="RemoteBackgroundPct">Optional measured or derived background at remote buses: bus id to (order to %).</param>
/// <param name="AssumedRemoteBackgroundFraction">When set, a remote node with no background is taken at this fraction of its own planning level (a declared assumption, named in the result).</param>
/// <param name="DiversityDeclared">True when the operational and locational diversity of the sources is declared, so Table 16 exponents apply; false (default) is linear addition (clause 6.3.3).</param>
/// <param name="MvaCapacity">The MVA import or export capacity of the connection (the larger), needed above 132 kV (equation 56).</param>
/// <param name="TransferThreshold">Remote nodes with a transfer coefficient at or below this are left out (clause 10.4.1, at the operator's discretion); default 0 considers every node.</param>
/// <param name="UserShuntImpedancePu">Optional impedance of the new user's own installation seen from the PCC, per order, pu on the model base; used for the post-connection impedance of equation (69).</param>
/// <param name="MissingBackgroundIsZero">Declare a missing PCC background order to be zero instead of INCOMPLETE.</param>
public sealed record G55Stage3Request(
    GridModel Model,
    int PccBus,
    IReadOnlyList<HarmonicSource> NewUserSources,
    HarmonicModelOptions Options,
    IReadOnlyDictionary<int, double> PccBackgroundPct,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>>? RemoteBackgroundPct = null,
    double? AssumedRemoteBackgroundFraction = null,
    bool DiversityDeclared = false,
    double? MvaCapacity = null,
    double TransferThreshold = 0.0,
    IReadOnlyDictionary<int, Complex>? UserShuntImpedancePu = null,
    bool MissingBackgroundIsZero = false);

/// <summary>One order of the Stage 3 limit table (clause 10.6, Table 24) with the compliance figures.</summary>
public sealed record G55Stage3Row(
    int Order,
    double PlanningLevelPct,
    double BackgroundPct,
    double HeadroomPccPct,
    double HeadroomTransferredMinPct,
    int LimitingBus,
    double Multiplier,
    double IncrementalLimitPct,
    double IncrementalPct,
    double TotalLimitPct,
    double TotalPct,
    double ResonantAllowancePct,
    double Alpha,
    bool IncrementalWithin,
    bool TotalWithin);

/// <summary>The Stage 3 result.</summary>
public sealed record G55Stage3Result(
    string Outcome,
    double ThdPlanningLevelPct,
    double ThdTotalPct,
    double ThdIncrementalPct,
    IReadOnlyList<G55Stage3Row> Rows,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> ComponentsOutsideOrders);

/// <summary>
/// RC4 R4-21: the Stage 3 assessment of ENA EREC G5 Issue 5 (2020) clause 10, on a harmonic network model. For each analysed order:
/// the transfer coefficient from the PCC to every other node, <c>T = |Z_PCC,n| / |Z_PCC|</c> (equation 50), from a unit current at
/// the PCC by the existing <see cref="HarmonicPenetration"/>; the headroom at the PCC and at each remote node,
/// <c>H = (V_PL^a - V_bg^a)^(1/a)</c> (51, 52), transferred to the PCC by dividing by T (53); the incremental limit
/// <c>V_lim,inc = M min(H_n,PCC, H_PCC)</c> (54) with M = 0.5 up to 132 kV (55) and the curve of equations (56) to (60) above it; the
/// total limit <c>V_lim,total = (V_bg^a + (M H_PCC)^a)^(1/a)</c> (62, 63); the resonant allowance (65); and compliance by (67) and (68),
/// with the total <c>V_total = (V_inc^a + (|Z_post|/|Z_pre| V_bg)^a)^(1/a)</c> (69). The new user's voltage at the PCC and elsewhere
/// is computed by the incumbent penetration solver; the fundamental voltage is taken as 1 pu (the distortion is a ratio to the
/// nominal fundamental), and the planning level at a node is that of its own nominal voltage (<see cref="Bus.BaseKv"/>).
/// </summary>
public static class G55Stage3
{
    /// <summary>Table 23 (p.84): the beta of equation (56) for the nominal voltage above 132 kV: 132 &lt; V &lt; 275: 1000; 275: 1500; 400: 2000.</summary>
    public static double Beta(double kv)
    {
        if (kv > 132.0 && kv < 275.0) return 1000.0;
        if (kv == 275.0) return 1500.0;
        if (kv == 400.0) return 2000.0;
        throw new ArgumentOutOfRangeException(nameof(kv), kv, "Table 23 gives beta for 132 < V < 275, 275 and 400 kV only.");
    }

    /// <summary>Equations (57) to (60), p.84: the apportionment multiplier M for kM = S_i / beta above 132 kV.</summary>
    public static double ApportionmentAbove132(double kM)
    {
        if (!(kM >= 0.0) || !double.IsFinite(kM)) throw new ArgumentOutOfRangeException(nameof(kM), kM, "kM must be finite and not negative.");
        if (kM <= 0.05) return 0.1;
        if (kM <= 0.25) return 2.0 * kM;
        if (kM <= 1.0) return 16.0 / 75.0 * kM + 67.0 / 150.0;
        return 0.66;
    }

    /// <summary>Clauses 10.4.3.1 and 10.4.3.2: M is 0.5 up to and including 132 kV; above it the curve of equations (57) to (60).</summary>
    public static double Multiplier(double pccKv, double? mvaCapacity)
    {
        if (pccKv <= 132.0) return 0.5;
        if (mvaCapacity is not double s || !(s > 0.0) || !double.IsFinite(s))
            throw new ArgumentException("Above 132 kV the apportionment multiplier needs the MVA import or export capacity of the connection.", nameof(mvaCapacity));
        return ApportionmentAbove132(s / Beta(pccKv));
    }

    /// <summary>Assess the connection. A missing background is INCOMPLETE (named), never a pass.</summary>
    public static G55Stage3Result Assess(G55Stage3Request req)
    {
        ArgumentNullException.ThrowIfNull(req);
        GridModel model = req.Model;
        int pccIndex = -1;
        for (int i = 0; i < model.BusCount; i++) if (model.Buses[i].Id == req.PccBus) { pccIndex = i; break; }
        if (pccIndex < 0) throw new ArgumentException($"PCC bus {req.PccBus} is not in the model.", nameof(req));
        double pccKv = model.Buses[pccIndex].BaseKv;
        if (!(pccKv > 0.0) || !double.IsFinite(pccKv)) throw new ArgumentException($"PCC bus {req.PccBus} has no usable nominal voltage; the planning level cannot be chosen.", nameof(req));
        if (req.NewUserSources.Count == 0) throw new ArgumentException("A Stage 3 assessment needs the new user's harmonic sources.", nameof(req));
        if (!(req.TransferThreshold >= 0.0) || !double.IsFinite(req.TransferThreshold)) throw new ArgumentOutOfRangeException(nameof(req), "TransferThreshold must be finite and not negative.");

        HarmonicModelOptions opt = req.Options;
        // The balanced positive-sequence model has no zero-sequence network: a triplen order cannot be represented (R029), so with
        // balanced harmonics the triplen orders are neither analysed nor injected, and the omission is named in the assumptions.
        bool balanced = opt.BalancedHarmonicSequences;
        var orders = opt.Orders.Where(h => h >= 2 && !(balanced && h % 3 == 0)).OrderBy(h => h).ToArray();
        if (orders.Length == 0) throw new ArgumentException("No analysable harmonic order remains (triplen orders are not represented with balanced harmonics).", nameof(req));
        double mult = Multiplier(pccKv, req.MvaCapacity);
        var omitted = new List<string>();
        IReadOnlyList<HarmonicSource> sourcesUsed = req.NewUserSources;
        if (balanced)
        {
            sourcesUsed = req.NewUserSources.Select(src =>
            {
                foreach (HarmonicComponent c in src.Spectrum) if (c.Order % 3 == 0 && c.MagnitudePercent != 0.0) omitted.Add($"bus {src.Bus} order {c.Order} at {c.MagnitudePercent:R} %");
                return src with { Spectrum = src.Spectrum.Where(c => c.Order % 3 != 0).ToArray() };
            }).ToArray();
        }
        HarmonicModelOptions solveOpt = opt with { Orders = opt.Orders.Where(h => !(balanced && h % 3 == 0)).ToArray() };

        HarmonicPenetrationResult inc = HarmonicPenetration.Solve(model, sourcesUsed, solveOpt);
        // Unit current (1 pu) at the PCC at every analysed order: its voltages are the transfer impedances (equation 50).
        var unit = new HarmonicSource(req.PccBus, "unit-injection", 1.0, orders.Select(h => new HarmonicComponent(h, 100.0)).ToArray());
        HarmonicPenetrationResult tr = HarmonicPenetration.Solve(model, [unit], solveOpt);

        var missing = new List<string>();
        var assumptions = new List<string>();
        bool usedAssumedRemote = false;
        var rows = new List<G55Stage3Row>(orders.Length);
        double sumTotSq = 0.0, sumIncSq = 0.0;
        bool allWithin = true;
        double thdPl = G55Limits.ThdPlanningLevelPct(pccKv);

        foreach (int h in orders)
        {
            double alpha = req.DiversityDeclared ? G55Limits.SummationExponent(h) : 1.0;
            double plPcc = G55Limits.PlanningLevelPct(pccKv, h);
            double bgPcc;
            if (req.PccBackgroundPct.TryGetValue(h, out double bg)) bgPcc = bg;
            else if (req.MissingBackgroundIsZero) bgPcc = 0.0;
            else { missing.Add("PCC background order " + h); continue; }
            double hPcc = G55Limits.Headroom(plPcc, bgPcc, alpha);

            Complex zPccSelf = tr.VoltageAt(h, pccIndex);
            double zSelf = zPccSelf.Magnitude;
            double minTransferred = double.PositiveInfinity;
            int limitingBus = req.PccBus;
            if (zSelf > 0.0)
            {
                for (int n = 0; n < model.BusCount; n++)
                {
                    if (n == pccIndex) continue;
                    Bus b = model.Buses[n];
                    double t = tr.VoltageAt(h, n).Magnitude / zSelf;
                    if (!(t > req.TransferThreshold) || t == 0.0) continue;
                    if (!(b.BaseKv > 0.0) || !double.IsFinite(b.BaseKv)) { if (!missing.Contains("nominal voltage of bus " + b.Id)) missing.Add("nominal voltage of bus " + b.Id); continue; }
                    double plN = G55Limits.PlanningLevelPct(b.BaseKv, h);
                    double bgN;
                    if (req.RemoteBackgroundPct is not null && req.RemoteBackgroundPct.TryGetValue(b.Id, out var perOrder) && perOrder.TryGetValue(h, out double v)) bgN = v;
                    else if (req.AssumedRemoteBackgroundFraction is double f) { bgN = f * plN; usedAssumedRemote = true; }
                    else { missing.Add($"remote background bus {b.Id} order {h}"); continue; }
                    double transferred = G55Limits.Headroom(plN, bgN, alpha) / t;
                    if (transferred < minTransferred) { minTransferred = transferred; limitingBus = b.Id; }
                }
            }
            double minHeadroom = Math.Min(hPcc, minTransferred);
            if (minHeadroom == hPcc) limitingBus = req.PccBus;
            double limInc = mult * minHeadroom;
            double limTotal = alpha == 1.0 ? bgPcc + mult * hPcc : G55Limits.AggregateWithExponent([bgPcc, mult * hPcc], alpha);
            double vInc = inc.VoltageAt(h, pccIndex).Magnitude * 100.0;
            double ratio = 1.0;
            if (req.UserShuntImpedancePu is not null && req.UserShuntImpedancePu.TryGetValue(h, out Complex zf) && zSelf > 0.0)
            {
                Complex zPre = zPccSelf;
                Complex zPost = zPre * zf / (zPre + zf);
                ratio = zPost.Magnitude / zPre.Magnitude;
                vInc *= ratio;
            }
            double vTot = G55Limits.AggregateWithExponent([vInc, ratio * bgPcc], alpha);
            double resonant = alpha == 1.0 ? Math.Max(mult * hPcc - vInc, 0.0)
                : (DetPow(mult * hPcc, alpha) > DetPow(vInc, alpha) ? DetPow(DetPow(mult * hPcc, alpha) - DetPow(vInc, alpha), 1.0 / alpha) : 0.0);
            bool incOk = vInc <= limInc;
            bool totOk = vTot <= limTotal;
            allWithin &= incOk && totOk;
            sumTotSq += vTot * vTot;
            sumIncSq += vInc * vInc;
            rows.Add(new G55Stage3Row(h, plPcc, bgPcc, hPcc, minTransferred, limitingBus, mult, limInc, vInc, limTotal, vTot, resonant, alpha, incOk, totOk));
        }
        if (usedAssumedRemote)
            assumptions.Add($"Remote-node background taken at {req.AssumedRemoteBackgroundFraction:R} of each node's planning level (declared by the caller, not measured).");
        assumptions.Add("Fundamental voltage taken as 1 pu at every node; distortion is a ratio to the nominal fundamental.");
        if (omitted.Count > 0)
            assumptions.Add("Triplen components omitted (zero sequence, not represented by the balanced model; use the three-phase study): " + string.Join("; ", omitted) + ".");
        if (req.UserShuntImpedancePu is not null)
            assumptions.Add("The new user's own impedance scales the incremental voltage by |Z_post|/|Z_pre| at the PCC (exact for emission injected at the PCC).");
        double thdTot = Math.Sqrt(sumTotSq);
        string outcome = missing.Count > 0 ? G55Outcome.Incomplete : allWithin && thdTot <= thdPl ? G55Outcome.Pass : G55Outcome.Fail;
        return new G55Stage3Result(outcome, thdPl, thdTot, Math.Sqrt(sumIncSq), rows, missing, assumptions, inc.ComponentsOutsideOrders);
    }

    private static double DetPow(double x, double y) => GridSim.Core.Numerics.DetMath.Pow(x, y);
}
