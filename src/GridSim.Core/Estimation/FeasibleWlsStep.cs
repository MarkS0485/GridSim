using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.Estimation;

/// <summary>
/// The physics-feasible estimation backend (<see cref="EstimationMethod.Feasible"/>): the
/// equality-constrained KKT of <see cref="ConstrainedDenseWlsStep"/> PLUS an ACTIVE SET that enforces
/// the state-coupled operational inequalities at SOLVE time - generator reactive capability
/// (Qg in [Qmin,Qmax]), across-branch angle difference (|theta_f - theta_t| &lt;= limit), and branch
/// apparent-power rating (|S_end| &lt;= RateA). Where the acceptance gate (Ring C) would reject a
/// converged state that breaches one of these, the active set instead pulls the solve onto the
/// boundary as it iterates, so the estimate converges to a feasible state rather than being thrown away.
///
/// Each Gauss-Newton major iteration linearises every currently-violated inequality at the current
/// point and appends its row to the augmented system's constraint block alongside the zero-injection
/// equalities. The Jacobian rows come straight from the measurement model - injection-Q partials
/// (<see cref="MeasurementModel.InjectionQRow"/>) for Q-capability, the two +-1 angle entries for the
/// angle difference, and the branch flow partials (<see cref="MeasurementModel.BranchFlowRow"/>) for
/// the thermal limit, whose nonlinear |S| &lt;= RateA is enforced through its squared form
/// S^2 = P^2 + Q^2 (row 2P*dP + 2Q*dQ) - so there is no copy-drift. A constraint no longer violated at
/// the next point simply drops out (re-evaluated each iteration); the active set converges with the state.
///
/// The constraint block is row-equilibrated (every constraint row, its C^T column and its right-hand side scaled
/// by an exact power of two near sqrt(max diag G)) so constraint pivots are not lost under the relative pivot
/// floor beside a gain of 1e6-1e8; this rescales only the multipliers. The angle-difference rows carry a
/// hysteresis band like the thermal rows (R257 follow-up, ruling Q12). The generator-Q rows carry a band too and are
/// released on the sign of their previous multiplier (N043), so a Qg at its limit is neither cycled nor frozen.
///
/// Load-bearing invariant: with no equality constraint and no active inequality the augmented system
/// IS the free normal equations, so the step matches <see cref="DenseWlsStep"/> to solver tolerance -
/// the feasible method costs nothing on a state that is already feasible. Dense backend only; the
/// estimator selects it under the same 3,000-bus fail-loud guard as the constrained method.
/// Observability and residual covariance delegate to <see cref="ConstrainedDenseWlsStep"/> (equalities
/// enter both; inequalities only add information, never remove it).
/// </summary>
internal sealed class FeasibleWlsStep : IWlsStep
{
    /// <summary>Hysteresis of an active thermal constraint, as a fraction of the rating squared: the constraint
    /// is held until |S|^2 drops below (1 - this) x rating^2, so it does not chatter at the limit (R174).</summary>
    internal const double ThermalHysteresisFraction = 1e-3;

    /// <summary>Hysteresis of an active across-branch angle-difference constraint, as a fraction of the angle limit
    /// (R257 follow-up, ruling Q12): the row is active once |theta_f - theta_t| exceeds (1 - this) x limit, and its
    /// right-hand side is clamped so it can only hold or pull the difference back towards the limit, never push a
    /// difference that is under the limit up onto it. The same scheme as the thermal block. Without it the angle
    /// active set switched on and off at the boundary (released at the limit, re-tripped by the data the next
    /// iteration) and the solve cycled to MaxIterations.</summary>
    internal const double AngleHysteresisFraction = 1e-3;

    /// <summary>Hysteresis of an active generator reactive-capability constraint, as a fraction of the bus's
    /// reactive capability range Qmax - Qmin (N043, the angle rows' scheme applied to the Q rows): the row is active
    /// once Qg is within this band of a bound (or past it), and its right-hand side is clamped so it can only hold Qg
    /// or pull it back to the bound. When only one bound is finite the band is this fraction of that bound's
    /// magnitude. Never narrower than <see cref="GeneratorQBandFloorPu"/>.</summary>
    internal const double GeneratorQHysteresisFraction = 1e-3;

