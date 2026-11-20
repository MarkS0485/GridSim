namespace GridSim.Core.Analysis.Reliability.Network;

/// <summary>
/// RC4 R4-13 customer indices from load-point figures. The expressions and the order of every sum are those of the
/// incumbent <see cref="ReliabilityStudy"/> (RC3 P8), so a network with the same data and no back-feed gives the same
/// bits: SAIFI = sum(lambda_j N_j) / sum(N_j), SAIDI = sum(U_j N_j) / sum(N_j), CAIDI = SAIDI / SAIFI,
/// ASAI = 1 - SAIDI / hoursPerYear, ENS = sum(L_j U_j), AENS = ENS / sum(N_j). A null load-point figure withholds the
/// indices that need it.
/// </summary>
internal static class CustomerIndices
{
    public static IReadOnlyList<ReliabilityIndex> System(IReadOnlyList<LoadPointReliability> points, double hoursPerYear, out double customers)
    {
        customers = 0;
        double? saifiSum = 0, saidiSum = 0, ensSum = 0;
        foreach (LoadPointReliability p in points)
        {
            customers += p.Customers;
            saifiSum = p.InterruptionsPerYear is double l ? saifiSum + l * p.Customers : null;
            saidiSum = p.OutageHoursPerYear is double u ? saidiSum + u * p.Customers : null;
            ensSum = p.EnergyNotSuppliedMwhPerYear is double e ? ensSum + e : null;
        }
        double? saifi = saifiSum / customers;
        double? saidi = saidiSum / customers;
        double? caidi = saifi is double f1 && saidi is double d1 && f1 > 0 ? d1 / f1 : null;
        double? asai = saidi is double d2 ? 1.0 - d2 / hoursPerYear : null;
        double? aens = ensSum / customers;
        return
        [
            new("SAIFI", saifi, "interruptions/(customer.yr)", "sum(lambda_j N_j) / sum(N_j)"),
            new("SAIDI", saidi, "h/(customer.yr)", "sum(U_j N_j) / sum(N_j)"),
            new("CAIDI", caidi, "h/interruption", "SAIDI / SAIFI"),
            new("ASAI", asai, "1", "1 - SAIDI / hoursPerYear"),
            new("ENS", ensSum, "MWh/yr", "sum(L_j U_j)"),
            new("AENS", aens, "MWh/(customer.yr)", "ENS / sum(N_j)"),
        ];
    }
}
