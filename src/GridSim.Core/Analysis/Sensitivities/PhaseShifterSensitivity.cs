using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Sensitivities;

/// <summary>
/// Phase-shifter distribution factors (PSDF, R4-03, report row 2.3): the DC change of every branch flow per radian of
/// phase shift on one branch.
///
/// <para>Convention: the solver's own. <see cref="Branch.PhaseShiftDeg"/> enters the pi-model as tap = a e^(j phi), and the
/// DC flow on the shifted branch is <c>b (theta_from - theta_to - phi)</c> (positive phi REDUCES the From-to-To flow). With
/// b = 1/X the shift is equivalent to injecting <c>+b phi</c> at the From bus and <c>-b phi</c> at the To bus, so for l != k
/// <c>dP_l/dphi = b_k * (PTDF[l, from] - PTDF[l, to])</c> and on the shifted branch itself
/// <c>dP_k/dphi = b_k * (PTDF[k, from] - PTDF[k, to] - 1)</c>. For two parallel branches X1 and X2 this is
/// <c>-1/(X1 + X2)</c> on branch 1 (RC4 PLAN writes it with the opposite sign, +1/(X1+X2), because it defines the flow as
/// (D + phi)/X1; the magnitude is the same). Results are in MW per radian: the per-unit value times the system base.</para>
/// </summary>
public static class PhaseShifterSensitivity
{
    /// <summary>MW per radian of phase shift on in-service branch <paramref name="branch"/>, one entry per model branch.</summary>
    public static double[] FlowSensitivityMwPerRad(GridModel model, DistributionFactors factors, int branch)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(factors);
        if (branch < 0 || branch >= model.Branches.Count)
            throw new ArgumentOutOfRangeException(nameof(branch), branch, $"the model has {model.Branches.Count} branches.");
        Branch br = model.Branches[branch];
        if (!br.InService) throw new ArgumentException($"branch {branch} is out of service.", nameof(branch));
        if (br.X == 0.0) throw new ArgumentException($"branch {branch} has zero reactance; the DC model cannot represent it.", nameof(branch));
        double b = 1.0 / br.X;
        double[] pf = factors.PtdfColumn(br.From);
        double[] pt = factors.PtdfColumn(br.To);
        var s = new double[pf.Length];
        for (int l = 0; l < s.Length; l++)
        {
            double phi = pf[l] - pt[l];
            s[l] = (l == branch ? phi - 1.0 : phi) * b * model.BaseMva;
        }
        return s;
    }
}