    /// <summary>The narrowest generator-Q band (pu): the old trigger tolerance, 1e-6 pu, so a bus with a zero
    /// capability range (Qmin = Qmax) or a zero single bound keeps the behaviour it had at the bound.</summary>
    internal const double GeneratorQBandFloorPu = 1e-6;

    private readonly ILinearSolver _linear;
    private readonly FeasibilityConstraints _feas;
    private readonly ConstrainedDenseWlsStep _base;

    private GridModel _model = null!;
    private MeasurementModel _mm = null!;
    private double[] _w = null!;
    private bool[] _isEq = null!;
    private int _nEq;
    private double[] _h = null!;
    private double[,] _jac = null!;
    private double[,]? _kkt;   // reused while the KKT size is unchanged (R119)

    // Generator Q-capability candidates (non-slack buses with a finite reactive limit), in pu.
    private int[] _qBus = null!;      // state bus index
    private double[] _qMinPu = null!;
    private double[] _qMaxPu = null!;
    private double[] _qdPu = null!;
    private double[] _qBandPu = null!; // hysteresis band per candidate (N043), pu
    private int[] _qPrevSide = null!;      // bound active on the previous solve: +1 upper, -1 lower, 0 none (N043)
    private double[] _qPrevLambda = null!; // that row's multiplier on the previous solve (sign = binding or not)
    private double[] _qRow = null!;   // scratch: one dQinj/dx row (length StateSize)

    // Angle-difference candidates (per branch): the two bus indices and their angle state columns
    // (-1 for a slack end). |theta_from - theta_to| <= AngleDiffMaxRad, enforced when violated.
    private int[] _adFromBus = null!;
    private int[] _adToBus = null!;
    private int[] _adFromCol = null!;
    private int[] _adToCol = null!;

    // Branch thermal-rating candidates: the branch (for its pi-model stamps), its two state/ybus bus
    // indices, and its RateA^2 in pu^2. |S_end(x)| <= RateA is nonlinear, so it is linearised at the
    // current point via S^2 = P^2 + Q^2 and enforced only for the end that is over rating.
    private Branch[] _thBranch = null!;
    private int[] _thFrom = null!;
    private int[] _thTo = null!;
    private double[] _thRateSqPu = null!;
    private int[] _thGroup = null!;     // parallel-circuit group of each thermal candidate (R257)
    private int _thGroupCount;
    private double[] _thPRow = null!;   // scratch: one dP_flow/dx row (length StateSize)
    private double[] _thQRow = null!;   // scratch: one dQ_flow/dx row (length StateSize)

    public FeasibleWlsStep(ILinearSolver linear, FeasibilityConstraints feas)
    {
        _linear = linear;
        _feas = feas;
        _base = new ConstrainedDenseWlsStep(linear);
    }

    public void Begin(GridModel model, MeasurementModel mm, double[] w)
    {
        _base.Begin(model, mm, w);
        _model = model;
        _mm = mm;
        _w = w;
        int m = mm.MeasurementCount;
        int s = mm.StateSize;

        _isEq = new bool[m];
        _nEq = 0;
        for (int r = 0; r < m; r++)
            if (string.Equals(mm.Measurements[r].Source, ConstrainedDenseWlsStep.ConstraintSource, StringComparison.Ordinal))
            { _isEq[r] = true; _nEq++; }

        _h = new double[m];
        _jac = new double[m, s];
        _qRow = new double[s];
        _thPRow = new double[s];
        _thQRow = new double[s];

        BuildQCandidates(model, mm);
        BuildAngleCandidates(model, mm);
        BuildThermalCandidates(model, mm);
    }

