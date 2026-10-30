namespace GridSim.Core.Presentation;

/// <summary>
/// The largest-credible-loss design basis the NESO RP1 / OPERATOR presentation is graded against, shared by
/// the WPF <c>NesoRp1Vm</c> / <c>OperatorVm</c> and the web RP1 / OPERATOR projections. The 3,600 MW Dogger Bank
/// A+B+C correlated cluster is the TSGB Policy 0021 ("The 1,800 Megawatt Fiction") worst-case scenario; the 1,800 MW
/// figure is carried alongside.
/// <para>N040 (S3, October 2026), what the official documents say: NESO, Frequency Risk and Control Report 2025, For
/// Approval, May 2025 (docs/reference S052), s.4.1 p.10: "The minimum inertia of 102 GVA.s is based on a future largest
/// loss of 1800 MW", and s.7.1: additional response "in preparation for securing the future largest loss of 1800 MW".
/// FRCR 2025 secures BMU-only events (one BMU lost) and does NOT secure simultaneous events (s.6.2, s.7.1), and a
/// correlated multi-unit cluster loss is a simultaneous event. So 1,800 MW is the official future largest loss, not a
/// superseded convention, and 3,600 MW is an unsecured scenario, not the official design basis. The presentation labels
/// say so; the constant names (and the values the tests pin) are unchanged, so no number moves.</para>
/// </summary>
public static class CredibleLossBasis
{
    /// <summary>The 3,600 MW Dogger Bank A+B+C correlated cluster (MW): the TSGB Policy 0021 scenario, a simultaneous
    /// event FRCR 2025 does not secure (shown as "correlated cluster (not secured)").</summary>
    public const double DesignBasisLossMw = 3600.0;

    /// <summary>1,800 MW: NESO FRCR 2025 (S052) s.4.1 "future largest loss" (the NETS SQSS v2.10 Infrequent Infeed Loss
    /// Risk, S036 ch.11, is also 1800 MW from 1 April 2014). The name is historical (kept so no test moves).</summary>
    public const double FictionLossMw = 1800.0;

    /// <summary>Human label for what the design basis represents.</summary>
    public const string DesignBasisSource = "Dogger Bank A+B+C";
}
