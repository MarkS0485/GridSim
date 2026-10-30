namespace GridSim.Core.Dynamics.Emt;

/// <summary>
/// The discrete companion model of a two-terminal reactive element for one timestep: an equivalent
/// conductance <see cref="Geq"/> in parallel with a history current source whose value next step is
/// <c>I_hist = HistCoeffV·v_prev + HistCoeffI·i_prev</c>, where <c>v_prev</c>/<c>i_prev</c> are the element's
/// branch voltage and branch current at the previous step. This single linear form unifies the trapezoidal
/// and backward-Euler R-L and C rules (see <see cref="EmtCompanionStamp"/>). A pure resistor has
/// <c>Geq=1/R</c> and no history (both coefficients zero).
/// </summary>
public readonly record struct Companion(double Geq, double HistCoeffV, double HistCoeffI)
{
    /// <summary>The history current source for this step, from the previous branch voltage and current.</summary>
    public double History(double vPrev, double iPrev) => HistCoeffV * vPrev + HistCoeffI * iPrev;
}

/// <summary>
/// Builds the trapezoidal / backward-Euler companion models (Dommel's method) for the passive EMT elements.
/// Pure functions of the element parameters and the timestep - validated to machine epsilon against the
/// closed-form RLC transient. All values are per-unit (<c>L</c>, <c>C</c> in seconds via <c>X/ω0</c>,
/// <c>B/ω0</c>); the step <c>dt</c> is in seconds.
/// </summary>
public static class EmtCompanionStamp
{
    /// <summary>A pure resistor: conductance only, no history.</summary>
    public static Companion Resistor(double r) => new(1.0 / r, 0.0, 0.0);

    /// <summary>
    /// A series R-L branch (<c>v = R·i + L·di/dt</c>). Trapezoidal:
    /// <c>Geq = 1/(R + 2L/dt)</c>, <c>I_hist = Geq·v_prev + α·i_prev</c>, <c>α = (2L/dt − R)/(2L/dt + R)</c>.
    /// Backward Euler: <c>Geq = 1/(R + L/dt)</c>, <c>I_hist = i_prev/(1 + R·dt/L)</c>.
    /// <c>L = 0</c> degrades exactly to a pure resistor.
    /// </summary>
    public static Companion SeriesRl(double r, double l, double dt, IntegrationRule rule)
    {
        if (l <= 0.0) return Resistor(r);
        if (rule == IntegrationRule.BackwardEuler)
        {
            double gBe = 1.0 / (r + l / dt);
            return new Companion(gBe, 0.0, 1.0 / (1.0 + r * dt / l));
        }
        double twoLoverDt = 2.0 * l / dt;
        double g = 1.0 / (r + twoLoverDt);
        double alpha = (twoLoverDt - r) / (twoLoverDt + r);
        return new Companion(g, g, alpha);
    }

    /// <summary>
    /// A capacitor (<c>i = C·dv/dt</c>). Trapezoidal: <c>Geq = 2C/dt</c>,
    /// <c>I_hist = −Geq·v_prev − i_prev</c>. Backward Euler: <c>Geq = C/dt</c>, <c>I_hist = −Geq·v_prev</c>.
    /// </summary>
    public static Companion Capacitor(double c, double dt, IntegrationRule rule)
    {
        if (rule == IntegrationRule.BackwardEuler)
        {
            double gBe = c / dt;
            return new Companion(gBe, -gBe, 0.0);
        }
        double g = 2.0 * c / dt;
        return new Companion(g, -g, -1.0);
    }

    /// <summary>
    /// A series R-C branch (R078: series compensation), <c>v = R*i + v_C</c> with <c>i = C*dv_C/dt</c>, folded into
    /// one companion so no internal node is needed. With <c>z</c> the capacitor's companion resistance
    /// (<c>dt/(2C)</c> trapezoidal, <c>dt/C</c> backward Euler) the capacitor obeys <c>v_C(n) = z*i(n) + (memory)</c>,
    /// so the branch is <c>i(n) = Geq*v(n) + I_hist</c> with <c>Geq = 1/(R + z)</c>, and the memory, written in the
    /// branch voltage and current of the previous step through <c>v_C(n-1) = v(n-1) - R*i(n-1)</c>, gives:
    /// <list type="bullet">
    /// <item>Trapezoidal: <c>I_hist = -Geq*v(n-1) + Geq*(R - z)*i(n-1)</c>.</item>
    /// <item>Backward Euler: <c>I_hist = -Geq*v(n-1) + Geq*R*i(n-1)</c>.</item>
    /// </list>
    /// With R = 0 these are exactly the <see cref="Capacitor"/> rules (algebraically; the arithmetic differs in
    /// the last bit, which is why a shunt capacitor keeps its own rule). A negative R (a network-reduction
    /// equivalent) is accepted; if <c>R + z</c> is zero the conductance is infinite and the caller must refuse it.
    /// </summary>
    public static Companion SeriesRc(double r, double c, double dt, IntegrationRule rule)
    {
        if (rule == IntegrationRule.BackwardEuler)
        {
            double zBe = dt / c;                  // backward-Euler companion resistance of the capacitor
            double gBe = 1.0 / (r + zBe);
            return new Companion(gBe, -gBe, gBe * r);
        }
        double z = dt / (2.0 * c);                // trapezoidal companion resistance of the capacitor
        double g = 1.0 / (r + z);
        return new Companion(g, -g, g * (r - z));
    }

    /// <summary>A shunt reactor / inductor to ground - the <see cref="SeriesRl"/> rule with R=0.</summary>
    public static Companion Inductor(double l, double dt, IntegrationRule rule) => SeriesRl(0.0, l, dt, rule);
}
