using System.Numerics;

namespace GridSim.Core.Harmonics;

/// <summary>Per-bus harmonic distortion indices from a penetration study.</summary>
/// <param name="BusIds">Bus ids, aligned with the arrays below.</param>
/// <param name="ThdVoltagePct">Total harmonic distortion of voltage at each bus (%).</param>
/// <param name="WorstBusId">The bus with the highest voltage THD.</param>
/// <param name="WorstThdPct">That bus's voltage THD (%).</param>
public sealed record HarmonicIndicesResult(
    IReadOnlyList<int> BusIds,
    double[] ThdVoltagePct,
    int WorstBusId,
    double WorstThdPct);

/// <summary>
/// Harmonic distortion indices - the numbers a study is judged by. Voltage THD/IHD come from a
/// <see cref="HarmonicPenetrationResult"/>; the transformer K-factor from a current spectrum; and the
/// "damping sharpness" from an impedance scan is the on-mission metric - a sharp, tall resonance is what a
/// grid shedding synchronous (harmonic-damping) machines develops.
/// </summary>
public static class HarmonicIndices
{
    /// <summary>
    /// Voltage THD per bus from a penetration result. THD_V = sqrt(sum_{h&gt;1} |V_h|^2) / |V_1|, expressed
    /// as a percent. The fundamental voltage magnitude per bus defaults to 1.0 pu (nominal) when not supplied.
    /// </summary>
    public static HarmonicIndicesResult FromPenetration(HarmonicPenetrationResult res, double[]? fundamentalVm = null)
    {
        int n = res.BusIds.Count;
        // GR-588: a given fundamental array must have one finite, positive voltage per bus of the result; a short array,
        // or an entry that is NaN, 0 or negative, used to be replaced by 1.0 pu for that bus, silently misstating its THD.
        // (Null still means "every fundamental is 1.0 pu".)
        if (fundamentalVm is not null)
        {
            if (fundamentalVm.Length != n)
                throw new ArgumentException($"The fundamental voltage array has {fundamentalVm.Length} entries; the result has {n} buses.", nameof(fundamentalVm));
            for (int i = 0; i < n; i++)
                if (!(fundamentalVm[i] > 0) || !double.IsFinite(fundamentalVm[i]))
                    throw new ArgumentException($"The fundamental voltage of bus {res.BusIds[i]} is {fundamentalVm[i]}; it must be a finite number above 0.", nameof(fundamentalVm));
        }
        var thd = new double[n];
        int worstBus = res.BusIds.Count > 0 ? res.BusIds[0] : 0;
        double worst = 0;

        for (int bi = 0; bi < n; bi++)
        {
            double sumSq = 0;
            for (int oi = 0; oi < res.Orders.Count; oi++)
            {
                if (res.Orders[oi] == 1) continue;         // exclude the fundamental
                Complex v = res.Voltage[oi][bi];
                sumSq += v.Real * v.Real + v.Imaginary * v.Imaginary;
            }
            double v1 = fundamentalVm is not null && bi < fundamentalVm.Length && fundamentalVm[bi] > 0
                ? fundamentalVm[bi] : 1.0;
            thd[bi] = Math.Sqrt(sumSq) / v1 * 100.0;
            if (thd[bi] > worst) { worst = thd[bi]; worstBus = res.BusIds[bi]; }
        }
        return new HarmonicIndicesResult(res.BusIds, thd, worstBus, worst);
    }

