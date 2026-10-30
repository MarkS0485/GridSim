using GridSim.Core.Model;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// An opt-in recorder that captures <b>everything a power-flow solve computes</b> - the admittance
/// matrix, the per-unit bus specification, every Newton iteration's mismatch/correction vectors (and
/// the Jacobian for small systems), the Q-limit switching, and the converged state. Attach one to
/// <see cref="PowerFlow.PowerFlowOptions.Trace"/> and the solver fills it as it runs; leave it
/// <c>null</c> (the default) and there is zero overhead. Serialised by
/// <see cref="GridSim.Core.IO.SolveExport"/>.
///
/// <para>The recorder holds raw numbers; interpretation (bus ids, P vs Q rows) is reconstructed by
/// the exporter from the captured index maps. Solvers pass only primitives here, so this type never
/// leaks solver internals.</para>
/// </summary>
public sealed class SolveTrace
{
    // ---- configuration ----
    /// <summary>Capture the full Jacobian each iteration (dense solver only), for systems no larger
    /// than <see cref="JacobianMaxN"/>. A big Jacobian is O(n^2) and would dwarf the file.</summary>
    public bool CaptureJacobian { get; init; }

    /// <summary>Bus-count ceiling for Jacobian capture unless <see cref="CaptureJacobian"/> forces it.</summary>
    public int JacobianMaxN { get; init; } = 300;

    // ---- captured header ----
    public string SolverName { get; private set; } = "";
    public int N { get; private set; }
    public double BaseMva { get; private set; }
    /// <summary>Set when the Jacobian was omitted purely because the system exceeded the size gate.</summary>
    public bool JacobianOmittedForSize { get; private set; }

    // ---- captured data (read by the exporter) ----
    public List<YBusEntry> YBus { get; } = new();
    public BusSpecRow[] Spec { get; private set; } = Array.Empty<BusSpecRow>();
    public List<SolvePass> Passes { get; } = new();
    public List<QLimitEvent> QLimitEvents { get; } = new();

    // ---- recorder API (called by solvers with primitives only) ----

    public void Header(string solver, int n, double baseMva)
    {
        SolverName = solver; N = n; BaseMva = baseMva;
    }

    /// <summary>Record one non-zero admittance entry (row i, col j: conductance g, susceptance b).</summary>
    public void AddYBus(int i, int j, double g, double b) => YBus.Add(new YBusEntry(i, j, g, b));

    /// <summary>Capture the bus specification and the start point (before any iteration). Units, the same on every
    /// engine (D-DOC-068): <paramref name="pspec"/> and <paramref name="qspec"/> per unit on the system base;
    /// <paramref name="qd"/>, <paramref name="qmin"/> and <paramref name="qmax"/> MVAr; <paramref name="vm0"/> and
    /// <paramref name="vg"/> per unit; <paramref name="va0Rad"/> radians (recorded as degrees).</summary>
    public void CaptureSpec(
        IReadOnlyList<int> ids, BusType[] type0, double[] vm0, double[] va0Rad,
        double[] pspec, double[] qspec, double[] qd, double[] vg, double[] qmin, double[] qmax)
    {
        var rows = new BusSpecRow[type0.Length];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BusSpecRow(
                Index: i, Id: ids[i], Type: type0[i].ToString(),
                VmInit: vm0[i], VaInitDeg: va0Rad[i] * 180.0 / Math.PI,
                Pspec: pspec[i], Qspec: qspec[i], Qd: qd[i], Vg: vg[i], Qmin: qmin[i], Qmax: qmax[i]);
        Spec = rows;
    }

    /// <summary>Begin a Q-limit outer pass; iterations recorded afterwards attach to it.</summary>
    public void BeginPass(int pass, BusType[] effType, IReadOnlyList<int> pvpq, IReadOnlyList<int> pq)
    {
        Passes.Add(new SolvePass
        {
            Pass = pass,
            EffectiveTypes = effType.Select(t => t.ToString()).ToArray(),
            PvPqIndices = pvpq.ToArray(),
            PqIndices = pq.ToArray(),
        });
    }

    /// <summary>Record one Newton iteration. <paramref name="mismatch"/> and <paramref name="correction"/>
    /// are ordered [P equations over PV+PQ, then Q equations over PQ]. Jacobian may be null.</summary>
    public void CaptureIteration(int iter, double maxMismatch, double[] mismatch, double[] correction, double[,]? jacobian)
    {
        double[][]? jac = null;
        if (jacobian is not null)
        {
            int r = jacobian.GetLength(0), c = jacobian.GetLength(1);
            jac = new double[r][];
            for (int a = 0; a < r; a++) { jac[a] = new double[c]; for (int b = 0; b < c; b++) jac[a][b] = jacobian[a, b]; }
        }
        (Passes.Count > 0 ? Passes[^1] : FallbackPass()).Iterations.Add(
            new IterationTrace(iter, maxMismatch, (double[])mismatch.Clone(), (double[])correction.Clone(), jac));
    }

    /// <summary>Note that a Jacobian was skipped only because the system was larger than the size gate.</summary>
    public void NoteJacobianOmittedForSize() => JacobianOmittedForSize = true;

    public void AddQLimitEvent(int busIndex, int busId, string pinnedAt, int pass)
        => QLimitEvents.Add(new QLimitEvent(busIndex, busId, pinnedAt, pass));

    private SolvePass FallbackPass()
    {
        var p = new SolvePass { Pass = 0, EffectiveTypes = Array.Empty<string>(), PvPqIndices = Array.Empty<int>(), PqIndices = Array.Empty<int>() };
        Passes.Add(p);
        return p;
    }
}

/// <summary>One non-zero admittance-matrix entry.</summary>
public readonly record struct YBusEntry(int I, int J, double G, double B);

/// <summary>The per-unit specification and start point of one bus.</summary>
public readonly record struct BusSpecRow(
    int Index, int Id, string Type,
    double VmInit, double VaInitDeg,
    double Pspec, double Qspec, double Qd, double Vg, double Qmin, double Qmax);

/// <summary>One reactive-limit outer pass: the effective bus types and the P/Q equation ordering it solved.</summary>
public sealed class SolvePass
{
    public int Pass { get; init; }
    public string[] EffectiveTypes { get; init; } = Array.Empty<string>();
    public int[] PvPqIndices { get; init; } = Array.Empty<int>();   // bus indices with a P equation
    public int[] PqIndices { get; init; } = Array.Empty<int>();     // bus indices with a Q equation
    public List<IterationTrace> Iterations { get; } = new();
}

/// <summary>One Newton iteration: the mismatch it started from, the correction it applied, and (optionally) the Jacobian.</summary>
public readonly record struct IterationTrace(
    int Iteration, double MaxMismatch, double[] Mismatch, double[] Correction, double[][]? Jacobian);

/// <summary>A PV->PQ (or revert) switch during the reactive-limit outer loop.</summary>
public readonly record struct QLimitEvent(int BusIndex, int BusId, string PinnedAt, int Pass);
