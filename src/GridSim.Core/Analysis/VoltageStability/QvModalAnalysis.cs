using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.VoltageStability;

/// <summary>One bus's participation in a QV mode.</summary>
/// <param name="BusId">The bus.</param>
/// <param name="Participation">Its participation factor (the real part; see <see cref="QvMode.Participation"/>).</param>
public sealed record QvModeBus(int BusId, double Participation);

/// <summary>One mode of the reduced Jacobian.</summary>
/// <param name="Rank">1 for the smallest |eigenvalue|, 2 for the next, and so on.</param>
/// <param name="Eigenvalue">The eigenvalue (pu reactive power per pu voltage). Positive: voltage stable; negative:
/// unstable; near 0: close to the limit.</param>
/// <param name="Participation">The bus participation factors, aligned with <see cref="QvModalResult.PqBusIds"/>. They sum
/// to 1 (complex in general; real for a real mode with real eigenvectors).</param>
/// <param name="TopBuses">The buses with the largest |participation|, largest first (ties in bus order).</param>
public sealed record QvMode(int Rank, Complex Eigenvalue, IReadOnlyList<Complex> Participation, IReadOnlyList<QvModeBus> TopBuses);

/// <summary>The QV modal analysis of one solved state.</summary>
/// <param name="PqBusIds">The PQ buses of the solved state, in model order: the rows and columns of the reduced Jacobian.</param>
/// <param name="ReducedJacobian">J_R = J_QV - J_Qtheta J_Ptheta^-1 J_PV over <see cref="PqBusIds"/>.</param>
/// <param name="Modes">The requested modes, smallest |eigenvalue| first.</param>
/// <param name="TotalModes">How many modes J_R has (its order).</param>
public sealed record QvModalResult(IReadOnlyList<int> PqBusIds, double[,] ReducedJacobian, IReadOnlyList<QvMode> Modes, int TotalModes);

/// <summary>
/// RC5 WP25: QV modal analysis (Gao, Morison and Kundur, "Voltage stability evaluation using modal analysis", IEEE Trans.
/// PWRS 7(4), 1992; Kundur, Power System Stability and Control, section 14.3).
/// <list type="bullet">
/// <item>At a converged power-flow state, the polar Jacobian of the Newton engines (the same formulas, with dP/dV and dQ/dV
/// taken per unit of |V|, and a ZIP load's voltage derivative on the diagonal) is split into J_Ptheta (theta over every
/// non-slack bus), J_PV, J_Qtheta and J_QV (V over the PQ buses of the solved state: a PV bus pinned at a reactive limit
/// counts as PQ).</item>
/// <item>J_R = J_QV - J_Qtheta X with X = J_Ptheta^-1 J_PV from one dense LU of J_Ptheta (partial pivoting) solved column
/// by column.</item>
/// <item>J_R is not symmetric in general, so it goes to the general real eigen solver (<see cref="GeneralEigen.Decompose"/>,
/// the one <c>modal-full</c> uses), which returns right vectors xi and left vectors psi with psi^H xi = 1.</item>
/// <item>Bus participation P_k = eta_k xi_k with eta = psi^H (the left row vector), divided by their sum so the factors of
/// a mode sum to 1 to rounding.</item>
/// </list>
/// Scope: the standard power-flow Jacobian only. Generator reactive limits are as solved; there are no dynamic models and
/// no branch or generator participation. A model with HVDC links, FACTS devices, static var compensators or induction
/// machines is refused: the Newton engines solve those in an outer loop, and J_R built from the plain admittance matrix
/// would describe a different system.
/// </summary>
public static class QvModalAnalysis
{
    /// <summary>The default number of modes reported.</summary>
    public const int DefaultModes = 5;

    /// <summary>The default number of participating buses listed per mode.</summary>
    public const int DefaultTopBuses = 10;

    /// <summary>Analyse the converged state <paramref name="solved"/> of <paramref name="model"/>.</summary>
    /// <exception cref="InvalidOperationException">The model has coupled devices (HVDC, FACTS, SVC, induction machines), the
    /// state is not converged, J_Ptheta is singular, or J_R is defective (no biorthonormal eigenvectors, so no
    /// participation factors).</exception>
    public static QvModalResult Analyse(GridModel model, PowerFlowResult solved, int modes = DefaultModes, int topBuses = DefaultTopBuses)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(solved);
        if (modes < 1) throw new ArgumentOutOfRangeException(nameof(modes), modes, "At least one mode must be asked for.");
        if (topBuses < 1) throw new ArgumentOutOfRangeException(nameof(topBuses), topBuses, "At least one bus per mode must be asked for.");
        if (model.HasCoupledDevices)
            throw new InvalidOperationException(
                "QV modal analysis does not model HVDC links, FACTS devices, static var compensators or induction machines: the reduced Jacobian is built from the network alone, so it would describe a different system.");
        if (!solved.Converged)
            throw new InvalidOperationException("QV modal analysis needs a converged power flow; this state did not converge.");
        int n = model.BusCount;
        if (solved.Vm.Length != n || solved.BusIds.Count != n)
            throw new ArgumentException("The solved state does not belong to this model (its bus count differs).", nameof(solved));
        for (int i = 0; i < n; i++)
            if (solved.BusIds[i] != model.Buses[i].Id)
                throw new ArgumentException("The solved state does not belong to this model (its bus order differs).", nameof(solved));

        Func<int, int> indexOf = NewtonShell.ModelIndex(model);
        NewtonShell.BusSpec spec = NewtonShell.BuildBusSpec(model, n, indexOf, new PowerFlowOptions());
        IReadOnlyList<BusType> types = solved.SolvedBusTypes ?? spec.Type0;