    /// <summary>The wrapped constrained backend's pseudo-weight is derived from the weights, so re-weighting
    /// re-runs the full <see cref="Begin"/> (the robust methods never select this backend).</summary>
    public void RebindWeights(double[] w)
    {
        if (_mm is null)
            throw new InvalidOperationException("RebindWeights called before Begin: there is no measurement set to re-weight.");
        if (w.Length != _mm.MeasurementCount)
            throw new ArgumentException($"RebindWeights: {w.Length} weights for {_mm.MeasurementCount} measurements.", nameof(w));
        Begin(_model, _mm, w);
    }

    /// <summary>Per in-service branch with a finite positive RateA, its state/ybus bus indices and its
    /// squared thermal rating in pu^2. A branch with no rating is not a constraint.</summary>
    private void BuildThermalCandidates(GridModel model, MeasurementModel mm)
    {
        var busIndex = new Dictionary<int, int>(model.Buses.Count);
        for (int i = 0; i < model.Buses.Count; i++) busIndex[model.Buses[i].Id] = i;

        double baseMva = mm.BaseMva;
        var br = new List<Branch>(); var fb = new List<int>(); var tb = new List<int>();
        var rate = new List<double>();
        foreach (Branch b in model.Branches)
        {
            if (!b.InService || b.RateAMva <= 0 || double.IsInfinity(b.RateAMva)) continue;
            if (!busIndex.TryGetValue(b.From, out int f) || !busIndex.TryGetValue(b.To, out int t)) continue;
            double ratePu = b.RateAMva / baseMva;
            br.Add(b); fb.Add(f); tb.Add(t); rate.Add(ratePu * ratePu);
        }
        _thBranch = br.ToArray();
        _thFrom = fb.ToArray();
        _thTo = tb.ToArray();
        _thRateSqPu = rate.ToArray();

        // R257: parallel circuits between the same two buses form one thermal GROUP. Their flow gradients are
        // functions of the same terminal voltages, so two active rows from one group are identical (identical
        // circuits) or nearly collinear, which made the KKT singular. Per group only the most over-rated
        // circuit is enforced each iteration (see TrySolve).
        var groupOf = new Dictionary<(int, int), int>();
        _thGroup = new int[_thBranch.Length];
        for (int k = 0; k < _thBranch.Length; k++)
        {
            var key = (Math.Min(_thFrom[k], _thTo[k]), Math.Max(_thFrom[k], _thTo[k]));
            if (!groupOf.TryGetValue(key, out int gid)) { gid = groupOf.Count; groupOf[key] = gid; }
            _thGroup[k] = gid;
        }
        _thGroupCount = groupOf.Count;
    }

    /// <summary>Per branch, the two bus indices and their angle state columns, for the across-branch
    /// angle-difference constraint. A branch with both ends slack (never happens) is skipped.</summary>
    private void BuildAngleCandidates(GridModel model, MeasurementModel mm)
    {
        var busIndex = new Dictionary<int, int>(model.Buses.Count);
        for (int i = 0; i < model.Buses.Count; i++) busIndex[model.Buses[i].Id] = i;

        var fb = new List<int>(); var tb = new List<int>();
        var fc = new List<int>(); var tc = new List<int>();
        // R257: the angle-difference constraint depends only on the two buses, so parallel circuits (or a
        // circuit authored in the reverse orientation) give IDENTICAL rows (or their negation). Two such rows
        // active together made the KKT exactly singular and the solve was reported as Unobservable. One
        // candidate per unordered bus pair holds the limit for the whole corridor.
        var seenPairs = new HashSet<(int, int)>();
        foreach (Branch br in model.Branches)
        {
            if (!br.InService || !busIndex.TryGetValue(br.From, out int f) || !busIndex.TryGetValue(br.To, out int t)) continue;
            int fCol = mm.AngleColumn(f), tCol = mm.AngleColumn(t);
            if (fCol < 0 && tCol < 0) continue; // both slack: nothing to constrain
            if (!seenPairs.Add((Math.Min(f, t), Math.Max(f, t)))) continue; // parallel circuit: same row
            fb.Add(f); tb.Add(t); fc.Add(fCol); tc.Add(tCol);
        }
        _adFromBus = fb.ToArray(); _adToBus = tb.ToArray();
        _adFromCol = fc.ToArray(); _adToCol = tc.ToArray();
    }

