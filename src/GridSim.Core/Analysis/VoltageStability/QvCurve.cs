using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.VoltageStability;

/// <summary>One point of a QV curve: the held voltage, the reactive output the synthetic unit needed there (MVAr, positive
/// = injecting; zero at the operating point), and whether the power flow converged. A non-converged point has NaN Q.</summary>
public sealed record QvPoint(double VPu, double QMvar, bool Converged);

/// <summary>
/// A QV curve at one bus. <see cref="Points"/> runs from the lowest voltage to the highest, anchored on the operating
/// point at <see cref="BaseVPu"/> (a grid of <see cref="StepPu"/> either side). <see cref="Status"/> is OK, BASE NOT
/// CONVERGED, or OUT OF SCOPE (a slack bus, or a case the sweep cannot be defined on). A sweep branch that met a
/// non-converged point stopped there; that point is in <see cref="Points"/> (Converged false), not skipped, and
/// <see cref="DownEnd"/>/<see cref="UpEnd"/> say how each branch ended ("limit" = reached the requested bound, "non-converged").
/// <see cref="GridMinQMvar"/> is the smallest sampled Q; <see cref="NoseVPu"/>/<see cref="NoseQMvar"/> refine it with the
/// parabola through the three samples around it (exact for a quadratic Q(V), as the closed-form two-bus case is), valid
/// when <see cref="MinBracketed"/> (the minimum is interior to the converged samples). <see cref="MarginMvar"/> =
/// QOperating - Qnose = -NoseQMvar: how much reactive power the bus can still lose before its curve has no intersection
/// with the zero line (a negative margin means the operating point is already past the nose).
/// </summary>
public sealed record QvCurveResult(
    int BusId, string Status, double BaseVPu, double StepPu, IReadOnlyList<QvPoint> Points,
    string DownEnd, string UpEnd, double GridMinQMvar, double GridMinVPu, bool MinBracketed,
    double NoseVPu, double NoseQMvar, double MarginMvar, double BaseResidualMvar);

