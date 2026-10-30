using System;
using System.Collections.Generic;
using System.Globalization;

namespace GridSim.Core.Dynamics.Models.Wecc;

// The parameter sets of the WECC second-generation renewable energy system (RES) models, in the names and units of
// the WECC specification and the PSS/E model records (machine base, per unit, seconds). Each record carries its own
// range check (Check), which lists EVERY problem it finds; nothing is clamped or defaulted silently. The defaults are
// typical published values (EPRI 3002006525, 2015, and the parameter ranges printed with the WECC models) so a test or
// a user can start from a sensible unit and change what matters; a DYR file always gives every value explicitly.
//
// Block diagrams: EPRI 3002006525 "Model User Guide for Generic Renewable Energy System Models" (2015, public): REGC_A
// Fig. D-4, REEC_A Fig. D-1, REEC_B Fig. D-2, REEC_C Fig. D-3 and Fig. 3-4, REPC_A Fig. 3-5, current limit logic
// App. B. The engineering choices where sources differ are recorded in the branch notes (DECISIONS.md, D2-D15).

/// <summary>Small helpers shared by the parameter checks.</summary>
internal static class WeccCheck
{
    internal static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Finite and at least <paramref name="min"/>.</summary>
    internal static void AtLeast(List<string> p, string model, string name, double v, double min)
    {
        if (!double.IsFinite(v) || v < min) p.Add($"{model} {name} = {N(v)}: must be a finite number of at least {N(min)}");
    }

    /// <summary>Finite and above <paramref name="min"/>.</summary>
    internal static void Above(List<string> p, string model, string name, double v, double min)
    {
        if (!double.IsFinite(v) || !(v > min)) p.Add($"{model} {name} = {N(v)}: must be a finite number above {N(min)}");
    }

    internal static void Finite(List<string> p, string model, string name, double v)
    {
        if (!double.IsFinite(v)) p.Add($"{model} {name} = {N(v)}: must be a finite number");
    }

    internal static void Flag(List<string> p, string model, string name, int v, params int[] allowed)
    {
        if (Array.IndexOf(allowed, v) < 0) p.Add($"{model} {name} = {v}: must be one of {string.Join(", ", allowed)}");
    }

    internal static void Ordered(List<string> p, string model, string lo, double a, string hi, double b)
    {
        if (double.IsFinite(a) && double.IsFinite(b) && !(a <= b)) p.Add($"{model} {lo} = {N(a)} must not exceed {hi} = {N(b)}");
    }

    internal static void StrictlyOrdered(List<string> p, string model, string lo, double a, string hi, double b)
    {
        if (double.IsFinite(a) && double.IsFinite(b) && !(a < b)) p.Add($"{model} {lo} = {N(a)} must be below {hi} = {N(b)}");
    }

    /// <summary>A four-point piecewise-linear table: finite, breakpoints in non-decreasing order.</summary>
    internal static void Table(List<string> p, string model, string xName, IReadOnlyList<double> x, string yName, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count || x.Count < 2) { p.Add($"{model} {xName}/{yName}: a table needs at least two (x, y) pairs of equal count"); return; }
        for (int i = 0; i < x.Count; i++)
        {
            Finite(p, model, $"{xName}{i + 1}", x[i]);
            Finite(p, model, $"{yName}{i + 1}", y[i]);
            if (i > 0 && double.IsFinite(x[i]) && double.IsFinite(x[i - 1]) && x[i] < x[i - 1])
                p.Add($"{model} {xName}{i + 1} = {N(x[i])} is below {xName}{i} = {N(x[i - 1])}: table breakpoints must not decrease");
        }
    }
}

