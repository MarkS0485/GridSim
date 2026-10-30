using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// One FACTS device's solved state: the shunt element's reactive power into the sending end bus (Mvar) and its current
/// (MVA at unity voltage), the series element's power delivered to the terminal end bus (MW, Mvar), the series voltage
/// (pu) and current (MVA at unity voltage), the active power through the bridge from the shunt to the series element
/// (MW), and whether the shunt current rating limited the shunt element (the sending end bus then not at VSET).
/// Series quantities are 0 for a <see cref="FactsMode.Statcom"/>; for a <see cref="FactsMode.SeriesImpedance"/> device
/// the series element is an ordinary branch and its flow is in the line flows.
/// </summary>
public sealed record FactsResult(string Name, FactsMode Mode, int SendingBus, int TerminalBus, double ShuntMvar, double ShuntCurrentMva,
    double SeriesMw, double SeriesMvar, double SeriesVoltagePu, double SeriesCurrentMva, double BridgeMw, bool ShuntLimited);

/// <summary>Where a static var compensator ended: holding its bus voltage, fixed at its initial susceptance (its bus inside
/// the band), or at either end of its range.</summary>
public enum SvcState
{
    /// <summary>Fixed at BINIT: its bus is inside VSWLO..VSWHI without any change.</summary>
    Initial,
    /// <summary>Holding its bus at the set-point (a fixed set-point, or the band edge its bus had crossed).</summary>
    Regulating,
    /// <summary>Every capacitor block in (the top of its range): the bus is below the set-point.</summary>
    AtMaximum,
    /// <summary>Every reactor block in (the bottom of its range): the bus is above the set-point.</summary>
    AtMinimum,
}

/// <summary>One static var compensator's solved state: its susceptance (Mvar at 1 pu), its reactive power at the solved
/// voltage (Mvar), the set-point it held (pu; NaN when it stayed at BINIT) and its state.</summary>
public sealed record SvcResult(string Name, int Bus, double SusceptanceMvar, double Mvar, double SetpointPu, SvcState State);

/// <summary>
/// The steady-state models of FACTS devices (PSS/E FACTS device records, modes 1 to 3) and static var compensators
/// (PSS/E switched shunts with continuous voltage control, MODSW 2), solved with the AC power flow inside
/// <see cref="HvdcCoupling"/>.
///
/// <para><b>Shunt element (every mode, when SHMX is above 0).</b> A voltage-controlling source at the sending end bus I
/// holding VSET. Its current is limited to SHMX (MVA at unity voltage): after each AC solve the shunt element's apparent
/// power S_sh is recomputed at the solved voltage, and when |S_sh| / |V_I| exceeds SHMX the source's reactive power is
/// fixed at the largest the rating allows, sqrt((SHMX |V_I|)^2 - P_sh^2) with the sign of the excess, and the bus is
/// released (PQ), the value re-taken at each solved voltage until it settles (a current-limited source).</para>
///
/// <para><b>UPFC (MODE 1 with a terminal end bus J).</b> The power-injection model of a lossless UPFC: the series element
/// delivers PDES + j QDES to bus J; the active power it takes from bus I and the series converter's power both come
/// through bus I, so bus I gives up exactly PDES of active power (the shunt converter supplies the bridge power), its
/// reactive power is whatever holds VSET. With no series impedance, the series voltage is V_se = V_J - V_I and the series
/// current I_se = conj(S_J / V_J); the series element draws S_I = V_I conj(I_se) from bus I, so the shunt converter
/// supplies S_sh = S_device - (-S_I), the bridge power is Re(S_J - S_I). After the solve the terminal end voltage band
/// VTMN..VTMX, the series voltage rating VSMX, the series current rating IMX (when above 0) and the bridge rating TRMX are
/// checked: a device outside any of them makes the solve NOT converged, naming the device and the limit (PSS/E then
/// changes the device's operating mode, which GridSim does not guess). A UPFC needs its shunt element (SHMX above 0).</para>
///
/// <para><b>MODE 2 with a terminal end bus.</b> The series element is bypassed: I and J are joined by a model branch of
/// PSS/E's zero-impedance reactance, the shunt element a STATCOM at I. <b>MODE 3.</b> The series element is a model
/// branch of impedance SET1 + j SET2 between I and J, with the shunt element at I. Both branches are added by the
/// reader, so here those devices are their shunt elements.</para>
///
/// <para><b>Static var compensator.</b> A continuously variable susceptance between every reactor block in and every
/// capacitor block in. Its initial susceptance BINIT is in the bus shunt. With VSWHI equal to VSWLO it holds that
/// voltage; otherwise it stays at BINIT while its bus is inside VSWLO..VSWHI and holds the band edge its bus crossed (the
/// continuous counterpart of MODSW 1's block switching; an interpretation, noted in the manual). While holding, it is a
/// voltage-controlling source of reactive power B V^2 - BINIT V^2 at the set-point; when that would need more than its
/// range, it is fixed at the end of the range (an exact susceptance, so its reactive power follows V^2), the bus
/// released.</para>
///
/// <para>Each controlled bus must have no generator and no VSC converter in voltage control and must not be the slack
/// bus; such a model is refused, naming the device. Several FACTS shunt elements and static var compensators may hold one
/// bus together: the bus is held at their common set-point and the reactive power its sources supply is shared by
/// RMPCT (PSS/E's "percent of the total Mvar required to hold the voltage" each device contributes), each device's share
/// being its RMPCT over the sum of the RMPCTs of the devices still holding the bus. A device whose share would pass its
/// own limit (current rating, susceptance range) is fixed at that limit and the others share the rest; the reactive
/// power still needed only grows for them, so a device fixed at a limit is never released. Conflicting set-points (two
/// VSETs, or a compensator holding a band edge another device does not hold) are refused or fail loud: one bus cannot be
/// held at two voltages.</para>
/// </summary>
public static class FactsDevices
{
    /// <summary>The reactive limits given to a FACTS or SVC source in the AC solve: wide enough that the Newton engine's
    /// own reactive-limit loop never acts on it (the limits are applied here, by current or by susceptance).</summary>
    internal const double UnlimitedMvar = 1e9;

