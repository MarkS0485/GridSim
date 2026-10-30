using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Physics;

/// <summary>
/// An independent audit of a solved grid state against the foundational physical laws of an AC
/// power network - the physics-vocabulary sibling of the numeric residual check. Where the solver
/// drives <c>S = V ⊙ conj(Y V)</c> to zero by Newton iteration, this RE-DERIVES the same balances
/// from the raw branch data by a different arithmetic path (per-branch pi-model flow accumulation,
/// not the solver's nodal YV product) and reports each law's residual by name. It never re-runs the
/// solver, so a pass is genuine corroboration that the solved voltages satisfy the network laws.
/// <para>GR-494, what is and is not independent: the nodal balance (KCL) and energy laws re-derive their sums by a
/// different path and can fail on a solved state. Law 2 (branch flow) recomputes each branch's flow with the same
/// admittance stamp as the result's line flows, so it can only detect a result that does not belong to the model passed
/// in. Law 7 (KVL around a loop) holds by construction for a nodal-voltage solution (every voltage drop is a
/// difference of two node voltages) and is reported Holds with residual 0 for every state.</para>
///
/// SCOPE, stated honestly. GridSim is a POSITIVE-SEQUENCE PHASOR model plus an electromechanical
/// frequency layer. That makes the circuit laws (energy conservation, Ohm, Joule, KCL, KVL,
/// complex power) and AC synchronism directly checkable. It is NOT an electromagnetic-field model,
/// so Ampere's and Gauss's laws are not verifiable here - they are embodied CONSTITUTIVELY (a
/// branch's series reactance X is the lumped consequence of Ampere's inductance; a transformer tap
/// ratio and generator internal EMF are the lumped consequence of Faraday's induction) but there is
/// no field to check. Those laws are reported as <see cref="LawStatus.Constitutive"/> /
/// <see cref="LawStatus.NotModeled"/> rather than given a fabricated residual - the same discipline
/// as the rest of the estate.
/// </summary>
public static class PhysicsLaws
{
    /// <summary>
    /// The default audit tolerance in per-unit (D-DOC-125, October 2026): the power-flow convergence tolerance,
    /// equal to the default of <see cref="PowerFlowOptions.Tolerance"/> (a test pins the two together). Newton
    /// declares a solve converged when the largest bus power mismatch is below this many per-unit on the system
    /// base, so a converged solve promises exactly this much and no more: laws 6 and 8 re-derive that mismatch by a
    /// different arithmetic path, and laws 1, 2 and 3 are identities of the state that hold to rounding.
    /// </summary>
    public const double DefaultTolerancePu = 1e-8;

    /// <summary>
    /// The ONE default pass line of the audit, in MVA (D-DOC-040 / D-DOC-125, October 2026): the convergence
    /// tolerance <see cref="DefaultTolerancePu"/> expressed on the model's own base, <c>BaseMva * 1e-8</c>.
    /// <para>Why relative. The residuals are in MW / MVA, but what a solve promises is a per-unit mismatch. On a
    /// 100 MVA base the two agree (100 * 1e-8 is exactly the double 1e-6, the old absolute default, so every
    /// 100 MVA case keeps its verdict bit for bit). On the 10,000 MVA <c>uk</c> base the solver's own promise is
    /// 1e-4 MW, and the old absolute 1e-6 failed a correctly converged solve (law 6 at 1.06e-6 MW): the line was
    /// a hundred times tighter than the solve it judged. On a 1 or 10 MVA distribution base the line is tighter
    /// than before (1e-8 or 1e-7 MVA), which is what the solver promised there too. The same rule is used by the
    /// solve and estimate exports, <c>physics-check</c>, <c>conformance</c>, the web and desktop panels and the
    /// Python sidecars (<c>physics_audit.py</c>, <c>residual_check.py</c>); an explicit tolerance overrides it.</para>
    /// </summary>
    public static double DefaultTolMva(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.BaseMva * DefaultTolerancePu;
    }