/// <summary>
/// REGC_A (PSS/E REGCA1): the renewable energy generator/converter interface. The current commands pass through the
/// converter lag Tg (with the reactive-current recovery rate limits Iqrmax/Iqrmin and the active-current rise limit
/// Rrpwr and the low-voltage power logic LVPL on the active current), and the network interface applies the
/// low-voltage active current management (LVACM, Lvpnt0..Lvpnt1) and the high-voltage reactive current management
/// (HVRCM, Volim, Khv, Iolim). EPRI 3002006525 Fig. D-4.
/// </summary>
public sealed record RegcaParameters
{
    /// <summary>LVPL switch: 1 enables the low-voltage power logic, 0 disables it.</summary>
    public int Lvplsw { get; init; } = 1;
    /// <summary>Converter time constant (s); 0 = instantaneous.</summary>
    public double Tg { get; init; } = 0.02;
    /// <summary>Active current up-ramp rate limit (pu/s, on Ip), the post-disturbance recovery rate.</summary>
    public double Rrpwr { get; init; } = 10.0;
    /// <summary>LVPL breakpoint voltage (pu): above it the LVPL places no limit.</summary>
    public double Brkpt { get; init; } = 0.9;
    /// <summary>LVPL zero crossing voltage (pu): at or below it the LVPL limits Ip to zero.</summary>
    public double Zerox { get; init; } = 0.4;
    /// <summary>LVPL current at the breakpoint (pu).</summary>
    public double Lvpl1 { get; init; } = 1.22;
    /// <summary>HVRCM voltage limit (pu).</summary>
    public double Volim { get; init; } = 1.2;
    /// <summary>LVACM: voltage at and above which the active current is unrestricted (pu).</summary>
    public double Lvpnt1 { get; init; } = 0.8;
    /// <summary>LVACM: voltage at and below which the active current is zero (pu).</summary>
    public double Lvpnt0 { get; init; } = 0.4;
    /// <summary>HVRCM lower limit on the reactive current exported (pu, negative: absorbing at most |Iolim|).</summary>
    public double Iolim { get; init; } = -1.3;
    /// <summary>Terminal voltage filter for the LVPL (s).</summary>
    public double Tfltr { get; init; } = 0.02;
    /// <summary>HVRCM overvoltage compensation gain (pu current per pu voltage).</summary>
    public double Khv { get; init; } = 0.7;
    /// <summary>Upper rate limit on the network-convention Iq (pu/s), active only when the initial Q is positive.</summary>
    public double Iqrmax { get; init; } = 9999.0;
    /// <summary>Lower rate limit on the network-convention Iq (pu/s), active only when the initial Q is negative.</summary>
    public double Iqrmin { get; init; } = -9999.0;
    /// <summary>PSS/E network-solution acceleration factor of the converter angle (0..1). Not a model dynamic: GridSim
    /// solves the converter / network fixed point to 1e-10 pu each step, so its converged answer does not depend on it.
    /// Range-checked and reported in the DYR notes, never silently dropped.</summary>
    public double Accel { get; init; } = 0.7;

    /// <summary>Every problem with these values (empty when they are usable).</summary>
    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        const string m = "REGC_A";
        WeccCheck.Flag(p, m, "Lvplsw", Lvplsw, 0, 1);
        WeccCheck.AtLeast(p, m, "Tg", Tg, 0.0);
        WeccCheck.Above(p, m, "Rrpwr", Rrpwr, 0.0);
        WeccCheck.Finite(p, m, "Brkpt", Brkpt);
        WeccCheck.Finite(p, m, "Zerox", Zerox);
        if (Lvplsw == 1)
        {
            WeccCheck.StrictlyOrdered(p, m, "Zerox", Zerox, "Brkpt", Brkpt);
            WeccCheck.AtLeast(p, m, "Lvpl1", Lvpl1, 0.0);
        }
        else WeccCheck.Finite(p, m, "Lvpl1", Lvpl1);
        WeccCheck.Above(p, m, "Volim", Volim, 0.0);
        WeccCheck.AtLeast(p, m, "Lvpnt0", Lvpnt0, 0.0);
        WeccCheck.StrictlyOrdered(p, m, "Lvpnt0", Lvpnt0, "Lvpnt1", Lvpnt1);
        WeccCheck.Finite(p, m, "Iolim", Iolim);
        if (double.IsFinite(Iolim) && Iolim > 0.0) p.Add($"{m} Iolim = {WeccCheck.N(Iolim)}: must be 0 or negative (it is the lower limit on the exported reactive current)");
        WeccCheck.AtLeast(p, m, "Tfltr", Tfltr, 0.0);
        WeccCheck.AtLeast(p, m, "Khv", Khv, 0.0);
        WeccCheck.Above(p, m, "Iqrmax", Iqrmax, 0.0);
        if (!double.IsFinite(Iqrmin) || !(Iqrmin < 0.0)) p.Add($"{m} Iqrmin = {WeccCheck.N(Iqrmin)}: must be a finite number below 0");
        if (!double.IsFinite(Accel) || Accel < 0.0 || Accel > 1.0) p.Add($"{m} Accel = {WeccCheck.N(Accel)}: must be in [0, 1]");
        return p;
    }
}