    /// <summary>Aggregate each non-slack generator bus's reactive capability [Qmin,Qmax] and its load
    /// Qd, all in pu; only buses with a finite bound become constraint candidates.</summary>
    private void BuildQCandidates(GridModel model, MeasurementModel mm)
    {
        double baseMva = mm.BaseMva;
        var busIndex = new Dictionary<int, int>(model.Buses.Count);
        var isSlack = new HashSet<int>();
        var qd = new Dictionary<int, double>();
        for (int i = 0; i < model.Buses.Count; i++)
        {
            Bus b = model.Buses[i];
            busIndex[b.Id] = i;
            qd[b.Id] = b.Qd;
            if (b.Type == BusType.Slack) isSlack.Add(b.Id);
        }

        var qMin = new Dictionary<int, double>();
        var qMax = new Dictionary<int, double>();
        // GR-517: the candidate order is the order buses first appear in the generator list, kept explicitly, not the
        // dictionary's enumeration order (insertion order today for an add-only dictionary, but not a documented promise).
        var genBusOrder = new List<int>();
        foreach (Generator g in model.Generators)
        {
            if (!qMin.ContainsKey(g.Bus)) genBusOrder.Add(g.Bus);
            qMin[g.Bus] = qMin.GetValueOrDefault(g.Bus, 0.0) + g.Qmin;
            qMax[g.Bus] = qMax.GetValueOrDefault(g.Bus, 0.0) + g.Qmax;
        }

        var bus = new List<int>();
        var lo = new List<double>();
        var hi = new List<double>();
        var load = new List<double>();
        foreach (int id in genBusOrder)
        {
            double qmn = qMin[id];
            if (isSlack.Contains(id) || !busIndex.TryGetValue(id, out int i)) continue;
            double qmx = qMax[id];
            if (double.IsInfinity(qmn) && double.IsInfinity(qmx)) continue; // unbounded => not a constraint
            bus.Add(i);
            lo.Add(double.IsInfinity(qmn) ? double.NegativeInfinity : qmn / baseMva);
            hi.Add(double.IsInfinity(qmx) ? double.PositiveInfinity : qmx / baseMva);
            load.Add(qd.GetValueOrDefault(id, 0.0) / baseMva);
        }
        _qBus = bus.ToArray();
        _qMinPu = lo.ToArray();
        _qMaxPu = hi.ToArray();
        _qdPu = load.ToArray();

        // N043: the hysteresis band of each candidate, fixed for the whole solve (it depends only on the data).
        _qBandPu = new double[_qBus.Length];
        for (int k = 0; k < _qBus.Length; k++) _qBandPu[k] = QBandPu(_qMinPu[k], _qMaxPu[k]);
        // A fresh solve starts with no generator-Q bound active (Begin runs at the start of every solve and of
        // every bad-data pass, so no active-set memory leaks between them).
        _qPrevSide = new int[_qBus.Length];
        _qPrevLambda = new double[_qBus.Length];
    }