    /// <summary>
    /// Audit <paramref name="result"/> (the solved state of <paramref name="model"/>) against the
    /// network's physical laws. <paramref name="tolMva"/> is the pass threshold on the MVA-scale
    /// residuals; null (the default) applies the one documented rule, <see cref="DefaultTolMva"/>
    /// (<c>BaseMva * 1e-8</c>, the solver's own convergence promise in MVA). A <paramref name="synchronism"/>
    /// sample, when supplied, is scored for law 9; otherwise law 9 reads NotApplicable and says it was not scored.
    /// </summary>
    public static PhysicsLawReport Audit(GridModel model, PowerFlowResult result, double? tolMva = null,
        SynchronismSample? synchronism = null)
        => AuditCore(model, result, tolMva ?? DefaultTolMva(model), synchronism, specified: null);

    /// <summary>
    /// Audit a state whose bus injections are NOT the case file's static dispatch - a state estimate
    /// built from telemetry (R011/R012). The plain <see cref="Audit"/> scores KCL (laws 6 and 8) against
    /// the model's scheduled Pg - Pd, which is right for a power-flow solution of that dispatch but wrong
    /// for an estimate: the real grid's injections differ from the case file by megawatts, so every real
    /// or noisy estimate would "violate" KCL at any engineering tolerance. Here laws 6 and 8 instead
    /// compare the network injection against <paramref name="specified"/> at exactly the buses (and
    /// components) it names; buses it does not name are unconstrained, because the state itself is the
    /// only authority on what flows there. Every other law is scored exactly as in <see cref="Audit"/>.
    /// </summary>
    /// <param name="model">The network the state belongs to.</param>
    /// <param name="result">The state, projected to a power-flow result (pure evaluation).</param>
    /// <param name="specified">The injections the state is physically bound to (bus id -&gt; MW / MVAr).</param>
    /// <param name="tolMva">Pass threshold on the MVA-scale residuals.</param>
    /// <param name="synchronism">Optional dynamics sample for law 9.</param>
    public static PhysicsLawReport AuditAgainst(GridModel model, PowerFlowResult result,
        SpecifiedInjections specified, double tolMva, SynchronismSample? synchronism = null)
    {
        ArgumentNullException.ThrowIfNull(specified);
        return AuditCore(model, result, tolMva, synchronism, specified);
    }