/// <summary>Which renewable energy electrical control model a <see cref="ReecParameters"/> set describes. REEC_B and
/// REEC_C are documented as subsets of REEC_A (EPRI 3002006525 section 3.3), REEC_C adding the storage path.</summary>
public enum ReecVariant
{
    /// <summary>REEC_A (PSS/E REECA1): wind and PV; VDL tables, the Iqfrz/Thld state machine, Thld2, PFlag.</summary>
    A,
    /// <summary>REEC_B (PSS/E REECB1): the simpler PV control; no VDL tables, no state machine, no PFlag.</summary>
    B,
    /// <summary>REEC_C (PSS/E REECCU1): battery storage; VDL tables, state of charge, Ipmin = -Ipmax.</summary>
    C,
}

/// <summary>
/// REEC_A / REEC_B / REEC_C: the renewable energy electrical controls, which turn the active and reactive power
/// references into the current commands Ipcmd and Iqcmd for the REGC model. EPRI 3002006525 Figs. D-1, D-2, D-3.
/// Fields a variant does not have are ignored by it and must stay at their defaults (the DYR reader never sets them).
/// </summary>
public sealed record ReecParameters
{
    public ReecVariant Variant { get; init; } = ReecVariant.A;
    /// <summary>Remote bus for the voltage measurement (0 = own terminal). Only 0 is modelled.</summary>
    public int RemoteBus { get; init; }
    /// <summary>1 = constant power factor (pfaref from the initial P and Q), 0 = Q control.</summary>
    public int PfFlag { get; init; }
    /// <summary>1 = Q control through the Kqp/Kqi PI, 0 = voltage control (reference Vref1 + Qref, REEC_A).</summary>
    public int VFlag { get; init; } = 1;
    /// <summary>1 = voltage / Q control through the Kvp/Kvi PI, 0 = constant power factor or Q (Tiq lag).</summary>
    public int QFlag { get; init; }
    /// <summary>REEC_A only: 1 = the power reference is multiplied by the generator speed (type 3 wind), 0 = not.</summary>
    public int PFlag { get; init; }
    /// <summary>0 = reactive current priority, 1 = active current priority.</summary>
    public int PqFlag { get; init; }
    /// <summary>Voltage below which the dip logic is active (pu).</summary>
    public double Vdip { get; init; } = 0.9;
    /// <summary>Voltage above which the dip logic is active (pu).</summary>
    public double Vup { get; init; } = 1.1;
    /// <summary>Terminal voltage filter (s).</summary>
    public double Trv { get; init; } = 0.02;
    /// <summary>Voltage error deadband lower threshold (pu, 0 or negative).</summary>
    public double Dbd1 { get; init; } = -0.05;
    /// <summary>Voltage error deadband upper threshold (pu, 0 or positive).</summary>
    public double Dbd2 { get; init; } = 0.05;
    /// <summary>Reactive current injection gain in a dip (pu current / pu voltage).</summary>
    public double Kqv { get; init; } = 2.0;
    /// <summary>Upper limit on the injected reactive current Iqinj (pu).</summary>
    public double Iqh1 { get; init; } = 1.05;
    /// <summary>Lower limit on the injected reactive current Iqinj (pu).</summary>
    public double Iql1 { get; init; } = -1.05;
    /// <summary>Voltage reference of the dip injection (pu); 0 = the initial terminal voltage (PSS/E convention).</summary>
    public double Vref0 { get; init; }
    /// <summary>REEC_A: Iqinj value held after the dip for Thld seconds when Thld &gt; 0 (pu).</summary>
    public double Iqfrz { get; init; }
    /// <summary>REEC_A: post-dip hold (s): &gt; 0 holds Iqinj = Iqfrz, &lt; 0 keeps Iqinj = Iqv, 0 ends at once.</summary>
    public double Thld { get; init; }
    /// <summary>REEC_A: time (s) the active current limit Ipmax stays at its dip value after the dip ends.</summary>
    public double Thld2 { get; init; }
    /// <summary>Electrical power filter for the power factor path (s).</summary>
    public double Tp { get; init; } = 0.05;
    public double Qmax { get; init; } = 0.4;
    public double Qmin { get; init; } = -0.4;
    public double Vmax { get; init; } = 1.1;
    public double Vmin { get; init; } = 0.9;
    public double Kqp { get; init; }
    public double Kqi { get; init; } = 0.1;
    public double Kvp { get; init; }
    public double Kvi { get; init; } = 40.0;
    /// <summary>REEC_A: user voltage bias added to Qref when VFlag = 0 (pu).</summary>
    public double Vref1 { get; init; }
    /// <summary>Reactive current lag (s), the QFlag = 0 path.</summary>
    public double Tiq { get; init; } = 0.02;
    /// <summary>Power reference up-ramp limit (pu/s).</summary>
    public double DPmax { get; init; } = 99.0;
    /// <summary>Power reference down-ramp limit (pu/s, negative).</summary>
    public double DPmin { get; init; } = -99.0;
    public double Pmax { get; init; } = 1.0;
    public double Pmin { get; init; }
    /// <summary>Converter current limit (pu).</summary>
    public double Imax { get; init; } = 1.3;
    /// <summary>Power order lag (s).</summary>
    public double Tpord { get; init; } = 0.02;
    /// <summary>VDL1 (reactive current limit vs Vt_filt) voltages and currents, four points (REEC_A, REEC_C).</summary>
    public IReadOnlyList<double> Vq { get; init; } = new[] { 0.0, 0.2, 0.5, 1.0 };
    public IReadOnlyList<double> Iq { get; init; } = new[] { 1.3, 1.3, 1.3, 1.3 };
    /// <summary>VDL2 (active current limit vs Vt_filt) voltages and currents, four points (REEC_A, REEC_C).</summary>
    public IReadOnlyList<double> Vp { get; init; } = new[] { 0.0, 0.2, 0.5, 1.0 };
    public IReadOnlyList<double> Ip { get; init; } = new[] { 1.3, 1.3, 1.3, 1.3 };
    /// <summary>REEC_C: discharge time constant T (s): SOC = SOCini - (1/T) integral of Pgen dt.</summary>
    public double T { get; init; } = 999.0;
    /// <summary>REEC_C: initial state of charge (fraction).</summary>
    public double SocIni { get; init; } = 0.5;
    /// <summary>REEC_C: state of charge at and above which charging stops (Ipmin = 0).</summary>
    public double SocMax { get; init; } = 0.8;
    /// <summary>REEC_C: state of charge at and below which discharging stops (Ipmax = 0).</summary>
    public double SocMin { get; init; } = 0.2;

