using System;
using System.Collections.Generic;
using System.Linq;

namespace GridSim.Core.Protection.ShortCircuit.Dc;

/// <summary>Options of a DC study. <see cref="StepS"/> and <see cref="EndS"/> null = chosen from the network's time constants.
/// <see cref="Times"/> are the instants at which the fault current is also reported.</summary>
public sealed record DcOptions(IReadOnlyList<double> Times, double? StepS = null, double? EndS = null, double? BreakerMakingA = null);

public enum DcStatus { Ok, Incomplete, Failed }

/// <summary>One source's current into the network: its peak (A), the time of that peak, and its value at each requested time.</summary>
public sealed record DcSourceResult(string Name, string Kind, double PeakA, double PeakTimeS, IReadOnlyList<double> AtTimesA, double EndA);

public sealed record DcResult(
    DcStatus Status, IReadOnlyList<string> Reasons, double StepS, double EndS, long StepCount, double PrefaultFaultNodeV,
    double PeakA, double PeakTimeS, double EndA, IReadOnlyList<double> Times, IReadOnlyList<double> AtTimesA,
    IReadOnlyList<DcSourceResult> Sources, bool? MakingOk, double MakingMarginA, IReadOnlyList<string> Notes, IReadOnlyList<string> Awaiting)
{
    public string Verdict => Status switch { DcStatus.Ok => "OK", DcStatus.Incomplete => "INCOMPLETE", _ => "FAILED" };
}

/// <summary>
/// A DC auxiliary-installation short circuit in the manner of IEC 61660-1: batteries, rectifiers, capacitors and DC motors feeding a fault
/// through a resistive-inductive network (RC4 R4-20c). The standard's own equivalent-circuit equations and coefficients are AWAITING STANDARD
/// TEXT; what is built here is the underlying physics solved directly, so that every source contributes its true time function:
/// <list type="bullet">
/// <item>battery or rectifier: a constant EMF behind R and L, <c>i = (E/R)(1 - e^(-R t/L))</c> for one source;</item>
/// <item>capacitor: a charged capacitor discharging through R and L (<c>i = (U/L)(e^(s1 t) - e^(s2 t))/(s1 - s2)</c>, s the roots of
/// <c>L C s^2 + R C s + 1</c>);</item>
/// <item>DC motor: back EMF <c>k w</c> with <c>J dw/dt = -k i</c> behind R and L (the same second-order form with <c>L J s^2 + R J s + k^2</c>).</item>
/// </list>
/// Method: the network is a set of series R-L elements; modified nodal analysis with the trapezoidal rule (one backward-Euler start step so that
/// the voltage jump at fault inception does not enter the first step), a constant matrix factored once with partial-pivot LU, a fixed step.
/// Order: validate, prefault (DC, fault open), factor, step, sum, report. Deterministic (no transcendental function is used).
/// </summary>
public static class Iec61660
{
    public static readonly IReadOnlyList<string> Awaiting = new[]
    {
        "AWAITING STANDARD TEXT: IEC 61660-1 clause numbers and edition (text is paid; not read)",
        "AWAITING STANDARD TEXT: the standard's equivalent-circuit equations and coefficients for each source (rise and decay time constants, peak and steady-state factors); this study solves the circuit directly instead",
        "AWAITING STANDARD TEXT: rectifier contribution (commutation, AC-side decay, current limiting); a rectifier is held as a constant EMF behind R and L here",
        "AWAITING STANDARD TEXT: motor contribution rules (rated quantities, when a motor counts, flux decay); a motor is armature R and L with the back EMF decaying through its inertia, load torque neglected",
        "AWAITING STANDARD TEXT: conductor temperature and tolerance corrections to R and the minimum and maximum case rules",
        "AWAITING STANDARD TEXT: the standard's worked examples (none reproduced; no acceptance claimed)",
    };

    private const long MaxSteps = 5_000_000;

