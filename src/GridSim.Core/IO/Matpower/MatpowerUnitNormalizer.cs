using GridSim.Core.Model;

namespace GridSim.Core.IO.Matpower;

/// <summary>
/// Detects and repairs MATPOWER distribution cases that ship in <b>RAW engineering units</b>
/// rather than the per-unit / MW convention every GridSim solver assumes.
/// </summary>
/// <remarks>
/// <para>
/// A handful of well-known radial distribution feeders (<c>case33bw</c>, <c>case69</c>,
/// <c>case85</c> …) declare a <c>baseMVA</c> and <c>baseKV</c> like any other case but then give
/// their data in physical units: branch series impedance in <b>ohms</b> (not per-unit) and bus
/// demand in <b>kW / kVAr</b> (not MW / MVAr). The standard <see cref="MatpowerParser"/> reads the
/// numbers verbatim (correctly — it is not its job to guess units), so parsed as-is these cases are
/// off by a factor of ~1000 on load and by <c>Zbase</c> on impedance and the power flow
/// voltage-collapses (a 3.715&nbsp;MW feeder is read as 3715&nbsp;MW ≈ 371&nbsp;p.u.).
/// </para>
/// <para>
/// The fix is the textbook per-unit reduction, applied per bus voltage level:
/// <code>
///   Zbase = BaseKv² / BaseMva            (ohms)
///   r_pu  = r_ohm / Zbase,  x_pu = x_ohm / Zbase
///   P_mw  = P_kw  / 1000,   likewise Qd, Gs, Bs
/// </code>
/// For <c>case33bw</c> (BaseMva = 10, BaseKv = 12.66) this gives Zbase = 16.03&nbsp;Ω, turning an
/// r of 0.0922&nbsp;Ω into 0.00575&nbsp;p.u. and a 100&nbsp;kW load into 0.1&nbsp;MW.
/// </para>
/// <para><b>Heuristic limits.</b> Unit-ness is not recorded in the file, so detection is a guess and
/// is deliberately <i>conservative</i> — it would rather miss a raw case than corrupt a correct one.
/// It requires <b>three independent tells to agree</b> (see <see cref="LooksRawUnits"/>); any one of
/// them alone is not enough. Consequences of that stance:
/// <list type="bullet">
///   <item>A raw case with only impedances-in-ohms but genuinely tiny loads (or vice-versa) will
///     <i>not</i> be flagged.</item>
///   <item>A raw feeder whose source/substation bus is modelled at a transmission voltage
///     (&gt; <see cref="DistributionKvCeiling"/> kV) will not be flagged, because the kV gate is
///     taken over <i>all</i> buses. The known raw cases are single-voltage feeders, so this is safe
///     in practice.</item>
///   <item>The already-per-unit MV benchmark <c>cigre_mv</c> is <i>not</i> flagged: it has a 110&nbsp;kV
///     source bus (fails the kV gate) and per-unit loads summing to &lt; 1× base (fails the load
///     gate) — two layers of protection.</item>
/// </list>
/// </para>
/// </remarks>
public static class MatpowerUnitNormalizer
{
    // --- Heuristic thresholds (documented so they can be re-tuned with evidence) ------------------

    /// <summary>Upper bound (kV) for a "distribution / medium-voltage" system. GB/EU distribution
    /// runs at ≤ 33 kV; transmission starts at 66–132 kV. Any bus above this rules the case out.</summary>
    private const double DistributionKvCeiling = 35.0;

    /// <summary>How many times BaseMva the total real demand must exceed before it looks like the
    /// loads are really in kW. A correctly-scaled feeder in MW sits well under 1× base here (cigre_mv
    /// ≈ 0.45×); a kW-misread feeder lands ~1000× higher (case33bw ≈ 371×). 30× is a wide, safe gap.</summary>
    private const double LoadToBaseRatioTrigger = 30.0;