    /// <summary>The WECC name of the variant.</summary>
    public string Name => Variant switch { ReecVariant.A => "REEC_A", ReecVariant.B => "REEC_B", _ => "REEC_C" };

    /// <summary>True when the variant has the VDL1 / VDL2 tables.</summary>
    public bool HasVdl => Variant != ReecVariant.B;

    /// <summary>Every problem with these values (empty when they are usable).</summary>
    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        string m = Name;
        if (RemoteBus != 0) p.Add($"{m} remote voltage bus {RemoteBus}: only the unit's own terminal (0) is modelled");
        WeccCheck.Flag(p, m, "PfFlag", PfFlag, 0, 1);
        WeccCheck.Flag(p, m, "VFlag", VFlag, 0, 1);
        WeccCheck.Flag(p, m, "QFlag", QFlag, 0, 1);
        WeccCheck.Flag(p, m, "PqFlag", PqFlag, 0, 1);
        if (Variant == ReecVariant.A) WeccCheck.Flag(p, m, "PFlag", PFlag, 0, 1);
        else if (PFlag != 0) p.Add($"{m} has no PFlag (only REEC_A multiplies the power reference by speed)");
        WeccCheck.Finite(p, m, "Vdip", Vdip);
        WeccCheck.Finite(p, m, "Vup", Vup);
        WeccCheck.StrictlyOrdered(p, m, "Vdip", Vdip, "Vup", Vup);
        WeccCheck.AtLeast(p, m, "Trv", Trv, 0.0);
        WeccCheck.Finite(p, m, "dbd1", Dbd1);
        WeccCheck.Finite(p, m, "dbd2", Dbd2);
        if (double.IsFinite(Dbd1) && Dbd1 > 0.0) p.Add($"{m} dbd1 = {WeccCheck.N(Dbd1)}: the lower deadband threshold must be 0 or negative");
        if (double.IsFinite(Dbd2) && Dbd2 < 0.0) p.Add($"{m} dbd2 = {WeccCheck.N(Dbd2)}: the upper deadband threshold must be 0 or positive");
        WeccCheck.Finite(p, m, "Kqv", Kqv);
        WeccCheck.Finite(p, m, "Iqh1", Iqh1);
        WeccCheck.Finite(p, m, "Iql1", Iql1);
        WeccCheck.Ordered(p, m, "Iql1", Iql1, "Iqh1", Iqh1);
        WeccCheck.AtLeast(p, m, "Vref0", Vref0, 0.0);
        if (Variant == ReecVariant.A)
        {
            WeccCheck.Finite(p, m, "Iqfrz", Iqfrz);
            WeccCheck.Finite(p, m, "Thld", Thld);
            WeccCheck.AtLeast(p, m, "Thld2", Thld2, 0.0);
            WeccCheck.Finite(p, m, "Vref1", Vref1);
        }
        else if (Iqfrz != 0 || Thld != 0 || Thld2 != 0 || Vref1 != 0)
            p.Add($"{m} has no Iqfrz, Thld, Thld2 or Vref1 (REEC_A only)");
        WeccCheck.AtLeast(p, m, "Tp", Tp, 0.0);
        WeccCheck.Finite(p, m, "Qmax", Qmax);
        WeccCheck.Finite(p, m, "Qmin", Qmin);
        WeccCheck.Ordered(p, m, "Qmin", Qmin, "Qmax", Qmax);
        WeccCheck.Finite(p, m, "Vmax", Vmax);
        WeccCheck.Finite(p, m, "Vmin", Vmin);
        WeccCheck.Ordered(p, m, "Vmin", Vmin, "Vmax", Vmax);
        WeccCheck.Finite(p, m, "Kqp", Kqp);
        WeccCheck.Finite(p, m, "Kqi", Kqi);
        WeccCheck.Finite(p, m, "Kvp", Kvp);
        WeccCheck.Finite(p, m, "Kvi", Kvi);
        WeccCheck.AtLeast(p, m, "Tiq", Tiq, 0.0);
        WeccCheck.Above(p, m, "dPmax", DPmax, 0.0);
        if (!double.IsFinite(DPmin) || !(DPmin < 0.0)) p.Add($"{m} dPmin = {WeccCheck.N(DPmin)}: must be a finite number below 0");
        WeccCheck.Finite(p, m, "Pmax", Pmax);
        WeccCheck.Finite(p, m, "Pmin", Pmin);
        WeccCheck.Ordered(p, m, "Pmin", Pmin, "Pmax", Pmax);
        WeccCheck.Above(p, m, "Imax", Imax, 0.0);
        WeccCheck.AtLeast(p, m, "Tpord", Tpord, 0.0);
        if (HasVdl)
        {
            WeccCheck.Table(p, m, "Vq", Vq, "Iq", Iq);
            WeccCheck.Table(p, m, "Vp", Vp, "Ip", Ip);
        }
        if (Variant == ReecVariant.C)
        {
            WeccCheck.Above(p, m, "T", T, 0.0);
            WeccCheck.Finite(p, m, "SOCini", SocIni);
            WeccCheck.Finite(p, m, "SOCmax", SocMax);
            WeccCheck.Finite(p, m, "SOCmin", SocMin);
            WeccCheck.StrictlyOrdered(p, m, "SOCmin", SocMin, "SOCmax", SocMax);
        }
        return p;
    }
}