    public static DcResult Compute(DcNetwork net, DcOptions options)
    {
        var reasons = new List<string>();
        var notes = new List<string>();
        double[] times = options.Times.ToArray();
        foreach (double t in times) if (!(t >= 0.0) || !double.IsFinite(t)) reasons.Add($"time {t} is not a finite number of seconds >= 0");
        if (options.StepS is double sv && (!(sv > 0.0) || !double.IsFinite(sv))) reasons.Add("step must be finite and above 0 s");
        if (options.EndS is double ev && (!(ev > 0.0) || !double.IsFinite(ev))) reasons.Add("end time must be finite and above 0 s");
        if (reasons.Count > 0) return Fail(DcStatus.Failed, reasons, times);

        // Node numbering (reference excluded), in order of first appearance.
        var nodeIx = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string n) { if (n != DcNetwork.Reference && !nodeIx.ContainsKey(n)) nodeIx[n] = nodeIx.Count; }
        foreach (DcBranch b in net.Branches) { Add(b.From); Add(b.To); }
        foreach (DcSource s in net.Sources) Add(s.Node);
        Add(net.Fault.Node);
        int nN = nodeIx.Count, nB = net.Branches.Count, nS = net.Sources.Count;
        int n = nN + nB + nS + 1;
        int iB = nN, iS = nN + nB, iF = nN + nB + nS;

        // Time scales.
        double tauMin = double.PositiveInfinity, sumR = net.Fault.ROhm, sumL = 0.0;
        foreach (DcBranch b in net.Branches) { sumR += b.ROhm; sumL += b.LHenry; if (b.LHenry > 0 && b.ROhm > 0) tauMin = Math.Min(tauMin, b.LHenry / b.ROhm); }
        foreach (DcSource s in net.Sources)
        {
            sumR += s.ROhm; sumL += s.LHenry;
            if (s.LHenry > 0 && s.ROhm > 0) tauMin = Math.Min(tauMin, s.LHenry / s.ROhm);
            if (s.Kind == DcSourceKind.Capacitor) tauMin = Math.Min(tauMin, Math.Sqrt(s.LHenry * s.CFarad));
            if (s.Kind == DcSourceKind.Motor) tauMin = Math.Min(tauMin, Math.Sqrt(s.LHenry * s.JKgM2) / s.KVsPerRad);
        }
        double tauMax = 0.0;
        if (sumR > 0.0)
        {
            tauMax = sumL / sumR;
            foreach (DcSource s in net.Sources)
            {
                if (s.Kind == DcSourceKind.Capacitor) tauMax = Math.Max(tauMax, sumR * s.CFarad);
                if (s.Kind == DcSourceKind.Motor) tauMax = Math.Max(tauMax, sumR * s.JKgM2 / (s.KVsPerRad * s.KVsPerRad));
            }
        }
        double h = options.StepS ?? (double.IsFinite(tauMin) ? tauMin / 400.0 : double.NaN);
        double end = options.EndS ?? (tauMax > 0.0 ? 8.0 * tauMax : double.NaN);
        if (!(h > 0.0) || !double.IsFinite(h)) reasons.Add("the step cannot be chosen from the network (no inductance with resistance); give a step");
        if (!(end > 0.0) || !double.IsFinite(end)) reasons.Add("the end time cannot be chosen from the network (no resistance); give an end time");
        if (reasons.Count > 0) return Fail(DcStatus.Incomplete, reasons, times);
        foreach (double t in times) if (t > end) { notes.Add($"requested time {t} s is beyond the end time {end} s and is reported at the end"); }
        long steps = (long)Math.Ceiling(end / h);
        if (steps > MaxSteps) { reasons.Add($"{steps} steps are needed (limit {MaxSteps}); give a larger step or a shorter end time"); return Fail(DcStatus.Incomplete, reasons, times); }