    private static PhysicsLawReport AuditCore(GridModel model, PowerFlowResult result, double tolMva,
        SynchronismSample? synchronism, SpecifiedInjections? specified)
    {
        double baseMva = model.BaseMva;

        // Solved bus voltage phasors and specified injections, keyed by bus id.
        int n = result.BusIds.Count;
        var idx = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idx[result.BusIds[i]] = i;

        var v = new Complex[n];
        for (int i = 0; i < n; i++)
            v[i] = DetMath.FromPolar(result.Vm[i], result.VaDeg[i] * Math.PI / 180.0);

        // Specified injection (generation - demand) per bus, and bus role (slack/PV free components).
        var specP = new double[n];
        var specQ = new double[n];
        var isSlack = new bool[n];
        var isPv = new bool[n];
        // D-DOC-041 (October 2026): which buses the solve ENDED as PV. The reactive balance (law 8) is checked at
        // every bus the solve held to a specified reactive injection, which is every bus it solved as PQ, and that
        // is decided by the types the solve ended with (PowerFlowResult.SolvedBusTypes, N071), not by the types the
        // model declares. Before, a PV bus the reactive-limit loop switched to PQ (its generators pinned at Qmax or
        // Qmin) and a PV bus with no generator (solved as PQ since R283) were both skipped, so their reactive
        // balance was never checked. A solver that does not report its final types (the sweep, a presolved state
        // read from disk) leaves SolvedBusTypes null and the declared types are used, exactly as before.
        IReadOnlyList<BusType>? solvedTypes = result.SolvedBusTypes is { } st && st.Count == n ? st : null;
        var qLoadAt = new double[n];      // the reactive load actually drawn at the solved voltage (MVAr)
        var pinnedAtLimit = new bool[n];  // declared PV, solved PQ, with generators: held at a reactive limit
        var qMinBus = new double[n];      // aggregate generator Qmin / Qmax per bus (MVAr), as the solver sums them
        var qMaxBus = new double[n];
        var hasGen = new bool[n];
        foreach (Bus bus in model.Buses)
        {
            if (!idx.TryGetValue(bus.Id, out int i)) continue;
            // The demand actually drawn at the solved voltage (R065, October 2026). A constant-power bus (no
            // ZIP, or a ZIP that does not vary with V) subtracts exactly Pd/Qd as it always has; a voltage-
            // dependent ZIP load draws Pd*PMultiplier(V), Qd*QMultiplier(V). Before, the nominal Pd/Qd was
            // used for every bus, so an exactly solved ZIP case FAILED laws 6 and 8 by Pd*(PMultiplier(V)-1).
            (double pLoad, double qLoad) = PowerFlow.LoadAtVoltage.Mw(bus, result.Vm[i]);
            specP[i] -= pLoad;
            specQ[i] -= qLoad;
            qLoadAt[i] = qLoad;
            isSlack[i] = bus.Type == BusType.Slack;
            // The bus type the solve ended with when it reports one (D-DOC-041), else the declared type.
            isPv[i] = (solvedTypes is null ? bus.Type : solvedTypes[i]) == BusType.Pv;
        }
        foreach (Generator gen in model.Generators)
        {
            if (!idx.TryGetValue(gen.Bus, out int i)) continue;
            specP[i] += gen.Pg;
            specQ[i] += gen.Qg;
            // The same aggregation as NewtonShell.BuildBusSpec: every generator listed at the bus is summed.
            qMinBus[i] += gen.Qmin;
            qMaxBus[i] += gen.Qmax;
            hasGen[i] = true;
        }
        // A bus declared PV that the solve ended as PQ while it has generators was pinned at a reactive limit by the
        // outer loop (NewtonShell.ApplyQLimits): its specified reactive injection is then that limit minus the load,
        // not the case file's Qg. A declared-PV bus with NO generator solved as PQ (R283) keeps specQ = -load, which is
        // already right (no generator Qg was added above).
        int pinnedCount = 0;
        if (solvedTypes is not null)
            foreach (Bus bus in model.Buses)
            {
                if (!idx.TryGetValue(bus.Id, out int i)) continue;
                if (bus.Type == BusType.Pv && solvedTypes[i] == BusType.Pq && hasGen[i])
                {
                    pinnedAtLimit[i] = true;
                    pinnedCount++;
                }
            }
        int switchedToPqCount = 0;   // declared PV, solved PQ (pinned at a limit, or no generator): newly checked
        if (solvedTypes is not null)
            foreach (Bus bus in model.Buses)
                if (idx.TryGetValue(bus.Id, out int i) && bus.Type == BusType.Pv && solvedTypes[i] == BusType.Pq)
                    switchedToPqCount++;

        // Which bus components the KCL laws (6 and 8) constrain. For the plain audit of a power-flow
        // solution this is the solver's own convention: P everywhere but the slack, Q only at PQ buses.
        // For an audit against explicitly specified injections (a state estimate, R011/R012) it is
        // exactly the buses and components the caller names, at the values the caller gives.
        var constrainP = new bool[n];
        var constrainQ = new bool[n];
        if (specified is null)
        {
            for (int i = 0; i < n; i++)
            {
                constrainP[i] = !isSlack[i];
                constrainQ[i] = !isSlack[i] && !isPv[i];
            }
        }
        else
        {
            ApplySpecified(specified.PMw, "real", idx, specP, constrainP);
            ApplySpecified(specified.QMvar, "reactive", idx, specQ, constrainQ);
        }
        int constrainedPCount = constrainP.Count(c => c);
        int constrainedQCount = constrainQ.Count(c => c);

        // ---- Independent branch-oriented recomputation (Ohm + Joule + the pi-model) ----
        // Sbus[i] accumulates the complex power the NETWORK draws at bus i (pu); branch losses sum
        // the Joule dissipation. This is a separate path from the solver's YV nodal evaluation.
        var sBus = new Complex[n];
        double shuntLossMw = 0.0; // real power dissipated by bus shunt conductances (Gs)

        // Bus shunts: y = (Gs + jBs)/base drawn at the bus, S = V conj(y V).
        foreach (Bus bus in model.Buses)
        {
            if ((bus.Gs == 0 && bus.Bs == 0) || !idx.TryGetValue(bus.Id, out int i)) continue;
            Complex ySh = new Complex(bus.Gs, bus.Bs) / baseMva;
            Complex sShunt = v[i] * Complex.Conjugate(ySh * v[i]);
            sBus[i] += sShunt;
            shuntLossMw += sShunt.Real * baseMva;
        }

        double maxOhmDiffMva = 0.0;    // recomputed branch flow vs the result's reported LineFlow
        double totalBranchLossMw = 0.0;
        double minBranchLossMw = 0.0;  // most-negative branch real loss (Joule floor: must be >= 0)
        int branchOrdinal = 0;
        // Law 2 bookkeeping: how many in-service branches were audited and how many of them had an aligned reported
        // flow to compare against. Law 2 used to report Holds with residual 0 when NO flow aligned (a result whose
        // LineFlows came from a model with a different branch list), having compared nothing.
        int law2Audited = 0, law2Compared = 0;
        foreach (Branch br in model.Branches)
        {
            // Out of service = electrically absent: auditing it computes
            // phantom current through the open branch from the solved
            // voltages, falsely failing the hard-gate laws after any
            // recloser/cascade trip. Ordinal still advances so the LineFlow
            // cross-check stays index-aligned.
            if (!br.InService) { branchOrdinal++; continue; }
            // A branch whose endpoint is not in the solved result cannot be audited, but the ordinal must still
            // advance: LineFlows is index-aligned with Branches, and skipping the increment here (before October
            // 2026, R281) paired every LATER branch with the wrong LineFlow. The endpoint guard below then
            // usually skipped the comparison, so law 2 reported Holds with residual 0 having checked nothing.
            if (!idx.TryGetValue(br.From, out int f) || !idx.TryGetValue(br.To, out int t)) { branchOrdinal++; continue; }

            // The same pi-model stamps as YBus.Build - the network's constitutive admittance. (The
            // stamp arithmetic itself is pinned independently by AdmittanceStampPinTests, so this audit
            // sharing the centralised stamp does not blind it to a defect introduced in that arithmetic.)
            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            law2Audited++;

            // Ohm's law: branch-end currents from the solved terminal voltages.
            Complex iFrom = s.Yff * v[f] + s.Yft * v[t];
            Complex iTo = s.Ytf * v[f] + s.Ytt * v[t];
            Complex sFrom = v[f] * Complex.Conjugate(iFrom); // pu
            Complex sTo = v[t] * Complex.Conjugate(iTo);

            sBus[f] += sFrom;
            sBus[t] += sTo;

            double lossMw = (sFrom + sTo).Real * baseMva; // Joule I^2 R across the branch (pi-model)
            totalBranchLossMw += lossMw;
            // NaN wins the min (as -Infinity, a violation of law 5), instead of being skipped.
            if (!(lossMw >= minBranchLossMw)) minBranchLossMw = double.IsNaN(lossMw) ? double.NegativeInfinity : lossMw;

            // Cross-check against the solver's reported per-branch flow, if aligned.
            if (branchOrdinal < result.LineFlows.Count)
            {
                LineFlow lf = result.LineFlows[branchOrdinal];
                if (lf.From == br.From && lf.To == br.To)
                {
                    law2Compared++;
                    maxOhmDiffMva = Math.Max(maxOhmDiffMva, Math.Abs(sFrom.Real * baseMva - lf.PFromMw));
                    maxOhmDiffMva = Math.Max(maxOhmDiffMva, Math.Abs(sFrom.Imaginary * baseMva - lf.QFromMvar));
                    maxOhmDiffMva = Math.Max(maxOhmDiffMva, Math.Abs(sTo.Real * baseMva - lf.PToMw));
                    maxOhmDiffMva = Math.Max(maxOhmDiffMva, Math.Abs(sTo.Imaginary * baseMva - lf.QToMvar));
                }
            }
            branchOrdinal++;
        }

        // ---- KCL / power balance / S = V I* : network injection vs specified ----
        // The single residual laws 6 and 8 read: the network power drawn at a bus equals the specified
        // injection there, at the components the bus constrains (P everywhere but slack; Q only at buses the
        // solve ended as PQ - slack P/Q and the Q of a bus still regulating as PV are whatever the network
        // requires and are not errors). Law 1 does not use it: it checks total dissipation against the loss.
        double maxBalanceMw = 0.0;   // KCL / S=VI real residual
        double maxBalanceMvar = 0.0;
        double sumNetInjectionMw = 0.0;
        for (int i = 0; i < n; i++)
        {
            double netP = sBus[i].Real * baseMva;
            double netQ = sBus[i].Imaginary * baseMva;
            sumNetInjectionMw += netP;
            if (constrainP[i])
                maxBalanceMw = Math.Max(maxBalanceMw, Math.Abs(netP - specP[i]));
            if (constrainQ[i])
            {
                double qResidual;
                if (specified is null && pinnedAtLimit[i])
                {
                    // D-DOC-041: a PV bus pinned at a reactive limit. The solver fixed its injection at
                    // (Qmax - load) or (Qmin - load) and recorded which one only inside the solve, so the audit
                    // scores the distance to the NEARER of the two: a correct solve sits on one of them to the
                    // convergence tolerance. An infinite limit is never the nearer one (its distance is infinite).
                    double toMax = Math.Abs(netQ - (qMaxBus[i] - qLoadAt[i]));
                    double toMin = Math.Abs(netQ - (qMinBus[i] - qLoadAt[i]));
                    qResidual = Math.Min(toMax, toMin);
                }
                else
                    qResidual = Math.Abs(netQ - specQ[i]);
                maxBalanceMvar = Math.Max(maxBalanceMvar, qResidual);
            }
        }

        // The KCL verdicts. With no constrained component at all there is nothing to compare, so the law
        // is NotApplicable (never a vacuous "Holds"). The plain audit keeps its historical verdicts unchanged.
        string kclBasis = specified is null
            ? "network real injection vs specified, at constrained buses"
            : $"network real injection vs {specified.Basis}, at {constrainedPCount} bus(es) with a specified real injection";
        LawResult kclLaw = specified is not null && constrainedPCount == 0
            ? new(6, PhysicsCheckRegistry.Name(6),
                "Sigma I at a node = 0 (injection balance)",
                LawStatus.NotApplicable, double.NaN, "MW", tolMva,
                $"no bus carries a specified real injection ({specified?.Basis ?? "static dispatch"}); the state alone fixes every injection")
            : new(6, PhysicsCheckRegistry.Name(6),
                "Sigma I at a node = 0 (injection balance)",
                maxBalanceMw <= tolMva ? LawStatus.Holds : LawStatus.Violated,
                maxBalanceMw, "MW", tolMva,
                kclBasis);
        LawResult complexPowerLaw = specified is not null && constrainedPCount == 0 && constrainedQCount == 0
            ? new(8, PhysicsCheckRegistry.Name(8),
                "S_i = V_i conj(Sigma_j Y_ij V_j)",
                LawStatus.NotApplicable, double.NaN, "MVA", tolMva,
                $"no bus carries a specified injection ({specified?.Basis ?? "static dispatch"}); the state alone fixes every injection")
            : new(8, PhysicsCheckRegistry.Name(8),
                "S_i = V_i conj(Sigma_j Y_ij V_j)",
                maxBalanceMvar <= tolMva && maxBalanceMw <= tolMva ? LawStatus.Holds : LawStatus.Violated,
                Math.Max(maxBalanceMw, maxBalanceMvar), "MVA", tolMva,
                // D-DOC-190 (October 2026): law 8 shares its residual with law 6 only (law 1 is the total-dissipation
                // check); the text used to say "laws 1 and 6". D-DOC-041: it also says which bus types it used.
                specified is null
                    ? Law8PlainDetail(constrainedQCount, solvedTypes is not null, switchedToPqCount, pinnedCount)
                    : $"same residual as law 6, reactive component included at {constrainedQCount} bus(es) with a specified reactive injection");

        // ---- Conservation of energy: independent total dissipation == the solver's reported loss ----
        // sumNetInjectionMw is the network power drawn at every bus (branch + shunt), which by
        // construction is the total dissipation; it must equal the solver's TotalLossMw. The shunt
        // conductance (Gs) dissipation is a real loss term, so it is kept separate from branch I^2 R.
        double totalNetworkLossMw = totalBranchLossMw + shuntLossMw;
        double energyResidualMw = Math.Abs(sumNetInjectionMw - result.TotalLossMw);
        double jouleTotalResidualMw = Math.Abs(totalNetworkLossMw - result.TotalLossMw);

        var laws = new List<LawResult>
        {
            new(1, PhysicsCheckRegistry.Name(1),
                "Sigma(generation - load) = Sigma losses",
                energyResidualMw <= tolMva ? LawStatus.Holds : LawStatus.Violated,
                energyResidualMw, "MW", tolMva,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"independent dissipation {sumNetInjectionMw:F6} MW (branch {totalBranchLossMw:F4} + shunt {shuntLossMw:F4}) vs reported loss {result.TotalLossMw:F6} MW")),

            // Law 2 compares only where a reported flow is aligned with the branch. None aligned: NotApplicable (never a
            // vacuous Holds). Some but not all: the verdict stands on those compared and the detail says how many.
            law2Audited > 0 && law2Compared == 0
                ? new(2, PhysicsCheckRegistry.Name(2),
                    "branch current I = y * dV (pi-model)",
                    LawStatus.NotApplicable, double.NaN, "MVA", tolMva,
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"no solver-reported line flow is aligned with any of the {law2Audited} audited branch(es); nothing was compared"))
                : new(2, PhysicsCheckRegistry.Name(2),
                    "branch current I = y * dV (pi-model)",
                    maxOhmDiffMva <= tolMva ? LawStatus.Holds : LawStatus.Violated,
                    maxOhmDiffMva, "MVA", tolMva,
                    law2Compared == law2Audited
                        ? "independent per-branch flow vs solver-reported line flow"
                        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"independent per-branch flow vs solver-reported line flow ({law2Compared} of {law2Audited} audited branches had an aligned reported flow)")),

            new(3, PhysicsCheckRegistry.Name(3),
                "P_loss = |I|^2 R; Sigma branch loss consistent with reported total",
                jouleTotalResidualMw <= tolMva ? LawStatus.Holds : LawStatus.Violated,
                jouleTotalResidualMw, "MW", tolMva,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"total network loss {totalNetworkLossMw:F6} MW vs reported {result.TotalLossMw:F6} MW")),

            new(4, PhysicsCheckRegistry.Name(4),
                "changing B induces EMF",
                LawStatus.Constitutive, double.NaN, "-", tolMva,
                "embodied in transformer tap ratio and generator EMF E' = V + jX'I; no field model to check"),

            // Ampere's law is embodied in the series reactance; its checkable proxy is PASSIVITY -
            // a passive branch dissipates (I^2 R >= 0). A negative branch loss means the MODEL carries a
            // negative resistance (a reduction/equivalent artefact), not a bad STATE, so this is reported
            // (Violated when found) but is NOT a hard-gate law - the estimator cannot fix the model.
            new(5, PhysicsCheckRegistry.Name(5),
                "passive branches dissipate: I^2 R >= 0",
                minBranchLossMw >= -tolMva ? LawStatus.Constitutive : LawStatus.Violated,
                Math.Max(0.0, -minBranchLossMw), "MW", tolMva,
                minBranchLossMw >= -tolMva
                    ? "series reactance X embodies the lumped inductance; all branches passive (I^2 R >= 0)"
                    : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"NON-PASSIVE branch in the model: most-negative branch loss {minBranchLossMw:E2} MW (R < 0 in the data)")),

            kclLaw,

            new(7, PhysicsCheckRegistry.Name(7),
                "Sigma voltage drops around a loop = 0",
                LawStatus.Holds, 0.0, "V", tolMva,
                "structural: nodal voltages are single-valued potentials, so every loop sum vanishes identically"),

            complexPowerLaw,

            SynchronismLaw(model, synchronism, tolMva),

            new(10, PhysicsCheckRegistry.Name(10),
                "electric field from charge; insulation, corona, clearances",
                LawStatus.NotModeled, double.NaN, "-", tolMva,
                "field/geometry law outside a positive-sequence phasor model; not represented"),
        };

        return new PhysicsLawReport(laws, baseMva, result.Converged);
    }

    /// <summary>
    /// The detail line of law 8 in the plain audit (D-DOC-190, D-DOC-041): which residual it shares (law 6's, with the
    /// reactive part added), at how many buses the reactive part was checked, and on which bus types. "Solved" types
    /// are the ones the solve ended with; a bus declared PV that ended as PQ is counted, and one pinned at a reactive
    /// limit is checked against that limit.
    /// </summary>
    internal static string Law8PlainDetail(int qBuses, bool solvedTypesKnown, int switchedToPq, int pinned)
    {
        string types = solvedTypesKnown
            ? "the bus types the solve ended with"
            : "the bus types the model declares (this solver does not report the types it ended with)";
        string switched = switchedToPq == 0
            ? ""
            : $"; {switchedToPq} of them declared PV and solved as PQ ({pinned} held at a reactive limit and checked against that limit, "
              + $"{switchedToPq - pinned} with no generator)";
        return $"same residual as law 6, reactive component included at {qBuses} PQ bus(es), by {types}{switched}";
    }

    /// <summary>Overwrite the static-dispatch specification with caller-specified injections for one
    /// component (real or reactive), marking exactly those buses constrained. Fails loud on a bus the
    /// state does not contain or a non-finite value: silently skipping either would make the KCL verdict
    /// claim a check that never ran.</summary>
    private static void ApplySpecified(IReadOnlyDictionary<int, double> values, string component,
        Dictionary<int, int> idx, double[] spec, bool[] constrain)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (KeyValuePair<int, double> kv in values)
        {
            if (!idx.TryGetValue(kv.Key, out int i))
                throw new ArgumentException(
                    $"Specified {component} injection names bus {kv.Key}, which is not in the audited state.", nameof(values));
            if (!double.IsFinite(kv.Value))
                throw new ArgumentException(
                    $"Specified {component} injection at bus {kv.Key} is {kv.Value}; it must be a finite number.", nameof(values));
            spec[i] = kv.Value;
            constrain[i] = true;
        }
    }

    /// <summary>Law 9 - AC phase and frequency synchronism. A steady-state power flow is a 50 Hz
    /// snapshot (NotApplicable); a dynamics run is scored on a real residual: how close the rotor-angle
    /// spread came to the pole-slip limit and the frequency excursion to its band. The residual is the
    /// larger of the two normalised margins, so &lt; 1 means synchronism held with headroom.</summary>
    private static LawResult SynchronismLaw(GridModel model, SynchronismSample? sample, double tolMva)
    {
        // D-DOC-188 (October 2026): say plainly that the law was NOT SCORED: a power flow is a steady-state snapshot
        // with no trajectory. The old text pointed at StabilityMetrics / ClassicalMultiMachine as if they scored it.
        // N246 (October 2026) and its rest (CF2): `stability --clear`, classical and `--rms`, now scores law 9 from its
        // own run's trajectory (ScoreSynchronism below, on SynchronismFromTrajectory.Classical / .Subtransient, the
        // 180 degree out-of-step criterion), printed beside the run's own pole-slip verdict. The text therefore names
        // that verb instead of saying "no verb or export does", which had become untrue. This moves the
        // physics-check-case9 / -case14 stdout fixtures (one line each; listed in the CF2 notes for the end pass).
        if (sample is null)
            return new(9, PhysicsCheckRegistry.Name(9),
                "synchronous machines share phase and nominal frequency",
                LawStatus.NotApplicable, double.NaN, "Hz", tolMva,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"NOT SCORED: steady-state snapshot at {model.FrequencyHz:F0} Hz with no trajectory; law 9 is scored only when a dynamics caller supplies a SynchronismSample: stability --clear (classical or --rms) scores it from its own run, no steady-state verb or export does"));
        return ScoreSample(sample, tolMva);
    }

    /// <summary>
    /// N246 (October 2026; Ruling R1, Mark delegated EE calls 2026-10-04): score law 9 on a dynamics run's own
    /// <see cref="SynchronismSample"/> (for example <see cref="SynchronismFromTrajectory.Classical"/> on a
    /// <c>stability --clear</c> run). No power-flow state is involved; <paramref name="tolerance"/> is carried into the
    /// result's Tolerance field (the margin threshold is always 1: Holds when the residual is 1 or less).
    /// </summary>
    public static LawResult ScoreSynchronism(SynchronismSample sample, double tolerance = 1.0)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return ScoreSample(sample, tolerance);
    }

    // The scored path of law 9, split out of SynchronismLaw (N246) without changing a byte of its arithmetic or text for
    // a sample with AngleOnly = false.
    private static LawResult ScoreSample(SynchronismSample sample, double tolMva)
    {
        if (sample.AngleOnly)
        {
            // N246: the fault-study out-of-step criterion, angle alone. A non-finite angle (a run that could not be
            // integrated) is a loss of synchronism, never a pass: the margin is +infinity and the text says why.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            bool finite = double.IsFinite(sample.MaxAngleSpreadDeg);
            double margin = !finite ? double.PositiveInfinity
                : sample.PoleSlipLimitDeg > 0 ? sample.MaxAngleSpreadDeg / sample.PoleSlipLimitDeg : double.PositiveInfinity;
            string angleText = finite
                ? $"largest rotor angle from the centre of inertia {sample.MaxAngleSpreadDeg.ToString("F1", inv)} deg"
                : "a rotor angle became non-finite (the run could not be integrated)";
            string freqText = double.IsFinite(sample.MaxFreqExcursionHz)
                ? $"peak speed deviation {sample.MaxFreqExcursionHz.ToString("F3", inv)} Hz, reported, not scored (no frequency band in a fault study)"
                : "peak speed deviation non-finite";
            return new(9, PhysicsCheckRegistry.Name(9),
                "synchronous machines share phase and nominal frequency",
                margin <= 1.0 ? LawStatus.Holds : LawStatus.Violated,
                margin, "margin", tolMva,
                $"{angleText} (limit {sample.PoleSlipLimitDeg.ToString("F0", inv)} deg, the out-of-step criterion); {freqText}");
        }

        double angleMargin = sample.PoleSlipLimitDeg > 0 ? sample.MaxAngleSpreadDeg / sample.PoleSlipLimitDeg : double.PositiveInfinity;
        double freqMargin = sample.FreqBandHz > 0 ? sample.MaxFreqExcursionHz / sample.FreqBandHz : double.PositiveInfinity;
        double residual = Math.Max(angleMargin, freqMargin);
        // Grid-forming: ADD (never replace) the analytic droop-envelope margin, active only when supplied.
        // FormingBoundHz <= 0 leaves residual untouched, so non-forming samples score byte-identically.
        if (sample.FormingBoundHz > 0)
            residual = Math.Max(residual, sample.MaxFreqExcursionHz / sample.FormingBoundHz);
        return new(9, PhysicsCheckRegistry.Name(9),
            "synchronous machines share phase and nominal frequency",
            residual <= 1.0 ? LawStatus.Holds : LawStatus.Violated,
            residual, "margin", tolMva,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"angle spread {sample.MaxAngleSpreadDeg:F1} deg (limit {sample.PoleSlipLimitDeg:F0}), freq excursion {sample.MaxFreqExcursionHz:F3} Hz (band {sample.FreqBandHz:F2})"));
    }
}

/// <summary>
/// Bus injections a state is physically bound to, for <see cref="PhysicsLaws.AuditAgainst"/> (R011/R012).
/// <see cref="PMw"/> and <see cref="QMvar"/> map a bus id to its specified net injection (generation minus
/// demand, MW / MVAr); a bus absent from a map leaves that component unconstrained. <see cref="Basis"/>
/// says in plain English where the values come from (for example "the estimator's zero-injection
/// constraints") and is quoted in the law's detail line so a reader can see what KCL was checked against.
/// </summary>
public sealed record SpecifiedInjections(
    IReadOnlyDictionary<int, double> PMw,
    IReadOnlyDictionary<int, double> QMvar,
    string Basis);