/// <summary>
/// The WECC dynamic model chain of one converter-interfaced unit, as bound from a DYR file (or built by a test): the
/// generator/converter model (REGC_A), its electrical control (REEC_A/B/C) and its plant controller (REPC_A,
/// <see cref="Repc"/>). Later slices add the wind turbine mechanics and DER_A. <see cref="Source"/> names where the data
/// came from, for reports.
/// </summary>
public sealed record WeccPlant(RegcaParameters? Regc, ReecParameters? Reec = null, string Source = "")
{
    /// <summary>The aggregated distributed energy resource model DER_A, which stands alone (no REGC, REEC, REPC or wind
    /// models). Exactly one of <see cref="Regc"/> and <see cref="Der"/> is set.</summary>
    public DeraParameters? Der { get; init; }

    /// <summary>A DER_A unit.</summary>
    public static WeccPlant DerA(DeraParameters p, string source = "") => new(null, null, source) { Der = p };

    /// <summary>The plant controller (REPC_A), or null (the REEC model's Pref and Qext are then held constant).</summary>
    public RepcaParameters? Repc { get; init; }

    /// <summary>The wind turbine mechanics (WTGT_A, and for a type 3 turbine WTGAR_A, WTGPT_A and WTGTRQ_A), or null.</summary>
    public WindParameters? Wind { get; init; }

