using GridSim.Core.Model;

namespace GridSim.Core.Harmonics;

/// <summary>
/// One bus's harmonic-voltage compliance verdict against a planning standard. <see cref="VoltageLevelKnown"/>
/// is false when the bus has no usable nominal voltage (BaseKv missing, zero, negative or non-finite): the
/// limit tier cannot be chosen, so the bus is judged against the STRICTEST tier and can never be
/// <see cref="Compliant"/> (R265, October 2026).
/// </summary>
public sealed record BusComplianceResult(
    int BusId,
    double BaseKv,
    double ThdPct,
    double ThdLimitPct,
    double WorstIhdPct,
    int WorstIhdOrder,
    double IhdLimitPct,
    bool Compliant,
    bool VoltageLevelKnown = true);

/// <summary>
/// Harmonic-voltage compliance assessment. IEEE 519-2014 sets bus voltage-distortion limits (THD and each
/// individual order) that tighten with voltage level; the UK G5/5 planning levels are broadly comparable at
/// the distribution/HV tiers. The engine reports the measured THD/IHD from a penetration study against those
/// limits per bus, so a study yields a pass/fail an operator can act on.
///
/// <para>The IEEE 519 limits are exact; the per-order G5/5 tables are detailed and version-sensitive, so only
/// the headline THD planning levels are encoded here (tagged as such, from G5 Issue 5 Table 1) - the full G5/5
/// order tables are a documented refinement. (IEEE 519 itself is not in docs/reference; only the ABB review note
/// of IEEE 519-2022, S293, is.)</para>
/// </summary>
public static class HarmonicCompliance
{
    /// <summary>IEEE 519-2014 bus voltage-distortion limits (THD %, individual-harmonic %) by nominal kV.</summary>
    // GR-579: a negative or NaN kV is not a voltage (a negative one used to fall into the most lenient, <= 1 kV, tier).
    public static (double ThdPct, double IhdPct) Ieee519VoltageLimits(double baseKv) => baseKv switch
    {
        < 0 or double.NaN => throw new ArgumentOutOfRangeException(nameof(baseKv), baseKv, "IEEE 519 limits need a nominal voltage of 0 kV or above."),
        <= 1.0 => (8.0, 5.0),      // V &lt;= 1 kV
        <= 69.0 => (5.0, 3.0),     // 1 kV &lt; V &lt;= 69 kV
        <= 161.0 => (2.5, 1.5),    // 69 kV &lt; V &lt;= 161 kV
        _ => (1.5, 1.0),           // V &gt; 161 kV
    };

    /// <summary>
    /// G5/5 (UK) voltage THD planning level (%) by nominal kV - the headline THD levels only; the per-order tables
    /// (G5 Issue 5 Tables 2 to 6) are a documented refinement.
    /// <para>Source (S3 G02 grounding, October 2026): ENA Engineering Recommendation G5 Issue 5 (2020), docs/reference
    /// id S139, section 5.2, Table 1 "THDV planning levels at supply system PCC nominal operating voltage", p.21:
    /// V &lt;= 0.4 kV 5%; 0.4 &lt; V &lt;= 25 kV 4.5%; 25 &lt; V &lt;= 66 kV 3.7%; 66 &lt; V &lt;= 230 kV 3%;
    /// V &gt; 230 kV 3%.</para>
    /// <para>Before the grounding the bands and levels were unsourced ([VERIFY]): &lt;= 1 kV 5%, &lt;= 33 kV 4%,
    /// &lt;= 132 kV 3%, above 2%. So 400 kV and 275 kV move 2% -> 3%, 33 kV 4% -> 3.7%, 11 kV 4% -> 4.5% and a bus
    /// above 0.4 kV but at or below 1 kV 5% -> 4.5%; 132 kV stays 3%. The strictest level (the one an unknown voltage
    /// level gets, R265) is now 3% (the 66-230 kV and the above-230 kV rows are both 3%).</para>
    /// </summary>
    public static double G55ThdPlanningLevelPct(double baseKv) => baseKv switch
    {
        <= 0.4 => 5.0,       // G5 Issue 5 Table 1: V <= 0.4 kV
        <= 25.0 => 4.5,      // 0.4 kV < V <= 25 kV
        <= 66.0 => 3.7,      // 25 kV < V <= 66 kV
        <= 230.0 => 3.0,     // 66 kV < V <= 230 kV
        _ => 3.0,            // V > 230 kV
    };

    /// <summary>
    /// The G5/5 headline THD verdict for an assessed bus: its planning level and whether its THD is within it. A
    /// bus with an unknown voltage level (<see cref="BusComplianceResult.VoltageLevelKnown"/> false) gets the
    /// strictest level and is never within it (R265) - the same rule as the IEEE 519 verdict.
    /// </summary>
    public static (double PlanningLevelPct, bool WithinLevel) G55Verdict(BusComplianceResult bus)
    {
        double level = G55ThdPlanningLevelPct(bus.VoltageLevelKnown ? bus.BaseKv : double.PositiveInfinity);
        return (level, bus.VoltageLevelKnown && bus.ThdPct <= level);
    }

    /// <summary>Assess every bus's voltage distortion against IEEE 519 given a penetration result.</summary>
    public static IReadOnlyList<BusComplianceResult> AssessIeee519(
        GridModel model, HarmonicPenetrationResult res, double[]? fundamentalVm = null)
    {
        var kvById = model.Buses.ToDictionary(b => b.Id, b => b.BaseKv);
        var results = new List<BusComplianceResult>(res.BusIds.Count);

        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(res, fundamentalVm);

        for (int bi = 0; bi < res.BusIds.Count; bi++)
        {
            int busId = res.BusIds[bi];
            // R265: an unknown voltage level (missing, zero, negative or non-finite BaseKv) used to fall into the
            // <= 1 kV row - the MOST lenient tier (8% THD) - so an imported 400 kV bus with BaseKv = 0 passed at
            // 4% THD instead of failing its 1.5% limit. It now gets the strictest tier and is never certified.
            double kv = kvById.TryGetValue(busId, out double v) ? v : double.NaN;
            bool kvKnown = kv > 0.0 && double.IsFinite(kv);
            (double thdLimit, double ihdLimit) = Ieee519VoltageLimits(kvKnown ? kv : double.PositiveInfinity);
            double v1 = fundamentalVm is not null && bi < fundamentalVm.Length && fundamentalVm[bi] > 0 ? fundamentalVm[bi] : 1.0;

            double worstIhd = 0; int worstOrder = 0;
            foreach (int h in res.Orders)
            {
                if (h == 1) continue;
                double ihd = HarmonicIndices.IndividualDistortionPct(res, bi, h, v1);
                if (ihd > worstIhd) { worstIhd = ihd; worstOrder = h; }
            }

            double thd = idx.ThdVoltagePct[bi];
            bool ok = kvKnown && thd <= thdLimit && worstIhd <= ihdLimit;
            results.Add(new BusComplianceResult(busId, kv, thd, thdLimit, worstIhd, worstOrder, ihdLimit, ok, kvKnown));
        }
        return results;
    }
}