    /// <summary>The bus voltage as a complex number (pu) from the solved magnitude and angle.</summary>
    internal static (double Re, double Im) Phasor(double vm, double vaDeg)
    {
        double a = vaDeg * Math.PI / 180.0;
        return (vm * DetMath.Cos(a), vm * DetMath.Sin(a));
    }

    /// <summary>The series element of a UPFC at the solved voltages (all pu on <paramref name="baseMva"/>): the series
    /// current I_se = conj(S_J / V_J), the power S_I = V_I conj(I_se) it draws from the sending end bus, the series voltage
    /// V_se = V_J - V_I and the bridge power Re(S_J - S_I).</summary>
    internal static (double SiRe, double SiIm, double CurrentPu, double SeriesVPu, double BridgePu) Series(FactsDevice d,
        (double Re, double Im) vi, (double Re, double Im) vj, double baseMva)
    {
        double sjr = d.PdesMw / baseMva, sji = d.QdesMvar / baseMva;
        // conj(I) = S_J / V_J.
        double den = vj.Re * vj.Re + vj.Im * vj.Im;
        double cr = (sjr * vj.Re + sji * vj.Im) / den, ci = (sji * vj.Re - sjr * vj.Im) / den;   // conj(I_se)
        // S_I = V_I conj(I_se).
        double sir = vi.Re * cr - vi.Im * ci, sii = vi.Re * ci + vi.Im * cr;
        double cur = Math.Sqrt(cr * cr + ci * ci);
        double vsr = vj.Re - vi.Re, vsi = vj.Im - vi.Im;
        return (sir, sii, cur, Math.Sqrt(vsr * vsr + vsi * vsi), sjr - sir);
    }

    /// <summary>The largest reactive power (Mvar, positive) the shunt element of <paramref name="d"/> can give at sending
    /// end voltage <paramref name="vm"/> (pu) while it carries <paramref name="pShMw"/> of active power; null when the
    /// active power alone exceeds the rating.</summary>
    internal static double? ShuntQMax(FactsDevice d, double vm, double pShMw)
    {
        double s = d.ShuntMaxMva * vm;
        double rem = s * s - pShMw * pShMw;
        return rem < 0 ? null : Math.Sqrt(rem);
    }

    /// <summary>Check a solved UPFC against its terminal end voltage band and its series voltage, series current and bridge
    /// ratings; the first limit exceeded, as a message, or null.</summary>
    internal static string? CheckUpfc(FactsDevice d, double vmJ, double seriesVPu, double currentPu, double bridgeMw, double baseMva)
    {
        if (vmJ < d.TerminalVminPu || vmJ > d.TerminalVmaxPu)
            return $"FACTS device {d.Name}: the terminal end bus {d.TerminalBus} is at {F(vmJ)} pu, outside VTMN..VTMX {F(d.TerminalVminPu)}..{F(d.TerminalVmaxPu)} pu; PSS/E would change the device's operating mode, which GridSim does not model";
        if (seriesVPu > d.SeriesVmaxPu)
            return $"FACTS device {d.Name}: its series voltage is {F(seriesVPu)} pu, above VSMX {F(d.SeriesVmaxPu)} pu; PSS/E would change the device's operating mode, which GridSim does not model";
        if (d.SeriesImaxMva > 0 && currentPu * baseMva > d.SeriesImaxMva)
            return $"FACTS device {d.Name}: its series current is {F(currentPu * baseMva)} MVA at unity voltage, above IMX {F(d.SeriesImaxMva)}; PSS/E would change the device's operating mode, which GridSim does not model";
        if (Math.Abs(bridgeMw) > d.BridgeMaxMw)
            return $"FACTS device {d.Name}: {F(Math.Abs(bridgeMw))} MW passes through its bridge, above TRMX {F(d.BridgeMaxMw)} MW; PSS/E would change the device's operating mode, which GridSim does not model";
        return null;
    }

    internal static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