    /// <summary>REGC_B, the voltage-source generator/converter, in place of REGC_A (exactly one of the two is set for a
    /// REGC-anchored unit).</summary>
    public RegcbParameters? RegcB { get; init; }

    /// <summary>REEC_D, the electrical control, in place of REEC_A/B/C (at most one of <see cref="Reec"/> and this).</summary>
    public ReecdParameters? ReecD { get; init; }

    /// <summary>REGFM_A1, the droop grid-forming inverter, which stands alone (no REGC, REEC, REPC, wind or DER_A).</summary>
    public RegfmA1Parameters? Gfm { get; init; }

    /// <summary>A REGFM_A1 unit.</summary>
    public static WeccPlant RegfmA1(RegfmA1Parameters p, string source = "") => new(null, null, source) { Gfm = p };

    /// <summary>The impedance (machine base) behind which the unit is a voltage source, stamped on its bus as a Norton
    /// admittance (REGC_B: re + j Xe; REGFM_A1: j XL); null for a current-source unit (REGC_A, DER_A).</summary>
    public System.Numerics.Complex? NortonImpedance => Gfm is not null ? new System.Numerics.Complex(0.0, Gfm.XL)
        : RegcB is not null ? new System.Numerics.Complex(RegcB.Re, RegcB.Xe) : null;

    /// <summary>The model chain in words, for reports ("REGC_A + REEC_A + REPC_A + WTGT_A").</summary>
    public string Chain => Der is not null ? "DER_A" : Gfm is not null ? "REGFM_A1"
        : (RegcB is not null ? "REGC_B" : "REGC_A") + (ReecD is not null ? " + REEC_D" : Reec is null ? "" : $" + {Reec.Name}")
            + (Repc is null ? "" : " + REPC_A") + (Wind is null ? "" : " + " + Wind.Chain);

