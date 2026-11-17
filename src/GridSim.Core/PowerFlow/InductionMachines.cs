using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>One induction machine's solved state: its slip, the active and reactive power it draws from its bus (MW,
/// Mvar; negative active power for a generator) and its mechanical (shaft) power (MW, positive for a motor).</summary>
public sealed record InductionMachineResult(string Name, int Bus, double Slip, double Mw, double Mvar, double MechanicalMw);

/// <summary>
/// The steady-state model of an induction machine (PSS/E induction machine record, custom design, single cage), solved
/// with the AC power flow inside <see cref="HvdcCoupling"/>.
///
/// <para><b>Equivalent circuit</b> (all pu on the machine base MBASE and RATEKV): stator RA + j XA in series with the
/// magnetising reactance j XM in parallel with the rotor R1 / s + j X1, at slip s. At terminal voltage V (pu of RATEKV)
/// the input impedance is Zin = RA + j XA + (j XM || (R1 / s + j X1)); the machine draws S = |V|^2 / conj(Zin) (its
/// reactive power positive when absorbed), the air-gap power is |V|^2 Re(Zp) / |Zin|^2 with Zp the parallel part, and the
/// mechanical power is the air-gap power times (1 - s). At s = 0 the rotor branch is open.</para>
///
/// <para><b>Slip.</b> The slip meets the schedule PSET: mechanical power out (PCODE 1) or electrical power in (PCODE 2),
/// positive for a motor and negative for a generator. It is found on the stable branch, between synchronous speed and the
/// slip of peak power: |s| is stepped out from 1e-9 by doubling (at most to 1) until the power passes the schedule, then
/// bisected to the last representable slip. A schedule beyond the peak power at the present voltage (the machine would
/// stall, or a generator run away) has no steady state: the solve fails, naming the machine. Plain arithmetic only, so
/// the result is the same bits everywhere.</para>
///
/// <para>The machine is a constant-power load at its bus for each AC solve, re-taken at each solved voltage until the
/// powers repeat within 1e-9 (1 + |P|).</para>
/// </summary>
public static class InductionMachines
{
    /// <summary>The input powers (pu on the machine base) and the air-gap power at slip <paramref name="s"/> and
    /// terminal voltage <paramref name="v"/> (pu of RATEKV).</summary>
    internal static (double P, double Q, double AirGap) Powers(InductionMachine m, double v, double s)
    {
        // Zp = jXm || Zr, Zr = R1/s + jX1 (open at s = 0).
        double zpr, zpi;
        if (s == 0) { zpr = 0; zpi = m.XmPu; }
        else
        {
            double a = m.R1Pu / s, b = m.X1Pu;
            double nr = -m.XmPu * b, ni = m.XmPu * a;      // jXm (a + jb)
            double dr = a, di = b + m.XmPu;                 // a + j(b + Xm)
            double dd = dr * dr + di * di;
            zpr = (nr * dr + ni * di) / dd;
            zpi = (ni * dr - nr * di) / dd;
        }
        double zr = m.RaPu + zpr, zi = m.XaPu + zpi;
        double z2 = zr * zr + zi * zi, v2 = v * v;
        return (v2 * zr / z2, v2 * zi / z2, v2 * zpr / z2);
    }

    /// <summary>The scheduled quantity (mechanical out, or electrical in; pu on the machine base) at slip
    /// <paramref name="s"/>.</summary>
    private static double Scheduled(InductionMachine m, double v, double s)
    {
        (double p, _, double ag) = Powers(m, v, s);
        return m.PowerCode == InductionPowerCode.Mechanical ? ag * (1 - s) : p;
    }

    /// <summary>Solve <paramref name="m"/> at bus voltage <paramref name="vmBus"/> (pu of the bus base
    /// <paramref name="busKv"/>).</summary>
    public static InductionMachineResult Solve(InductionMachine m, double vmBus, double busKv)
    {
        ArgumentNullException.ThrowIfNull(m);
        double v = m.RatedKv > 0 && busKv > 0 ? vmBus * busKv / m.RatedKv : vmBus;
        double target = m.ScheduledMw / m.MBase;
        double s = SolveSlip(m, v, target);
        (double p, double q, double ag) = Powers(m, v, s);
        return new InductionMachineResult(m.Name, m.Bus, s, p * m.MBase, q * m.MBase, ag * (1 - s) * m.MBase);
    }