    /// <summary>
    /// The solved fundamental voltage magnitude of every bus of a penetration result, aligned with
    /// <see cref="HarmonicPenetrationResult.BusIds"/> - the denominator |V_1| of THD and IHD. This is the ONE function
    /// every front end uses to normalise its harmonic indices (docs-programme finding: the CLI divided by a nominal
    /// 1.0 pu while the web HARMONICS tab divided by the solved fundamental, so the two printed different THD for the
    /// same network and sources).
    /// <para>The values are matched by bus id, not by position, so a power-flow result whose bus order differs from the
    /// model's still lines up. Fails loud (<see cref="InvalidOperationException"/>) when the fundamental did not
    /// converge, when a bus of the study is missing from it, or when a magnitude is not finite and positive: a
    /// distortion ratio over a meaningless denominator must never be reported.</para>
    /// </summary>
    public static double[] SolvedFundamentalVm(HarmonicPenetrationResult res, PowerFlow.PowerFlowResult fundamental)
    {
        ArgumentNullException.ThrowIfNull(res);
        ArgumentNullException.ThrowIfNull(fundamental);
        if (!fundamental.Converged)
            throw new InvalidOperationException(
                $"the fundamental power flow did not converge ({fundamental.Status}); harmonic distortion is a ratio to the " +
                "solved fundamental voltage, so it cannot be computed.");
        var byId = new Dictionary<int, double>(fundamental.BusIds.Count);
        for (int i = 0; i < fundamental.BusIds.Count; i++) byId[fundamental.BusIds[i]] = fundamental.Vm[i];
        var vm = new double[res.BusIds.Count];
        for (int bi = 0; bi < res.BusIds.Count; bi++)
        {
            int id = res.BusIds[bi];
            if (!byId.TryGetValue(id, out double v))
                throw new InvalidOperationException($"bus {id} of the harmonic study is not in the fundamental power-flow result.");
            if (!double.IsFinite(v) || v <= 0)
                throw new InvalidOperationException($"bus {id} has a solved fundamental voltage of {v} pu; THD needs a finite, positive |V_1|.");
            vm[bi] = v;
        }
        return vm;
    }

    /// <summary>Individual harmonic distortion of voltage at a bus and order (%): |V_h| / |V_1| * 100.</summary>
    // GR-588: the fundamental is a finite number above 0 (a 0, negative or NaN value used to become 1.0 pu silently).
    private static double RequirePositiveFundamental(double v) => v > 0 && double.IsFinite(v) ? v
        : throw new ArgumentOutOfRangeException(nameof(v), v, "The fundamental voltage must be a finite number above 0.");

    public static double IndividualDistortionPct(HarmonicPenetrationResult res, int busIndex, int order, double fundamentalVm = 1.0)
    {
        for (int oi = 0; oi < res.Orders.Count; oi++)
            if (res.Orders[oi] == order)
                return res.Voltage[oi][busIndex].Magnitude / RequirePositiveFundamental(fundamentalVm) * 100.0;
        return 0.0;
    }

    /// <summary>
    /// The UL transformer K-factor of a current spectrum: <c>K = sum_h (I_h^2 * h^2) / sum_h I_h^2</c>
    /// (including the fundamental). It quantifies the extra eddy-current heating harmonic load current causes
    /// - the derating a transformer needs. A pure fundamental gives K=1; a 6-pulse load gives ~8-9.
    /// </summary>
    public static double KFactor(HarmonicSource source)
    {
        double num = source.FundamentalCurrentPu * source.FundamentalCurrentPu * 1.0; // h=1 term (I_1^2 * 1^2)
        double den = source.FundamentalCurrentPu * source.FundamentalCurrentPu;
        foreach (HarmonicComponent c in source.Spectrum)
        {
            if (c.Order == 1) continue;
            double ih = (c.MagnitudePercent / 100.0) * source.FundamentalCurrentPu;
            double ih2 = ih * ih;
            num += ih2 * c.Order * c.Order;
            den += ih2;
        }
        return den > 0 ? num / den : 1.0;
    }

    /// <summary>
    /// Resonance "sharpness" from an impedance scan: the peak driving-point |Z| relative to the median across
    /// the scanned band. A high value is a tall, lightly-damped resonance - the harmonic-damping-loss
    /// signature of a low-inertia, inverter-dominated grid. (1 = flat / well-damped.)
    /// </summary>
    public static double DampingSharpness(ImpedanceScanResult scan)
    {
        int m = scan.Z.Length;
        // An empty scan has no resonance and no flatness either: refuse rather than read 1 ("flat / well-damped").
        if (m == 0) throw new ArgumentException("DampingSharpness: the impedance scan holds no order.", nameof(scan));
        var mags = new double[m];
        for (int i = 0; i < m; i++) mags[i] = scan.Z[i].Magnitude;
        var sorted = (double[])mags.Clone();
        Array.Sort(sorted);
        double median = m % 2 == 1 ? sorted[m / 2] : 0.5 * (sorted[m / 2 - 1] + sorted[m / 2]);
        return median > 0 ? scan.PeakMagnitude / median : double.PositiveInfinity;
    }
}