    /// <summary>Floor (as-given) on the median branch series-impedance magnitude √(r²+x²). Genuine
    /// per-unit feeder branches are ≪ 1 (often &lt; 0.01); ohmic values on an MV feeder are ~0.05–2.
    /// Below this floor the numbers already look like per-unit, so we do not treat them as ohms.</summary>
    private const double OhmicImpedanceFloor = 0.05;

    /// <summary>Ceiling on the median <i>converted</i> per-unit impedance (raw √(r²+x²) ÷ Zbase). If
    /// dividing by Zbase still leaves an implausibly large per-unit value, the raw numbers were not
    /// ohms after all, so we refuse to convert.</summary>
    private const double ConvertedPuImpedanceCeiling = 1.0;

    /// <summary>
    /// Conservative heuristic: does this model appear to be a RAW-unit distribution case (ohms + kW)
    /// rather than the per-unit / MW convention the solvers expect? Requires the distribution-scale,
    /// load-magnitude and ohmic-impedance tells to <i>all</i> agree; see the class remarks for limits.
    /// </summary>
    public static bool LooksRawUnits(GridModel m)
    {
        if (m is null) return false;
        if (m.BaseMva <= 0) return false;
        if (m.Buses.Count == 0 || m.Branches.Count == 0) return false;

        // Tell 1 — distribution scale. We need real base voltages to reason about (and to build
        // Zbase for the conversion); a case with no BaseKv anywhere cannot be normalised, so bail.
        // Taking the ceiling over ALL buses means a transmission-voltage source bus vetoes the case
        // (this is what protects cigre_mv, whose 110 kV feed-in bus sits above the ceiling).
        double maxBaseKv = 0.0;
        bool anyBaseKv = false;
        foreach (Bus b in m.Buses)
        {
            if (b.BaseKv > 0)
            {
                anyBaseKv = true;
                if (b.BaseKv > maxBaseKv) maxBaseKv = b.BaseKv;
            }
        }
        if (!anyBaseKv) return false;                       // no voltage base → can't judge or convert
        if (maxBaseKv > DistributionKvCeiling) return false; // transmission-voltage bus present → not a raw feeder

        // Tell 2 — load magnitude. Summed real demand, read as MW, is implausibly large for this base.
        double totalPd = 0.0;
        foreach (Bus b in m.Buses) totalPd += Math.Abs(b.Pd);
        double loadToBase = totalPd / m.BaseMva;
        if (loadToBase < LoadToBaseRatioTrigger) return false;

        // Tell 3 — ohmic impedance. The median branch |Z| (as given) is too big to be per-unit, and
        // dividing by the from-bus Zbase yields a sane per-unit value. Median (not mean) resists the
        // odd large-transformer branch skewing the statistic.
        Dictionary<int, double> kvById = BuildBaseKvLookup(m);

        var rawMag = new List<double>(m.Branches.Count);
        var convertedMag = new List<double>(m.Branches.Count);
        foreach (Branch br in m.Branches)
        {
            double mag = Math.Sqrt(br.R * br.R + br.X * br.X);
            rawMag.Add(mag);

            double zbase = ZbaseFor(br.From, kvById, m.BaseMva);
            convertedMag.Add(zbase > 0 ? mag / zbase : mag);
        }

        double medianRaw = Median(rawMag);
        double medianConverted = Median(convertedMag);

        if (medianRaw < OhmicImpedanceFloor) return false;                 // already looks per-unit
        if (medianConverted >= ConvertedPuImpedanceCeiling) return false;  // ÷Zbase still huge → not ohms

        return true;
    }