    private static double SolveSlip(InductionMachine m, double v, double target)
    {
        double f0 = Scheduled(m, v, 0) - target;
        if (f0 == 0) return 0;
        // The scheduled quantity rises with slip on the stable branch (motor side) and falls below zero on the generator
        // side, so the direction of the slip is the sign of the shortfall at synchronous speed.
        double dir = f0 < 0 ? 1.0 : -1.0;
        double lo = 0, flo = f0, hi = 0, fhi = f0, best = double.NaN;
        bool found = false;
        for (double mag = 1e-9; mag <= 1.0; mag *= 2)
        {
            double sk = dir * mag, fk = Scheduled(m, v, sk) - target;
            if (Math.Sign(fk) != Math.Sign(flo) || fk == 0) { hi = sk; fhi = fk; found = true; break; }
            // Past the peak: the quantity moves back towards (or away from) the schedule without reaching it.
            if (Math.Abs(fk) > Math.Abs(flo) && lo != 0) break;
            lo = sk; flo = fk;
        }
        if (!found)
            throw new InvalidOperationException($"induction machine {m.Name} at bus {m.Bus}: no slip on the stable branch meets its schedule of {F(m.ScheduledMw)} MW at {F(v)} pu; the {(dir > 0 ? "motor would stall" : "generator would run away")}.");
        if (fhi == 0) return hi;
        for (int k = 0; k < 2000; k++)
        {
            double mid = lo + (hi - lo) / 2;
            if (mid == lo || mid == hi) break;
            double fm = Scheduled(m, v, mid) - target;
            if (fm == 0) { best = mid; break; }
            if (Math.Sign(fm) == Math.Sign(flo)) { lo = mid; flo = fm; } else { hi = mid; fhi = fm; }
        }
        if (!double.IsNaN(best)) return best;
        return Math.Abs(flo) <= Math.Abs(fhi) ? lo : hi;
    }

    /// <summary>
    /// RC3 P8 (motor acceleration): the machine's electrical state at a SPECIFIED slip, with no schedule and no slip
    /// search. A separate, additive entry point over the same single-cage arithmetic <see cref="Solve"/> uses
    /// (<see cref="Powers"/>, unchanged): the motor acceleration study derives the slip from rotor speed at every step
    /// (s = 1 at standstill) and must never select a slip through the steady-state equilibrium search.
    /// <paramref name="vMotorPu"/> is the terminal voltage in pu of the machine's rated voltage. Slip 0 opens the rotor
    /// branch, as in <see cref="Solve"/>.
    /// </summary>
    public static InductionMachineSlipPoint AtSlip(InductionMachine m, double vMotorPu, double slip)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (!double.IsFinite(vMotorPu) || vMotorPu < 0) throw new ArgumentOutOfRangeException(nameof(vMotorPu), vMotorPu, "terminal voltage must be finite and not negative.");
        if (!double.IsFinite(slip)) throw new ArgumentOutOfRangeException(nameof(slip), slip, "slip must be finite.");
        (double p, double q, double ag) = Powers(m, vMotorPu, slip);
        // At 1 pu the same arithmetic gives the input admittance itself: P - jQ drawn at |V| = 1 is conj(1 / Zin), so
        // G = P1 and B = -Q1 (pu on the machine base). v2 = 1 exactly, so these are zr / |Z|^2 and zi / |Z|^2 unrounded.
        (double p1, double q1, _) = Powers(m, 1.0, slip);
        double s2 = p * p + q * q;
        double current = vMotorPu > 0 ? Math.Sqrt(s2) / vMotorPu : 0.0;
        return new InductionMachineSlipPoint(slip, vMotorPu, p, q, ag, ag * (1 - slip), current, p1, -q1);
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>
/// RC3 P8: an induction machine's electrical state at a specified slip (<see cref="InductionMachines.AtSlip"/>), all in
/// per unit on the machine base (MBASE, RATEKV): input active and reactive power (reactive positive when absorbed), the
/// air-gap power, which is also the electrical torque in pu of the synchronous torque base, the mechanical power
/// (air-gap times (1 - s)), the terminal current magnitude, and the input admittance G + jB (B negative: inductive) that
/// represents the machine at this slip as a constant-impedance load.
/// </summary>
public sealed record InductionMachineSlipPoint(double Slip, double VoltagePu, double PPu, double QPu, double AirGapPu,
    double MechanicalPu, double CurrentPu, double ConductancePu, double SusceptancePu)
{
    /// <summary>Electrical torque, pu of the torque base (rated MVA over synchronous mechanical speed).</summary>
    public double TorquePu => AirGapPu;
}