        // ---- prefault: DC steady state with the fault open (inductors short, capacitors charged, motors at their EMF) ----
        double[] x0;
        try
        {
            double[,] dc = Assemble(net, nodeIx, nN, nB, nS, 0.0, h, dcMode: true);
            double[] rhs0 = new double[n];
            for (int s = 0; s < nS; s++) rhs0[iS + s] = EmfAtStart(net.Sources[s]);
            x0 = Lu.Factor(dc).Solve(rhs0);
        }
        catch (InvalidOperationException ex) { return Fail(DcStatus.Incomplete, new[] { "prefault state is singular: " + ex.Message }, times); }
        double prefaultV = nodeIx.TryGetValue(net.Fault.Node, out int fi) ? x0[fi] : 0.0;
        if (Math.Abs(prefaultV) < 1e-12) notes.Add("the fault node is at 0 V before the fault: nothing drives a short circuit current there");

        Lu lu1, luT;
        try
        {
            lu1 = Lu.Factor(Assemble(net, nodeIx, nN, nB, nS, 1.0, h, false));
            luT = Lu.Factor(Assemble(net, nodeIx, nN, nB, nS, 0.5, h, false));
        }
        catch (InvalidOperationException ex) { return Fail(DcStatus.Failed, new[] { "network matrix is singular: " + ex.Message }, times); }

        // State: currents (branches, sources), previous node voltages, capacitor voltages, motor speeds.
        double[] x = (double[])x0.Clone();
        x[iF] = 0.0;
        double[] cap = new double[nS];     // capacitor voltage or motor speed
        for (int s = 0; s < nS; s++)
        {
            DcSource src = net.Sources[s];
            cap[s] = src.Kind == DcSourceKind.Capacitor ? src.EVolt : src.Kind == DcSourceKind.Motor ? src.EVolt / src.KVsPerRad : 0.0;
        }
        var atTimes = new double[times.Length];
        var srcAt = new double[nS][];
        for (int s = 0; s < nS; s++) srcAt[s] = new double[times.Length];
        var srcPeak = new double[nS]; var srcPeakT = new double[nS];
        double peak = 0.0, peakT = 0.0;
        double[] prevSrc = new double[nS];
        for (int s = 0; s < nS; s++) prevSrc[s] = x[iS + s];
        int nextT = 0;
        int[] order = Enumerable.Range(0, times.Length).OrderBy(k => times[k]).ToArray();
        // t = 0 samples (fault current 0 at inception; sources at their prefault current).
        while (nextT < order.Length && times[order[nextT]] <= 0.0) { atTimes[order[nextT]] = 0.0; for (int s = 0; s < nS; s++) srcAt[s][order[nextT]] = x[iS + s]; nextT++; }
        double[] rhs = new double[n];
        double iPrev = 0.0;
        for (long step = 1; step <= steps; step++)
        {
            double theta = step == 1 ? 1.0 : 0.5;
            Lu lu = step == 1 ? lu1 : luT;
            Array.Clear(rhs);
            for (int b = 0; b < nB; b++)
            {
                DcBranch br = net.Branches[b];
                if (br.LHenry > 0.0)
                {
                    double vd = Volt(x, nodeIx, br.From) - Volt(x, nodeIx, br.To);
                    rhs[iB + b] = br.LHenry / h * x[iB + b] - (1.0 - theta) * br.ROhm * x[iB + b] + (1.0 - theta) * vd;
                }
            }
            for (int s = 0; s < nS; s++)
            {
                DcSource src = net.Sources[s];
                double i0 = x[iS + s], v0 = Volt(x, nodeIx, src.Node);
                if (!(src.LHenry > 0.0)) { rhs[iS + s] = src.EVolt; continue; }
                double e0 = src.Kind switch { DcSourceKind.Capacitor => cap[s], DcSourceKind.Motor => src.KVsPerRad * cap[s], _ => src.EVolt };
                double a = src.Kind switch
                {
                    DcSourceKind.Capacitor => cap[s] - h / src.CFarad * (1.0 - theta) * i0,
                    DcSourceKind.Motor => src.KVsPerRad * cap[s] - src.KVsPerRad * src.KVsPerRad * h / src.JKgM2 * (1.0 - theta) * i0,
                    _ => src.EVolt,
                };
                rhs[iS + s] = src.LHenry / h * i0 - (1.0 - theta) * src.ROhm * i0 - (1.0 - theta) * v0 + (1.0 - theta) * e0 + theta * a;
            }
            double[] xn = lu.Solve(rhs);
            // Capacitor and motor states.
            for (int s = 0; s < nS; s++)
            {
                DcSource src = net.Sources[s];
                double i0 = x[iS + s], i1 = xn[iS + s];
                if (src.Kind == DcSourceKind.Capacitor) cap[s] -= h / src.CFarad * (theta * i1 + (1.0 - theta) * i0);
                else if (src.Kind == DcSourceKind.Motor) cap[s] -= src.KVsPerRad * h / src.JKgM2 * (theta * i1 + (1.0 - theta) * i0);
            }
            x = xn;
            double t1 = step * h, iF1 = x[iF];
            // Peak: the sampled maximum (the step is a small fraction of the shortest time constant).
            if (Math.Abs(iF1) > peak) { peak = Math.Abs(iF1); peakT = t1; }
            for (int s = 0; s < nS; s++)
                if (Math.Abs(x[iS + s]) > srcPeak[s]) { srcPeak[s] = Math.Abs(x[iS + s]); srcPeakT[s] = t1; }
            while (nextT < order.Length && times[order[nextT]] <= t1)
            {
                double tt = times[order[nextT]], f = (tt - (t1 - h)) / h;
                f = Math.Min(1.0, Math.Max(0.0, f));
                atTimes[order[nextT]] = iPrev + f * (iF1 - iPrev);
                for (int s = 0; s < nS; s++) srcAt[s][order[nextT]] = prevSrc[s] + f * (x[iS + s] - prevSrc[s]);
                nextT++;
            }
            iPrev = iF1;
            for (int s = 0; s < nS; s++) prevSrc[s] = x[iS + s];
        }
        while (nextT < order.Length) { atTimes[order[nextT]] = iPrev; for (int s = 0; s < nS; s++) srcAt[s][order[nextT]] = prevSrc[s]; nextT++; }