        YBus y = YBus.Build(model);
        var g = new double[n, n];
        var b = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) { g[i, j] = y.Y[i, j].Real; b[i, j] = y.Y[i, j].Imaginary; }
        double[] vm = (double[])solved.Vm.Clone();
        var va = new double[n];
        for (int i = 0; i < n; i++) va[i] = solved.VaDeg[i] * Math.PI / 180.0;
        var p = new double[n];
        var q = new double[n];
        NewtonRaphsonPowerFlow.ComputeInjections(n, g, b, vm, va, p, q);

        var pvpq = new List<int>();
        var pq = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (types[i] != BusType.Slack) pvpq.Add(i);
            if (types[i] == BusType.Pq) pq.Add(i);
        }
        int na = pvpq.Count, nv = pq.Count;
        int[] pqIds = pq.Select(i => model.Buses[i].Id).ToArray();
        if (nv == 0) return new QvModalResult(pqIds, new double[0, 0], Array.Empty<QvMode>(), 0);

        var jac = new double[na + nv, na + nv];
        NewtonRaphsonPowerFlow.BuildJacobianInto(jac, g, b, vm, va, p, q, pvpq, pq, spec, model.BaseMva);

        double[,] jr = Reduce(jac, na, nv);
        IReadOnlyList<GeneralEigen.Eigenpair> pairs;
        try { pairs = GeneralEigen.Decompose(jr); }
        catch (GeneralEigen.DefectiveEigenproblemException ex)
        {
            throw new InvalidOperationException("The reduced Jacobian is defective, so its participation factors do not exist: " + ex.Message, ex);
        }

        // Smallest |lambda| first; LINQ's OrderBy is stable, so equal magnitudes keep the solver's (Re, Im) order.
        var chosen = pairs.Select((e, k) => (e, k)).OrderBy(t => Complex.Abs(t.e.Value)).Take(Math.Min(modes, pairs.Count)).ToList();
        var result = new List<QvMode>(chosen.Count);
        for (int r = 0; r < chosen.Count; r++)
        {
            GeneralEigen.Eigenpair e = chosen[r].e;
            var part = new Complex[nv];
            Complex sum = Complex.Zero;
            for (int k = 0; k < nv; k++) { part[k] = Complex.Conjugate(e.Left[k]) * e.Right[k]; sum += part[k]; }
            for (int k = 0; k < nv; k++) part[k] /= sum;
            QvModeBus[] top = Enumerable.Range(0, nv).OrderByDescending(k => Complex.Abs(part[k])).Take(Math.Min(topBuses, nv))
                .Select(k => new QvModeBus(pqIds[k], part[k].Real)).ToArray();
            result.Add(new QvMode(r + 1, e.Value, part, top));
        }
        return new QvModalResult(pqIds, jr, result, pairs.Count);
    }

    /// <summary>J_R = J_QV - J_Qtheta (J_Ptheta^-1 J_PV) from the full Newton Jacobian <paramref name="j"/> (the first
    /// <paramref name="na"/> rows are P equations and the first <paramref name="na"/> columns angles).</summary>
    internal static double[,] Reduce(double[,] j, int na, int nv)
    {
        var jr = new double[nv, nv];
        for (int r = 0; r < nv; r++)
            for (int c = 0; c < nv; c++) jr[r, c] = j[na + r, na + c];
        if (na == 0) return jr;

        // LU of J_Ptheta with partial pivoting (row swaps recorded), then X = J_Ptheta^-1 J_PV column by column.
        var lu = new double[na, na];
        for (int r = 0; r < na; r++)
            for (int c = 0; c < na; c++) lu[r, c] = j[r, c];
        var perm = new int[na];
        for (int i = 0; i < na; i++) perm[i] = i;
        double scale = 0;
        for (int r = 0; r < na; r++)
            for (int c = 0; c < na; c++) scale = Math.Max(scale, Math.Abs(lu[r, c]));
        for (int k = 0; k < na; k++)
        {
            int piv = k;
            for (int r = k + 1; r < na; r++) if (Math.Abs(lu[r, k]) > Math.Abs(lu[piv, k])) piv = r;
            if (!(Math.Abs(lu[piv, k]) > scale * 1e-14))
                throw new InvalidOperationException("J_Ptheta is singular at this state, so the reduced Jacobian cannot be formed.");
            if (piv != k)
            {
                for (int c = 0; c < na; c++) (lu[k, c], lu[piv, c]) = (lu[piv, c], lu[k, c]);
                (perm[k], perm[piv]) = (perm[piv], perm[k]);
            }
            for (int r = k + 1; r < na; r++)
            {
                double f = lu[r, k] / lu[k, k];
                lu[r, k] = f;
                for (int c = k + 1; c < na; c++) lu[r, c] -= f * lu[k, c];
            }
        }
        var x = new double[na];
        for (int col = 0; col < nv; col++)
        {
            for (int r = 0; r < na; r++) x[r] = j[perm[r], na + col];
            for (int r = 1; r < na; r++)
                for (int c = 0; c < r; c++) x[r] -= lu[r, c] * x[c];
            for (int r = na - 1; r >= 0; r--)
            {
                for (int c = r + 1; c < na; c++) x[r] -= lu[r, c] * x[c];
                x[r] /= lu[r, r];
            }
            for (int r = 0; r < nv; r++)
            {
                double s = 0;
                for (int k = 0; k < na; k++) s += j[na + r, k] * x[k];
                jr[r, col] -= s;
            }
        }
        return jr;
    }
}