    /// <summary>Every problem with the chain and its values (empty when usable).</summary>
    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        if (Der is not null)
        {
            if (Regc is not null || Reec is not null || Repc is not null || Wind is not null)
                p.Add("DER_A stands alone: a unit takes either DER_A or the REGC_A model chain, not both");
            if (RegcB is not null || ReecD is not null || Gfm is not null)
                p.Add("DER_A stands alone: a unit takes either DER_A or the REGC_A model chain, not both");
            p.AddRange(Der.Check());
            return p;
        }
        if (Gfm is not null)
        {
            if (Regc is not null || RegcB is not null || Reec is not null || ReecD is not null || Repc is not null || Wind is not null)
                p.Add("REGFM_A1 stands alone: a grid-forming unit takes no REGC, REEC, REPC or wind model");
            p.AddRange(Gfm.Check());
            return p;
        }
        if (Regc is null && RegcB is null) { p.Add("a WECC converter unit needs a REGC_A generator/converter model (or REGC_B, DER_A or REGFM_A1)"); return p; }
        if (Regc is not null && RegcB is not null) p.Add("a unit takes one generator/converter model: REGC_A or REGC_B, not both");
        if (Regc is not null) p.AddRange(Regc.Check());
        if (RegcB is not null) p.AddRange(RegcB.Check());
        if (Reec is not null && ReecD is not null) p.Add("a unit takes one electrical control model: REEC_A/B/C or REEC_D, not both");
        if (Reec is not null) p.AddRange(Reec.Check());
        if (ReecD is not null) p.AddRange(ReecD.Check());
        if (Repc is not null)
        {
            if (Reec is null && ReecD is null) p.Add("REPC_A needs an electrical control model (REEC_A/B/C) to drive (or REEC_D)");
            p.AddRange(Repc.Check());
        }
        if (Wind is not null)
        {
            if (Reec is null || Reec.Variant != ReecVariant.A)
                p.Add("wind turbine mechanics need the REEC_A electrical control (the only one that takes the generator speed)");
            else if (Wind.IsType3 && Reec.PFlag != 1)
                p.Add("a type 3 turbine (WTGTRQ_A) needs REEC_A PFlag = 1: the torque controller gives a torque, which REEC_A turns into "
                    + "power by multiplying by the generator speed (EPRI 3002006525 section 3.3)");
            p.AddRange(Wind.Check());
        }
        return p;
    }
}

/// <summary>
/// Where a WECC plant controller measures (REPC_A): its regulated bus and the branch whose flow is the plant output,
/// resolved against the case when the RMS study is prepared. Bus 0 means the unit's own terminal (and no branch: the
/// unit's own P, Q and current). The branch current at the from end is <c>I = Y11 V(from) + Y12 V(to)</c> (system per unit,
/// the branch's own pi-model stamp with its tap and phase shift, oriented from BUS1 to BUS2). The initial voltages are the
/// solved power flow's, for the initialisation.
/// </summary>
public sealed record WeccMeasurement(
    int RegulatedBus, int FromBus, int ToBus, System.Numerics.Complex Y11, System.Numerics.Complex Y12,
    System.Numerics.Complex RegulatedV0, System.Numerics.Complex FromV0, System.Numerics.Complex ToV0)
{
    /// <summary>True when the plant output is a branch flow (BUS1 and BUS2 given).</summary>
    public bool HasBranch => FromBus != 0;

    /// <summary>The extra network buses this measurement reads (non-zero, distinct, in the order regulated, from, to).</summary>
    public IEnumerable<int> Buses()
    {
        var seen = new HashSet<int>();
        foreach (int b in new[] { RegulatedBus, FromBus, ToBus })
            if (b != 0 && seen.Add(b)) yield return b;
    }
}