        var results = new List<DcSourceResult>(nS);
        for (int s = 0; s < nS; s++)
            results.Add(new DcSourceResult(net.Sources[s].Name, net.Sources[s].Kind.ToString().ToLowerInvariant(), srcPeak[s], srcPeakT[s], srcAt[s], x[iS + s]));
        bool? making = null; double margin = double.NaN;
        if (options.BreakerMakingA is double bm) { making = peak <= bm; margin = bm - peak; }
        return new DcResult(DcStatus.Ok, Array.Empty<string>(), h, steps * h, steps, prefaultV, peak, peakT, x[iF], times, atTimes, results, making, margin, notes, Awaiting);
    }

    private static double EmfAtStart(DcSource s) => s.EVolt;

    private static double Volt(double[] x, Dictionary<string, int> ix, string node) => node == DcNetwork.Reference ? 0.0 : x[ix[node]];

    /// <summary>The constant system matrix for weight theta (1 = backward Euler, 0.5 = trapezoidal); <paramref name="dcMode"/> makes every
    /// inductor a short, the capacitor and motor EMFs constant, and the fault open.</summary>
    private static double[,] Assemble(DcNetwork net, Dictionary<string, int> ix, int nN, int nB, int nS, double theta, double h, bool dcMode)
    {
        int n = nN + nB + nS + 1, iB = nN, iS = nN + nB, iF = nN + nB + nS;
        var m = new double[n, n];
        for (int b = 0; b < nB; b++)
        {
            DcBranch br = net.Branches[b];
            int row = iB + b;
            bool alg = dcMode || !(br.LHenry > 0.0);
            // KCL: the branch current leaves From and enters To.
            if (br.From != DcNetwork.Reference) m[ix[br.From], row] -= 1.0;
            if (br.To != DcNetwork.Reference) m[ix[br.To], row] += 1.0;
            double th = alg ? 1.0 : theta;
            m[row, row] = alg ? br.ROhm : br.LHenry / h + th * br.ROhm;
            if (br.From != DcNetwork.Reference) m[row, ix[br.From]] -= th;
            if (br.To != DcNetwork.Reference) m[row, ix[br.To]] += th;
        }
        for (int s = 0; s < nS; s++)
        {
            DcSource src = net.Sources[s];
            int row = iS + s, node = ix[src.Node];
            m[node, row] += 1.0;   // the source current enters its node
            bool alg = dcMode || !(src.LHenry > 0.0);
            if (alg) { m[row, row] = src.ROhm; m[row, node] = 1.0; continue; }
            double b = src.Kind switch
            {
                DcSourceKind.Capacitor => -h / src.CFarad * theta,
                DcSourceKind.Motor => -src.KVsPerRad * src.KVsPerRad * h / src.JKgM2 * theta,
                _ => 0.0,
            };
            m[row, row] = src.LHenry / h + theta * src.ROhm - theta * b;
            m[row, node] = theta;
        }
        int fn = ix[net.Fault.Node];
        m[fn, iF] -= 1.0;   // the fault current leaves its node to the return
        if (dcMode) m[iF, iF] = 1.0;                           // i_fault = 0
        else { m[iF, iF] = net.Fault.ROhm; m[iF, fn] = -1.0; } // R i - v = 0
        return m;
    }

    private static DcResult Fail(DcStatus st, IReadOnlyList<string> reasons, double[] times) =>
        new(st, reasons, double.NaN, double.NaN, 0, double.NaN, double.NaN, double.NaN, double.NaN, times, times.Select(_ => double.NaN).ToArray(),
            Array.Empty<DcSourceResult>(), null, double.NaN, Array.Empty<string>(), Awaiting);

    /// <summary>Dense LU with partial pivoting (small matrices; deterministic).</summary>
    private sealed class Lu
    {
        private readonly double[,] _a; private readonly int[] _p; private readonly int _n;
        private Lu(double[,] a, int[] p) { _a = a; _p = p; _n = p.Length; }

        public static Lu Factor(double[,] src)
        {
            int n = src.GetLength(0);
            var a = (double[,])src.Clone();
            var p = new int[n];
            for (int i = 0; i < n; i++) p[i] = i;
            double scale = 0.0;
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) scale = Math.Max(scale, Math.Abs(a[i, j]));
            for (int k = 0; k < n; k++)
            {
                int piv = k; double best = Math.Abs(a[k, k]);
                for (int i = k + 1; i < n; i++) if (Math.Abs(a[i, k]) > best) { best = Math.Abs(a[i, k]); piv = i; }
                if (!(best > 1e-14 * Math.Max(scale, 1e-300))) throw new InvalidOperationException("matrix is singular (check for a node with no path to a source, or a zero-resistance loop)");
                if (piv != k)
                {
                    for (int j = 0; j < n; j++) (a[k, j], a[piv, j]) = (a[piv, j], a[k, j]);
                    (p[k], p[piv]) = (p[piv], p[k]);
                }
                for (int i = k + 1; i < n; i++)
                {
                    double f = a[i, k] / a[k, k];
                    a[i, k] = f;
                    if (f != 0.0) for (int j = k + 1; j < n; j++) a[i, j] -= f * a[k, j];
                }
            }
            return new Lu(a, p);
        }

        public double[] Solve(double[] b)
        {
            var y = new double[_n];
            for (int i = 0; i < _n; i++)
            {
                double s = b[_p[i]];
                for (int j = 0; j < i; j++) s -= _a[i, j] * y[j];
                y[i] = s;
            }
            for (int i = _n - 1; i >= 0; i--)
            {
                double s = y[i];
                for (int j = i + 1; j < _n; j++) s -= _a[i, j] * y[j];
                y[i] = s / _a[i, i];
            }
            return y;
        }
    }
}
