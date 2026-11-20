using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Sensitivities;

/// <summary>One column of outage transfer distribution factors (OTDF): the flow on every branch per MW injected at
/// <see cref="BusId"/> AFTER branch <see cref="OutageBranch"/> is out. When the outage islands the network the column is
/// not defined: <see cref="Islanded"/> is true and every factor is NaN (never an invented number).</summary>
public sealed record OtdfColumn(int OutageBranch, int BusId, bool Islanded, double[] Factors);

/// <summary>
/// OTDF (R4-03, report row 2.2): <c>OTDF[l, bus | k] = PTDF[l, bus] + LODF[l, k] * PTDF[k, bus]</c>, the standard
/// post-outage transfer factor built from the validated <see cref="DistributionFactors"/> (one factorisation of B', two
/// solves per column). The outaged branch's own entry is exactly zero. Same DC model, same islanding rule
/// (<see cref="DistributionFactors.IslandingSensitivityTolerance"/>) as the existing <c>ptdf</c> and <c>lodf</c> verbs.
/// </summary>
public static class OutageTransferFactors
{
    /// <summary>The OTDF column for an outage of in-service branch <paramref name="outageBranch"/> and an injection at
    /// <paramref name="busId"/> (withdrawn at that bus's island reference, as <see cref="DistributionFactors.PtdfColumn"/>).</summary>
    public static OtdfColumn Column(GridModel model, DistributionFactors factors, int outageBranch, int busId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(factors);
        if (outageBranch < 0 || outageBranch >= model.Branches.Count)
            throw new ArgumentOutOfRangeException(nameof(outageBranch), outageBranch, $"the model has {model.Branches.Count} branches.");
        if (!model.Branches[outageBranch].InService)
            throw new ArgumentException($"branch {outageBranch} is out of service: removing it is not an outage.", nameof(outageBranch));
        double[] ptdf = factors.PtdfColumn(busId);   // throws for a bus the model does not have
        if (factors.OutageIslands(outageBranch))
        {
            var nan = new double[ptdf.Length];
            Array.Fill(nan, double.NaN);
            return new OtdfColumn(outageBranch, busId, true, nan);
        }
        double[] lodf = factors.LodfColumn(outageBranch);
        var otdf = new double[ptdf.Length];
        double transfer = ptdf[outageBranch];
        for (int l = 0; l < otdf.Length; l++) otdf[l] = l == outageBranch ? 0.0 : ptdf[l] + lodf[l] * transfer;
        return new OtdfColumn(outageBranch, busId, false, otdf);
    }
}