    /// <summary>The generator-Q hysteresis band (pu) of a bus with capability [<paramref name="qMinPu"/>,
    /// <paramref name="qMaxPu"/>] (N043): <see cref="GeneratorQHysteresisFraction"/> of the range when both bounds
    /// are finite, of the finite bound's magnitude when only one is, never below <see cref="GeneratorQBandFloorPu"/>.
    /// An inverted range (Qmin above Qmax) is contradictory data and fails loud rather than producing a negative
    /// band that would hold a bus from both sides.</summary>
    internal static double QBandPu(double qMinPu, double qMaxPu)
    {
        if (double.IsNaN(qMinPu) || double.IsNaN(qMaxPu))
            throw new ArgumentException($"Generator reactive limits must be numbers (got Qmin {qMinPu}, Qmax {qMaxPu} pu).");
        if (qMinPu > qMaxPu)
            throw new ArgumentException($"Generator reactive limits are inverted: Qmin {qMinPu} pu is above Qmax {qMaxPu} pu.");
        double scale;
        bool minFinite = !double.IsInfinity(qMinPu), maxFinite = !double.IsInfinity(qMaxPu);
        if (minFinite && maxFinite) scale = qMaxPu - qMinPu;
        else if (maxFinite) scale = Math.Abs(qMaxPu);
        else if (minFinite) scale = Math.Abs(qMinPu);
        else scale = 0.0;   // unbounded: never a candidate (BuildQCandidates skips it), kept total for safety
        return Math.Max(GeneratorQBandFloorPu, GeneratorQHysteresisFraction * scale);
    }

    public ObservabilityReport GateObservability(double[] vm, double[] va) => _base.GateObservability(vm, va);

    public (double[] Normalized, bool[] Critical) NormalizedResiduals(double[] vm, double[] va, double[] residualPu)
        => _base.NormalizedResiduals(vm, va, residualPu);