    /// <summary>
    /// Returns a per-unit / MW copy of <paramref name="m"/> if it <see cref="LooksRawUnits"/>, else
    /// returns <paramref name="m"/> unchanged. The copy is immutable: <see cref="Bus"/> and
    /// <see cref="Branch"/> are records copied with <c>with</c>, and a fresh <see cref="GridModel"/>
    /// carries every other property across verbatim.
    /// </summary>
    /// <remarks>
    /// Scope of the conversion (matches the raw-unit definition and nothing more):
    /// <list type="bullet">
    ///   <item>Branch <c>R</c>, <c>X</c> ÷ Zbase(from-bus). Branch <c>B</c> is left untouched — line
    ///     charging converts as a <i>susceptance</i> (×Zbase, not ÷), and the known raw feeders carry
    ///     no charging anyway.</item>
    ///   <item>Bus <c>Pd</c>, <c>Qd</c>, <c>Gs</c>, <c>Bs</c> ÷ 1000 (kW/kVAr → MW/MVAr).</item>
    ///   <item>Generators are left as-is: on these radial feeders the only generator is the slack,
    ///     whose Pg/Qg the load flow computes, so its stored value is immaterial.</item>
    ///   <item>A branch whose from-bus has no BaseKv (Zbase undefined) keeps its raw impedance; this
    ///     is not expected on a single-voltage feeder but avoids dividing by zero.</item>
    /// </list>
    /// </remarks>
    public static GridModel Normalize(GridModel m)
    {
        if (!LooksRawUnits(m)) return m;

        // Generators are not converted (the remarks say why: on the known raw feeders the only generator is the slack,
        // whose output the load flow computes). That reasoning holds only for a single generator. A raw feeder with
        // distributed generation would keep its kW figures as MW (a 500 kW PV unit read as 500 MW) after its loads were
        // converted, so it is refused, naming the count, rather than half-converted.
        if (m.AllGenerators.Count > 1)
            throw new InvalidDataException(
                $"MATPOWER case '{m.Name}' looks like a raw-unit feeder (ohms and kW) but has {m.AllGenerators.Count} generators; " +
                "only a single slack generator can be left unconverted. Convert the case to per-unit and MW (or load it with " +
                "MatpowerUnitHandling.AsWritten and convert it yourself).");

        Dictionary<int, double> kvById = BuildBaseKvLookup(m);

        var buses = new List<Bus>(m.Buses.Count);
        foreach (Bus b in m.Buses)
            buses.Add(b with
            {
                Pd = b.Pd / 1000.0,
                Qd = b.Qd / 1000.0,
                Gs = b.Gs / 1000.0,
                Bs = b.Bs / 1000.0,
            });

        var branches = new List<Branch>(m.Branches.Count);
        foreach (Branch br in m.Branches)
        {
            double zbase = ZbaseFor(br.From, kvById, m.BaseMva);
            if (zbase > 0)
                branches.Add(br with { R = br.R / zbase, X = br.X / zbase });
            else
                branches.Add(br);
        }

        // CopyWith carries every other property (asset dynamics and protections included; they used to be
        // dropped here, R270).
        return m.CopyWith(buses: buses, branches: branches);
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>Map bus id → BaseKv (first wins on the rare duplicate id).</summary>
    private static Dictionary<int, double> BuildBaseKvLookup(GridModel m)
    {
        var kvById = new Dictionary<int, double>(m.Buses.Count);
        foreach (Bus b in m.Buses) kvById.TryAdd(b.Id, b.BaseKv);
        return kvById;
    }

    /// <summary>Zbase = BaseKv² / BaseMva for the given bus, or 0 if its base voltage is unknown.</summary>
    private static double ZbaseFor(int busId, Dictionary<int, double> kvById, double baseMva)
        => kvById.TryGetValue(busId, out double kv) && kv > 0 && baseMva > 0
            ? kv * kv / baseMva
            : 0.0;

    /// <summary>Median of a value list (average of the two middle elements for an even count).
    /// Sorts a copy so the caller's list is left intact. Empty → 0.</summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0.0;
        var sorted = new List<double>(values);
        sorted.Sort();
        int n = sorted.Count;
        return n % 2 == 1
            ? sorted[n / 2]
            : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
    }
}