/// <summary>
/// QV curve (R4-03, report row 6.2). A copy of the case holds a synthetic voltage-controlling unit at the bus with an
/// unbounded reactive range (<see cref="Generator.PowerFlowOnly"/>, last on the bus so its set point wins); the bus is made
/// a PV bus. Units already on the bus are held at their solved base-case reactive output (their combined base output is
/// carried by the first, Qmin = Qmax) so the curve is the EXTRA compensation required, zero at the operating point. For each
/// voltage on the grid the unit's set point is moved and the case solved with the study solver
/// (<see cref="ContingencyAnalysis.CreateStudySolver"/>, <see cref="ContingencyAnalysis.StudyOptions"/>); the unit's
/// reactive output is <c>Q = QInj + Qload(V) - Q0</c> (the bus's solved net injection plus its load at that voltage, by
/// <see cref="LoadAtVoltage"/>, less the held base output Q0). The sweep goes down from the base voltage and up from it.
/// </summary>
public static class QvCurve
{
    /// <summary>Compute the curve at <paramref name="busId"/> between <paramref name="vMinPu"/> and <paramref name="vMaxPu"/>
    /// on a grid of <paramref name="stepPu"/> anchored at the operating point.</summary>
    public static QvCurveResult Compute(GridModel model, int busId, double vMinPu = 0.4, double vMaxPu = 1.2, double stepPu = 0.01)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!(stepPu > 0) || !double.IsFinite(stepPu)) throw new ArgumentException("the voltage step must be a positive finite number.", nameof(stepPu));
        if (!(vMinPu > 0) || !(vMaxPu > vMinPu) || !double.IsFinite(vMaxPu))
            throw new ArgumentException("the voltage range needs 0 < vmin < vmax.", nameof(vMinPu));
        int b = -1;
        for (int i = 0; i < model.BusCount; i++) if (model.Buses[i].Id == busId) { b = i; break; }
        if (b < 0) throw new ArgumentException($"bus {busId} is not in the model.", nameof(busId));

        IPowerFlowSolver solver = ContingencyAnalysis.CreateStudySolver(model);
        PowerFlowOptions options = ContingencyAnalysis.StudyOptions;
        PowerFlowResult baseSolve = solver.Solve(model, options);
        QvCurveResult Refused(string status, double v = double.NaN) =>
            new(busId, status, v, stepPu, Array.Empty<QvPoint>(), "", "", double.NaN, double.NaN, false, double.NaN, double.NaN, double.NaN, double.NaN);
        if (!baseSolve.Converged) return Refused("BASE NOT CONVERGED");
        Bus bus = model.Buses[b];
        if (bus.Type == BusType.Slack) return Refused("OUT OF SCOPE: bus is the slack; its voltage is already fixed and its reactive output is not a free control", baseSolve.Vm[b]);
        double v0 = baseSolve.Vm[b];

        bool hasUnits = model.Generators.Any(g => g.Bus == busId);
        double q0 = hasUnits ? baseSolve.QInjMvar[b] + LoadAtVoltage.Mw(bus, v0).QMvar : 0.0;

        var gens = new List<Generator>();
        bool firstHeld = true;
        foreach (Generator g in model.Generators)
        {
            if (g.Bus != busId) { gens.Add(g); continue; }
            double q = firstHeld ? q0 : 0.0;
            firstHeld = false;
            gens.Add(g with { Qg = q, Qmin = q, Qmax = q });
        }
        var buses = new List<Bus>(model.Buses);

        (bool ok, double q) SolveAt(double v)
        {
            buses[b] = bus with { Type = BusType.Pv, Vm = v };
            var unit = new Generator(busId, 0.0, 0.0, v, double.NegativeInfinity, double.PositiveInfinity,
                0.0, double.PositiveInfinity, model.BaseMva, 0.0, "QV-synthetic", "", PowerFlowOnly: true);
            var all = new List<Generator>(gens) { unit };
            GridModel m = model.CopyWith(buses: buses.ToArray(), generators: all);
            PowerFlowResult r = solver.Solve(m, options);
            if (!r.Converged) return (false, double.NaN);
            return (true, r.QInjMvar[b] + LoadAtVoltage.Mw(bus, r.Vm[b]).QMvar - q0);
        }

        // The operating point first: Q should be ~0 there (the held base output is the whole bus generation).
        (bool okBase, double qBase) = SolveAt(v0);
        if (!okBase) return Refused("BASE NOT CONVERGED: the case with the synthetic unit held at the operating voltage did not solve", v0);

        var down = new List<QvPoint>();
        string downEnd = "limit";
        for (int k = 1; ; k++)
        {
            double v = v0 - k * stepPu;
            if (v < vMinPu - 1e-12) break;
            (bool ok, double q) = SolveAt(v);
            down.Add(new QvPoint(v, q, ok));
            if (!ok) { downEnd = "non-converged"; break; }
        }
        var up = new List<QvPoint>();
        string upEnd = "limit";
        for (int k = 1; ; k++)
        {
            double v = v0 + k * stepPu;
            if (v > vMaxPu + 1e-12) break;
            (bool ok, double q) = SolveAt(v);
            up.Add(new QvPoint(v, q, ok));
            if (!ok) { upEnd = "non-converged"; break; }
        }

        var points = new List<QvPoint>(down.Count + 1 + up.Count);
        for (int i = down.Count - 1; i >= 0; i--) points.Add(down[i]);
        points.Add(new QvPoint(v0, qBase, true));
        points.AddRange(up);

        // The contiguous converged run around the operating point (a non-converged point ends its branch).
        int lo = 0, hi = points.Count - 1;
        while (!points[lo].Converged) lo++;
        while (!points[hi].Converged) hi--;
        int min = lo;
        for (int i = lo; i <= hi; i++) if (points[i].QMvar < points[min].QMvar) min = i;
        bool bracketed = min > lo && min < hi;
        double noseV = points[min].VPu, noseQ = points[min].QMvar;
        if (bracketed)
        {
            double qa = points[min - 1].QMvar, qb = points[min].QMvar, qc = points[min + 1].QMvar;
            double second = qa - 2.0 * qb + qc;
            if (second > 0.0)
            {
                noseV = points[min].VPu + stepPu * (qa - qc) / (2.0 * second);
                noseQ = qb - (qa - qc) * (qa - qc) / (8.0 * second);
            }
        }
        return new QvCurveResult(busId, "OK", v0, stepPu, points, downEnd, upEnd,
            points[min].QMvar, points[min].VPu, bracketed, noseV, noseQ, -noseQ, qBase);
    }
}