    public bool TrySolve(double[] vm, double[] va, double[] dxOut)
    {
        int s = _mm.StateSize;
        int m = _mm.MeasurementCount;
        _mm.EvaluateH(vm, va, _h);
        _mm.EvaluateJacobian(vm, va, _jac); // clears then fills

        // Active inequality constraints: linearise each Q-capability bound that is violated or inside its
        // hysteresis band at the current point.
        var cRows = new List<double[]>();
        var cRhs = new List<double>();
        // Generator-Q rows come first among the inequality rows; these record, per Q row in order, its candidate
        // index and its side (+1 upper, -1 lower), so its multiplier can be read back after the solve (N043).
        var qRowCandidate = new List<int>();
        var qRowSide = new List<int>();
        const double qTolPu = 1e-6;   // a bound counts as violated once Qg is this far past it (unchanged)
        // Honor the constraint record's toggle exactly as the thermal block
        // below honors EnforceBranchRatings - this loop used to run
        // unconditionally, enforcing Q-capability with the flag off.
        //
        // Hysteresis (N043). The old rule activated a bound's row only while Qg was more than 1e-6 pu past it, so a
        // Qg pulled exactly onto its limit was released the next iteration, the data pushed it over again, and the
        // solve alternated between a constrained and an unconstrained step until MaxIterations (case118 with
        // noise; the Ring B angle-cap scenario on case14). The rows now carry a band of GeneratorQHysteresisFraction
        // of the capability range (QBandPu), as the angle rows do, with ONE difference from the angle rows, which
        // matters for reactive limits:
        //
        //   - A bound past which Qg lies by more than 1e-6 pu is activated, as before, with its full right-hand
        //     side (pull Qg back onto the bound).
        //   - A row that was active on the PREVIOUS solve stays active while Qg is inside the band (Qg above
        //     Qmax - band, or below Qmin + band) AND its multiplier on that solve said the bound was binding (the
        //     data was pushing Qg through it: lambda > 0 for an upper bound, lambda < 0 for a lower one). It keeps
        //     the full right-hand side, so the step lands Qg on the bound instead of letting it drift off and
        //     re-trip. A row whose multiplier says the data is pulling Qg back inside is released.
        //
        // Why not the angle rows' "active anywhere in the band, right-hand side clamped to hold" rule: a generator
        // very often sits legitimately AT its reactive limit (a PV bus switched to PQ in the power flow), and the
        // iterate approaches that limit from inside. The clamp then froze Qg wherever it first entered the band,
        // below its true value; tried on 1.95.089 data it turned the noise-free case57 fit (J 1.7e-23) into J 224.8
        // with 4 good measurements removed as "bad data". Releasing on the multiplier sign is the standard
        // active-set rule and has no such freeze.
        for (int k = 0; _feas.EnforceGeneratorQ && k < _qBus.Length; k++)
        {
            int i = _qBus[k];
            double qInj = _mm.InjectionQRow(i, vm, va, _qRow); // fills _qRow = dQinj/dx
            double qg = qInj + _qdPu[k];
            double band = _qBandPu[k];
            bool hasMax = !double.IsInfinity(_qMaxPu[k]), hasMin = !double.IsInfinity(_qMinPu[k]);
            // Was this bound active and binding on the previous solve? (+1 upper, -1 lower, 0 neither.)
            bool heldUpper = _qPrevSide[k] == +1 && _qPrevLambda[k] > 0.0;
            bool heldLower = _qPrevSide[k] == -1 && _qPrevLambda[k] < 0.0;
            int side;
            if (hasMax && (qg > _qMaxPu[k] + qTolPu || (heldUpper && qg > _qMaxPu[k] - band))) side = +1;
            else if (hasMin && (qg < _qMinPu[k] - qTolPu || (heldLower && qg < _qMinPu[k] + band))) side = -1;
            else continue;
            double targetInj = (side > 0 ? _qMaxPu[k] : _qMinPu[k]) - _qdPu[k];
            cRows.Add((double[])_qRow.Clone());
            cRhs.Add(targetInj - qInj); // Qinj(x0) + J dx = target  =>  J dx = target - Qinj(x0)
            qRowCandidate.Add(k);
            qRowSide.Add(side);
        }

        // Active angle-difference constraints: |theta_from - theta_to| <= AngleDiffMaxRad. Trivial
        // Jacobian (+1 at the from angle column, -1 at the to angle column).
        //
        // Hysteresis (R257 follow-up, ruling Q12), the same two details as the thermal block below: (1) the row is
        // active from (1 - AngleHysteresisFraction) x limit, so a difference sitting AT the limit keeps its row
        // instead of being released and re-tripped by the data next iteration (the old 1e-9 trigger cycled on and
        // off at the boundary); (2) the right-hand side is clamped so the row can only HOLD a difference inside the
        // band or PULL an over-limit one back to the limit, never push an under-limit difference up onto it.
        double angLimit = _feas.AngleDiffMaxRad;
        if (angLimit > 0)
        {
            double angTrigger = angLimit - angLimit * AngleHysteresisFraction;
            for (int k = 0; k < _adFromBus.Length; k++)
            {
                double diff = va[_adFromBus[k]] - va[_adToBus[k]];
                double rhsAngle;
                // (theta_f - theta_t) + J dx = target  =>  J dx = target - diff, clamped as described above.
                if (diff > angTrigger) rhsAngle = Math.Min(0.0, angLimit - diff);
                else if (diff < -angTrigger) rhsAngle = Math.Max(0.0, -angLimit - diff);
                else continue;
                var row = new double[s];
                if (_adFromCol[k] >= 0) row[_adFromCol[k]] = 1.0;
                if (_adToCol[k] >= 0) row[_adToCol[k]] = -1.0;
                cRows.Add(row);
                cRhs.Add(rhsAngle);
            }
        }

        // Active branch-thermal constraints: |S_end(x)| <= RateA is nonlinear, so linearise the squared
        // form S^2 = P^2 + Q^2 at the current point. d(S^2) = 2P dP + 2Q dQ, so the constraint row is
        // 2P*(dP/dx) + 2Q*(dQ/dx). Only the MORE-LOADED end of a branch is constrained: the two ends
        // differ only by the branch loss, so their gradient rows are near-collinear and enforcing both
        // would make the KKT singular; holding the worse end holds the rating.
        //
        // Two details keep the active set from limit-cycling against a strong flow measurement that wants
        // the branch over rating: (1) a small hysteresis band keeps the constraint active while the flow
        // sits AT the rating (releasing exactly at the boundary lets the data overshoot and re-trip it);
        // (2) the RHS is clamped <= 0 so the row can only HOLD or PULL DOWN an over-rating flow, never
        // push a below-rating flow up onto the limit. At the boundary the RHS is ~0, so the step moves
        // along the limit surface (dS^2 ~ 0) and the solve converges there.
        if (_feas.EnforceBranchRatings)
        {
            // Pass 1: which circuits are active, and per parallel group (R257) the most over-rated one
            // (largest S^2 / RateA^2). A group with a single circuit selects exactly that circuit, so a network
            // without parallel circuits enforces exactly the rows it always did.
            var groupPick = new int[_thGroupCount];
            var groupRatio = new double[_thGroupCount];
            Array.Fill(groupPick, -1);
            var worseFrom = new bool[_thBranch.Length];
            var worseSq = new double[_thBranch.Length];
            for (int k = 0; k < _thBranch.Length; k++)
            {
                double rateSq = _thRateSqPu[k];
                double band = rateSq * ThermalHysteresisFraction; // hysteresis: hold until the flow drops clearly below rating
                (double pf, double qf) = _mm.BranchFlowRow(_thBranch[k], _thFrom[k], _thTo[k], fromEnd: true, vm, va, _thPRow, _thQRow);
                double sSqFrom = pf * pf + qf * qf;
                (double pt, double qt) = _mm.BranchFlowRow(_thBranch[k], _thFrom[k], _thTo[k], fromEnd: false, vm, va, _thPRow, _thQRow);
                double sSqTo = pt * pt + qt * qt;

                bool fromWorse = sSqFrom >= sSqTo;
                double sSq = fromWorse ? sSqFrom : sSqTo;
                if (sSq <= rateSq - band) continue; // clearly under rating: inactive
                worseFrom[k] = fromWorse;
                worseSq[k] = sSq;
                int gid = _thGroup[k];
                double ratio = sSq / rateSq;
                if (groupPick[gid] < 0 || ratio > groupRatio[gid]) { groupPick[gid] = k; groupRatio[gid] = ratio; }
            }

            // Pass 2: one row per active group, in candidate order (deterministic).
            for (int k = 0; k < _thBranch.Length; k++)
            {
                if (groupPick[_thGroup[k]] != k) continue;
                double rateSq = _thRateSqPu[k];
                // Fill the scratch rows for the worse end.
                (double p, double q) = _mm.BranchFlowRow(_thBranch[k], _thFrom[k], _thTo[k], fromEnd: worseFrom[k], vm, va, _thPRow, _thQRow);
                var row = new double[s];
                for (int col = 0; col < s; col++) row[col] = 2.0 * p * _thPRow[col] + 2.0 * q * _thQRow[col];
                cRows.Add(row);
                cRhs.Add(Math.Min(0.0, rateSq - worseSq[k])); // hold or pull down to RateA^2; never push up
            }
        }

        int c = _nEq + cRows.Count;
        // R119: reuse the KKT buffer while its size (s + active constraints) is unchanged - it is cleared first,
        // so this is bit-identical to a fresh allocation; a change in the active set re-allocates.
        if (_kkt is null || _kkt.GetLength(0) != s + c) _kkt = new double[s + c, s + c];
        else Array.Clear(_kkt);
        double[,] kkt = _kkt;
        var rhs = new double[s + c];

        // Free-row normal equations G_f, g_f (skip the equality rows).
        for (int r = 0; r < m; r++)
        {
            if (_isEq[r]) continue;
            double wr = _w[r];
            double wres = wr * (_mm.ZPu[r] - _h[r]);
            for (int c1 = 0; c1 < s; c1++)
            {
                double h1 = _jac[r, c1];
                if (h1 == 0) continue;
                rhs[c1] += h1 * wres;
                double wh = wr * h1;
                // kkt[c1, c2] += wh * _jac[r, c2] for c2 = c1 .. s-1 (bit-exact vector kernel, R118).
                Numerics.SimdKernels.AddScaledRow(ref kkt[0, 0], c1 * (s + c) + c1, ref _jac[0, 0], r * s + c1, s - c1, wh, fastFma: false);
            }
        }

        // Row equilibration of the constraint block (R257 follow-up, ruling Q12). The free gain G = H^T W H has
        // entries of 1e6-1e8 with standard sigmas, while a constraint row (a Jacobian row, or +-1 for an angle) has
        // entries of order 1. After elimination a constraint pivot is about |C|^2 / |G|, i.e. about |G|^2 below
        // the largest KKT entry, which falls under the shared relative pivot floor (1e-12 x max|A|): case14 at its
        // true state with ONE active angle row was declared singular, and case57 / case118 failed at iteration 0
        // on their generator-Q rows. Scaling every constraint row (and its C^T column and right-hand side) by
        // alpha ~ sqrt(max diag G) brings the constraint pivots to about |C|^2, the same order as the gain's.
        //
        // Why it leaves the answer alone: with D = diag(I, alpha I),
        //     [G  alpha C^T] [dx          ]   [g        ]
        //     [alpha C   0 ] [lambda/alpha] = [alpha r_c]
        // is the original KKT with its constraint rows multiplied by alpha and the multipliers divided by alpha;
        // dx is the same vector mathematically, and only the (unused) multipliers are rescaled.
        //
        // alpha is a POWER OF TWO, 2^floor(e/2) where e = ILogB(max diag G) (so sqrt(max diag G)/alpha lies in
        // [1, 2)): multiplying by it is exact in binary floating point, so the scaled entries carry no rounding of
        // their own, and the exponent comes from integer arithmetic, identical on every CPU. A gain with no
        // positive finite diagonal (no free information, or a non-finite entry the solve will reject) is left
        // unscaled (alpha = 1).
        // The computation is shared with the Constrained method (N044), unchanged: same loop, same bits.
        double alpha = ConstrainedDenseWlsStep.ConstraintRowScale(kkt, s);

        // Constraint block alpha*C / alpha*C^T: zero-injection equalities then the active inequalities, each row
        // and its right-hand side scaled by alpha.
        int ci = 0;
        for (int r = 0; r < m; r++)
        {
            if (!_isEq[r]) continue;
            int row = s + ci;
            for (int col = 0; col < s; col++)
            {
                double v = alpha * _jac[r, col];
                kkt[row, col] = v;
                kkt[col, row] = v;
            }
            rhs[row] = alpha * (_mm.ZPu[r] - _h[r]);
            ci++;
        }
        for (int a = 0; a < cRows.Count; a++)
        {
            int row = s + ci;
            double[] cr = cRows[a];
            for (int col = 0; col < s; col++)
            {
                double v = alpha * cr[col];
                kkt[row, col] = v;
                kkt[col, row] = v;
            }
            rhs[row] = alpha * cRhs[a];
            ci++;
        }

        // Mirror the free block's upper triangle (H^TWH is symmetric).
        for (int c1 = 0; c1 < s; c1++)
            for (int c2 = c1 + 1; c2 < s; c2++)
                kkt[c2, c1] = kkt[c1, c2];

        try
        {
            _linear.SolveInPlace(kkt, rhs); // indefinite KKT: partial-pivot LU handles it
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        Array.Copy(rhs, dxOut, s);

        // N043: remember which generator-Q bounds were active on this solve and their multipliers, for the next
        // iteration's keep-or-release decision. The solved multiplier of constraint row r is rhs[s + r], scaled by
        // 1/alpha by the row equilibration (alpha > 0), so its SIGN is the true multiplier's sign, which is all the
        // decision reads. Q rows follow the _nEq equality rows. Every other candidate is marked inactive.
        Array.Clear(_qPrevSide);
        Array.Clear(_qPrevLambda);
        for (int j = 0; j < qRowCandidate.Count; j++)
        {
            int k = qRowCandidate[j];
            _qPrevSide[k] = qRowSide[j];
            _qPrevLambda[k] = rhs[s + _nEq + j];
        }
        return true;
    }
}
