using GridSim.Core.Model;

namespace GridSim.Core.Qsts;

// ---------------------------------------------------------------------------------------------------------------------
// Quasi-static time series (QSTS): the study definition.
//
// A QSTS study is a time-stepped sequence of power flows: at each instant t_k = k * StepSeconds the base case is scaled
// by its load and generation profiles, the inverter-based resources and storage units are folded in as injections, the
// controllers (tap changers, capacitor banks, inverter functions, storage dispatch) act, and the regulated state is
// recorded. The records in this file are the study's INPUT: what to vary over time and how each controller behaves.
// They are plain immutable records; QstsStudy.Validate checks every rule against the case and throws one
// InvalidDataException listing every problem (fail loud: a study that cannot mean what it says is never run).
//
// Units: time in whole seconds (so step instants are exact integers), power in MW / MVAr, voltage in per unit,
// impedance in per unit on the system base, energy in MWh.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>How the controllers act within a step.</summary>
public enum QstsControlMode
{
    /// <summary>Each step is solved by the existing steady-state loops (the OLTC tap loop of
    /// <see cref="PowerFlow.SteadyStateSolver"/>, around <see cref="PowerFlow.SwitchedShuntPowerFlow"/> when capacitor
    /// banks are given): controls settle instantly and have no time behaviour. Tap positions and bank steps are carried
    /// from step to step. Inverter volt-var / volt-watt and peak-shave storage need the timed framework and are refused.</summary>
    Steady,

    /// <summary>The time-aware control framework: controllers with deadbands, delays, dead times, response times and
    /// operation counting (<see cref="QstsEngine"/>).</summary>
    Timed,
}

/// <summary>How a profile is read between its samples.</summary>
public enum QstsInterpolation
{
    /// <summary>Each sample holds until the next one (zero-order hold): the usual reading of half-hourly settlement data.</summary>
    Hold,
    /// <summary>Straight line between neighbouring samples.</summary>
    Linear,
}

/// <summary>
/// A named time series of multipliers (or MW values, depending on who reads it), sampled every
/// <see cref="StepSeconds"/> from t = 0. <see cref="ValueAt"/> reads it at a whole-second instant.
/// </summary>
/// <param name="Name">The name the bindings refer to.</param>
/// <param name="StepSeconds">Seconds between samples (a whole number above zero).</param>
/// <param name="Values">The samples; value i is at t = i * StepSeconds.</param>
/// <param name="Interpolation">Hold (default) or linear between samples.</param>
/// <param name="Wrap">True to repeat the profile when the study runs past its end (a daily shape over several days);
/// false (default) refuses a study longer than the profile.</param>
public sealed record QstsProfile(string Name, int StepSeconds, IReadOnlyList<double> Values,
    QstsInterpolation Interpolation = QstsInterpolation.Hold, bool Wrap = false)
{
    /// <summary>The value at <paramref name="tSeconds"/> (whole seconds from the start). Integer arithmetic locates the
    /// sample, so the reading is exact and the same on every machine: hold returns sample floor(t / step); linear returns
    /// v[i] + f * (v[i+1] - v[i]) with f = (t - i * step) / step, and exactly v[i] when f is 0.</summary>
    public double ValueAt(long tSeconds)
    {
        if (tSeconds < 0) throw new ArgumentOutOfRangeException(nameof(tSeconds), tSeconds, "time must be zero or more");
        long n = Values.Count;
        long i = tSeconds / StepSeconds;
        long rem = tSeconds - i * StepSeconds;
        if (Interpolation == QstsInterpolation.Hold || rem == 0)
            return Values[(int)Index(i, n)];
        double f = rem / (double)StepSeconds;
        double a = Values[(int)Index(i, n)], b = Values[(int)Index(i + 1, n)];
        return a + f * (b - a);
    }

    private long Index(long i, long n)
    {
        if (i < n) return i;
        if (Wrap) return i % n;
        throw new InvalidOperationException($"profile '{Name}' has {n} samples and was read at sample {i} (set \"wrap\": true to repeat it).");
    }

    /// <summary>The last whole-second instant this profile can be read at without wrapping.</summary>
    public long CoversUntilSeconds => Interpolation == QstsInterpolation.Hold
        ? (long)Values.Count * StepSeconds - 1
        : (long)(Values.Count - 1) * StepSeconds;
}

/// <summary>Scale the demand (Pd, Qd) of some buses by a profile: Pd(t) = Pd * m(t).</summary>
/// <param name="Buses">The bus ids, or null for every bus with demand.</param>
/// <param name="Profile">The profile name.</param>
/// <param name="ScaleP">Scale Pd (default true).</param>
/// <param name="ScaleQ">Scale Qd (default true).</param>
public sealed record QstsLoadBinding(IReadOnlyList<int>? Buses, string Profile, bool ScaleP = true, bool ScaleQ = true);

/// <summary>Drive a generator's Pg by a profile: multiply (Pg(t) = Pg * m(t)) or set in MW (Pg(t) = m(t)).</summary>
/// <param name="Generator">Index into the model's in-service generators (from 0).</param>
/// <param name="Profile">The profile name.</param>
/// <param name="AbsoluteMw">True: the profile value IS the MW; false (default): a multiplier on the case Pg.</param>
public sealed record QstsGeneratorBinding(int Generator, string Profile, bool AbsoluteMw = false);

/// <summary>An added load (electric vehicle charging, heat pumps): Pd += Mw * m(t), Qd += the matching MVAr at
/// <see cref="PowerFactor"/> (lagging).</summary>
public sealed record QstsLoadAddition(string Name, int Bus, double Mw, string? Profile, double PowerFactor = 1.0);

/// <summary>The inverter control function of a PV unit.</summary>
public enum InverterFunction
{
    /// <summary>Unity power factor: Q = 0.</summary>
    Unity,
    /// <summary>Fixed power factor (IEEE 1547-2018 5.3.2 constant power factor mode).</summary>
    FixedPowerFactor,
    /// <summary>Volt-var (IEEE 1547-2018 5.3.3).</summary>
    VoltVar,
    /// <summary>Volt-watt (IEEE 1547-2018 5.4.2).</summary>
    VoltWatt,
    /// <summary>Volt-var and volt-watt together.</summary>
    VoltVarVoltWatt,
}

/// <summary>Which limit gives way when P and Q together exceed the apparent power rating.</summary>
public enum InverterPriority
{
    /// <summary>Reactive power first: Q per the function (capped at the rating), P curtailed to sqrt(S^2 - Q^2).</summary>
    Var,
    /// <summary>Active power first: P as available, Q capped at sqrt(S^2 - P^2).</summary>
    Watt,
}

/// <summary>A piecewise-linear volt-var curve: Q as a fraction of the rated apparent power (positive = injecting,
/// capacitive) at four voltages V1 &lt; V2 &lt;= V3 &lt; V4; flat outside [V1, V4].</summary>
public sealed record VoltVarCurve(double V1, double Q1, double V2, double Q2, double V3, double Q3, double V4, double Q4)
{
    /// <summary>IEEE 1547-2018 Table 8, Category A default: (0.90, +0.25), (1.00, 0), (1.00, 0), (1.10, -0.25).</summary>
    public static VoltVarCurve Ieee1547CategoryA => new(0.90, 0.25, 1.00, 0.0, 1.00, 0.0, 1.10, -0.25);

    /// <summary>IEEE 1547-2018 Table 8, Category B default with VRef = 1.0 pu: (0.92, +0.44), (0.98, 0), (1.02, 0),
    /// (1.08, -0.44).</summary>
    public static VoltVarCurve Ieee1547CategoryB => new(0.92, 0.44, 0.98, 0.0, 1.02, 0.0, 1.08, -0.44);

    /// <summary>Q (fraction of rated apparent power) at voltage <paramref name="v"/>. Exactly the point value at each break
    /// point; linear between them: Qa + (v - Va) * (Qb - Qa) / (Vb - Va).</summary>
    public double At(double v)
    {
        if (v <= V1) return Q1;
        if (v >= V4) return Q4;
        if (v < V2) return Q1 + (v - V1) * (Q2 - Q1) / (V2 - V1);
        if (v <= V3) return V3 == V2 ? Q2 : Q2 + (v - V2) * (Q3 - Q2) / (V3 - V2);
        return Q3 + (v - V3) * (Q4 - Q3) / (V4 - V3);
    }
}

/// <summary>A volt-watt curve: the active power cap as a fraction of rated active power, P1 at and below V1 falling
/// linearly to P2 at and above V2.</summary>
public sealed record VoltWattCurve(double V1, double P1, double V2, double P2)
{
    /// <summary>IEEE 1547-2018 Table 10 default for a DER that cannot absorb active power, with P'min = 0:
    /// (1.06, 1.0), (1.10, 0.0) (P2 is the lesser of 0.2 Prated and Pmin, which is 0 for PV).</summary>
    public static VoltWattCurve Ieee1547Default => new(1.06, 1.0, 1.10, 0.0);

    /// <summary>The cap (fraction of rated P) at voltage <paramref name="v"/>; exactly P1 / P2 at the break points.</summary>
    public double At(double v)
    {
        if (v <= V1) return P1;
        if (v >= V2) return P2;
        return P1 + (v - V1) * (P2 - P1) / (V2 - V1);
    }
}

/// <summary>The inverter settings of a PV unit.</summary>
/// <param name="Function">Unity, fixed power factor, volt-var, volt-watt, or both.</param>
/// <param name="PowerFactor">For fixed power factor: the magnitude (0 &lt; pf &lt;= 1).</param>
/// <param name="Absorbing">For fixed power factor: true = absorbing reactive power (inductive; the usual setting to
/// offset the voltage rise of export), false = injecting.</param>
/// <param name="VoltVar">The volt-var curve (Category B default).</param>
/// <param name="VoltWatt">The volt-watt curve (IEEE 1547 default).</param>
/// <param name="ResponseSeconds">Open-loop response time: the time to reach 90 % of a step change of the function's
/// output (IEEE 1547-2018 Table 8: 5 s default for Category B, 10 s for Category A; Table 10: 10 s for volt-watt).
/// 0 = instant.</param>
/// <param name="Priority">Var (default) or watt priority within the rating.</param>
/// <param name="VoltageBus">The bus whose voltage the function reads; null = the unit's own bus.</param>
public sealed record InverterSettings(
    InverterFunction Function = InverterFunction.Unity,
    double PowerFactor = 1.0,
    bool Absorbing = true,
    VoltVarCurve? VoltVar = null,
    VoltWattCurve? VoltWatt = null,
    double ResponseSeconds = 0.0,
    InverterPriority Priority = InverterPriority.Var,
    int? VoltageBus = null)
{
    /// <summary>The volt-var curve in force (Category B when none is given).</summary>
    public VoltVarCurve VoltVarCurveOrDefault => VoltVar ?? VoltVarCurve.Ieee1547CategoryB;

    /// <summary>The volt-watt curve in force (the IEEE 1547 default when none is given).</summary>
    public VoltWattCurve VoltWattCurveOrDefault => VoltWatt ?? VoltWattCurve.Ieee1547Default;
}

/// <summary>A PV (or any inverter-based generation) unit, folded into its bus as a negative load P + jQ.</summary>
/// <param name="Name">Unique name (for reports and the operation log).</param>
/// <param name="Bus">The bus it connects to.</param>
/// <param name="RatedMw">Active power at profile value 1.0 (MW).</param>
/// <param name="RatedMva">Apparent power rating (MVA, at least RatedMw).</param>
/// <param name="Profile">The availability profile (fraction of RatedMw); null = always 1.0.</param>
/// <param name="Inverter">The control function.</param>
public sealed record QstsPvUnit(string Name, int Bus, double RatedMw, double RatedMva, string? Profile, InverterSettings Inverter);

/// <summary>Storage dispatch modes.</summary>
public enum StorageMode
{
    /// <summary>Follow a MW schedule (a profile in MW, discharge positive).</summary>
    Schedule,
    /// <summary>Hold the active power through a monitored branch between LowMw and HighMw: discharge the excess above
    /// HighMw, charge into the room below LowMw.</summary>
    PeakShave,
    /// <summary>Self-consumption: cover the net demand of its own bus (demand less PV there), charging from a surplus.</summary>
    SelfConsumption,
}

/// <summary>A storage unit: an energy reservoir with a power rating, SOC limits and one-way efficiencies, folded into
/// its bus as a negative load (discharge positive). Energy over a step: E(k+1) = E(k) + ChargeEfficiency * Pc * dt -
/// Pd / DischargeEfficiency * dt (dt in hours, power held over the step).</summary>
public sealed record QstsStorageUnit(
    string Name, int Bus, double RatedMw, double CapacityMwh, double InitialSoc, double SocMin, double SocMax,
    double ChargeEfficiency, double DischargeEfficiency, StorageMode Mode,
    string? ScheduleProfile = null, string? MonitoredBranch = null, double HighMw = 0, double LowMw = 0);

/// <summary>OLTC time characteristic.</summary>
public enum OltcCharacteristic
{
    /// <summary>Definite time: each operation waits its delay.</summary>
    Definite,
    /// <summary>Inverse time: the delay scales as half-band / |deviation| (a larger deviation acts sooner), never below
    /// <see cref="OltcTiming.MinDelaySeconds"/>.</summary>
    Inverse,
}

/// <summary>How the operations after the first in one excursion are timed.</summary>
public enum OltcSequence
{
    /// <summary>Continuous (non-sequential): after the first delay, further steps follow at the inter-tap delay
    /// until the voltage is back in band.</summary>
    Continuous,
    /// <summary>Sequential: the timer restarts after every operation, so each step waits the full delay.</summary>
    Sequential,
}

/// <summary>How several regulators share a control iteration.</summary>
public enum OltcCoordination
{
    /// <summary>Every ready regulator steps in the same iteration.</summary>
    Independent,
    /// <summary>Only the first ready regulator (in priority order) steps; the network is re-solved before the next.</summary>
    Sequential,
}

/// <summary>Timing and line drop compensation of one tap changer (or the default for all of them).</summary>
/// <param name="DelaySeconds">Initial time delay (s).</param>
/// <param name="InterTapSeconds">Delay between further steps in one excursion under continuous timing (s).</param>
/// <param name="Sequence">Continuous or sequential timing.</param>
/// <param name="Characteristic">Definite or inverse time.</param>
/// <param name="MinDelaySeconds">Inverse time: the shortest delay (s).</param>
/// <param name="LdcRPu">Line drop compensation resistance (pu on the system base); 0 = none.</param>
/// <param name="LdcXPu">Line drop compensation reactance (pu on the system base); 0 = none.</param>
/// <param name="Priority">Order under sequential coordination (lower first; ties by record order).</param>
public sealed record OltcTiming(
    double DelaySeconds = 0, double InterTapSeconds = 0, OltcSequence Sequence = OltcSequence.Continuous,
    OltcCharacteristic Characteristic = OltcCharacteristic.Definite, double MinDelaySeconds = 0,
    double LdcRPu = 0, double LdcXPu = 0, int Priority = 0);

/// <summary>Capacitor bank control.</summary>
public enum CapacitorControl
{
    /// <summary>Switch in below OnValue (pu), out above OffValue (pu) at the controlled bus.</summary>
    Voltage,
    /// <summary>Switch in when the reactive power into the monitored branch (MVAr, From end) is above OnValue, out when
    /// below OffValue.</summary>
    Var,
    /// <summary>In between OnValue and OffValue seconds of the day, out otherwise.</summary>
    Time,
}

/// <summary>A switched capacitor or reactor bank in equal steps (MVAr at 1 pu, positive = capacitive), as
/// <see cref="PowerFlow.SwitchedShunt"/>, with time behaviour.</summary>
public sealed record QstsCapacitor(
    string Name, int Bus, double StepMvar, int MinSteps, int MaxSteps, int InitialSteps, CapacitorControl Control,
    double OnValue, double OffValue, double DelaySeconds = 0, double OffDelaySeconds = 0, double DeadTimeSeconds = 0,
    int? ControlsBus = null, string? MonitoredBranch = null);

/// <summary>The limits a QSTS run judges its states against.</summary>
public sealed record QstsLimits(double VminPu = 0.94, double VmaxPu = 1.06, double LoadingPercent = 100.0, bool PerBusVoltageLimits = false);

/// <summary>The whole QSTS study.</summary>
public sealed record QstsStudy
{
    /// <summary>Seconds between steps (a whole number, at least 1).</summary>
    public required int StepSeconds { get; init; }

    /// <summary>Number of steps (at least 1). Step k is the instant k * StepSeconds.</summary>
    public required int Steps { get; init; }

    /// <summary>The second of the day at step 0 (0 = midnight); time-controlled capacitor banks read the time of day as
    /// (StartSecondOfDay + t) mod 86400.</summary>
    public int StartSecondOfDay { get; init; }

    /// <summary>Steady (existing loops) or timed (the new framework). Default timed.</summary>
    public QstsControlMode Mode { get; init; } = QstsControlMode.Timed;

    public IReadOnlyList<QstsProfile> Profiles { get; init; } = Array.Empty<QstsProfile>();
    public IReadOnlyList<QstsLoadBinding> Loads { get; init; } = Array.Empty<QstsLoadBinding>();
    public IReadOnlyList<QstsGeneratorBinding> Generators { get; init; } = Array.Empty<QstsGeneratorBinding>();
    public IReadOnlyList<QstsLoadAddition> LoadAdditions { get; init; } = Array.Empty<QstsLoadAddition>();
    public IReadOnlyList<QstsPvUnit> Pv { get; init; } = Array.Empty<QstsPvUnit>();
    public IReadOnlyList<QstsStorageUnit> Storage { get; init; } = Array.Empty<QstsStorageUnit>();
    public IReadOnlyList<QstsCapacitor> Capacitors { get; init; } = Array.Empty<QstsCapacitor>();

    /// <summary>Generation follows load: every generator off the slack bus without a profile of its own is scaled by
    /// the step's demand ratio and the dispatch is re-balanced (GridScenario.AtLoad's rule, the chain verb's), so a
    /// transmission case's slack does not absorb the whole demand swing. Default false (generation as the case).</summary>
    public bool GenerationFollowsLoad { get; init; }

    /// <summary>Whether the case's OLTC records regulate at all (default true). False locks every tap at its case value.</summary>
    public bool OltcEnabled { get; init; } = true;

    /// <summary>The timing every OLTC uses unless <see cref="OltcSettings"/> names it (default: instant, no LDC).</summary>
    public OltcTiming OltcDefault { get; init; } = new();

    /// <summary>Per-transformer timing, keyed by the OLTC record's transformer name.</summary>
    public IReadOnlyDictionary<string, OltcTiming> OltcSettings { get; init; } = new Dictionary<string, OltcTiming>();

    public OltcCoordination OltcCoordination { get; init; } = OltcCoordination.Independent;

    public QstsLimits Limits { get; init; } = new();

    /// <summary>Cap on discrete control iterations (re-solves after capacitor / tap operations) per step. Default 25,
    /// the steady-state tap loop's pass cap (<see cref="PowerFlow.SteadyStateSolver.DefaultMaxOltcPasses"/>).</summary>
    public int MaxControlIterations { get; init; } = PowerFlow.SteadyStateSolver.DefaultMaxOltcPasses;

    /// <summary>Cap on continuous (inverter / storage) fixed-point iterations per step.</summary>
    public int MaxContinuousIterations { get; init; } = 200;

    /// <summary>Continuous controllers are settled when no set point moved by more than this fraction of its rating.</summary>
    public double ContinuousTolerance { get; init; } = 1e-7;

    /// <summary>Branches (by name) whose active power at the From end is recorded at every step
    /// (<see cref="QstsStepRecord.MonitoredPMw"/>): the reverse-power check of the hosting-capacity study reads them.</summary>
    public IReadOnlyList<string> MonitorBranches { get; init; } = Array.Empty<string>();

    /// <summary>Keep every step's bus voltages and branch loadings in the result (default true). Off for very long runs.</summary>
    public bool RecordStates { get; init; } = true;

    /// <summary>The profile named <paramref name="name"/> (null when none is).</summary>
    public QstsProfile? Profile(string? name)
    {
        if (name is null) return null;
        foreach (QstsProfile p in Profiles) if (p.Name == name) return p;
        return null;
    }

    /// <summary>The timing of the OLTC on <paramref name="transformer"/>.</summary>
    public OltcTiming TimingFor(string transformer) =>
        OltcSettings.TryGetValue(transformer, out OltcTiming? t) ? t : OltcDefault;

    /// <summary>Check the study against <paramref name="model"/>. Throws one <see cref="InvalidDataException"/> that lists
    /// every problem; returns normally only when the study can run as written.</summary>
    public void Validate(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var p = new List<string>();
        if (StepSeconds < 1) p.Add($"stepSeconds must be a whole number of seconds, at least 1 (got {StepSeconds}).");
        if (Steps < 1) p.Add($"steps must be at least 1 (got {Steps}).");
        if (StartSecondOfDay < 0 || StartSecondOfDay >= 86400) p.Add($"startSecondOfDay must be in [0, 86400) (got {StartSecondOfDay}).");
        if (MaxControlIterations < 0) p.Add("maxControlIterations must be 0 or more.");
        if (MaxContinuousIterations < 1) p.Add("maxContinuousIterations must be at least 1.");
        if (!(ContinuousTolerance > 0) || !double.IsFinite(ContinuousTolerance)) p.Add("continuousTolerance must be a finite number above 0.");
        long lastT = (long)Math.Max(0, Steps - 1) * Math.Max(1, StepSeconds);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (QstsProfile pr in Profiles)
        {
            if (string.IsNullOrWhiteSpace(pr.Name)) p.Add("a profile has no name.");
            else if (!names.Add(pr.Name)) p.Add($"profile '{pr.Name}' is defined twice.");
            if (pr.StepSeconds < 1) p.Add($"profile '{pr.Name}': stepSeconds must be at least 1.");
            if (pr.Values.Count == 0) p.Add($"profile '{pr.Name}' has no values.");
            for (int i = 0; i < pr.Values.Count; i++)
                if (!double.IsFinite(pr.Values[i])) { p.Add($"profile '{pr.Name}': value {i} is not a finite number."); break; }
            if (pr.Values.Count > 0 && pr.StepSeconds >= 1 && !pr.Wrap && pr.CoversUntilSeconds < lastT)
                p.Add($"profile '{pr.Name}' covers {pr.CoversUntilSeconds} s but the study runs to {lastT} s (add values, or set \"wrap\": true).");
            if (pr.Interpolation == QstsInterpolation.Linear && pr.Values.Count < 2 && pr.Wrap is false && Steps > 1)
                p.Add($"profile '{pr.Name}': linear interpolation needs at least two values.");
        }
        void NeedProfile(string? name, string who, bool required)
        {
            if (name is null) { if (required) p.Add($"{who}: a profile is required."); return; }
            if (Profile(name) is null) p.Add($"{who}: profile '{name}' is not defined.");
        }

        var busIds = new HashSet<int>();
        var busById = new Dictionary<int, Bus>();
        foreach (Bus b in model.Buses) { busIds.Add(b.Id); busById.TryAdd(b.Id, b); }
        var branchNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Branch br in model.Branches) if (br.Name is not null) branchNames.Add(br.Name);

        var boundBuses = new HashSet<int>();
        bool allBound = false;
        for (int i = 0; i < Loads.Count; i++)
        {
            QstsLoadBinding lb = Loads[i];
            string who = $"loads entry {i + 1}";
            NeedProfile(lb.Profile, who, true);
            if (!lb.ScaleP && !lb.ScaleQ) p.Add($"{who}: scales neither P nor Q.");
            if (lb.Buses is null)
            {
                if (allBound || boundBuses.Count > 0) p.Add($"{who}: \"all\" overlaps another load binding (each bus takes one load profile).");
                allBound = true;
            }
            else
            {
                if (allBound) p.Add($"{who}: overlaps the \"all\" load binding (each bus takes one load profile).");
                foreach (int b in lb.Buses)
                {
                    if (!busIds.Contains(b)) p.Add($"{who}: bus {b} is not in the case.");
                    else if (!boundBuses.Add(b)) p.Add($"{who}: bus {b} already takes another load profile.");
                }
            }
        }
        var boundGens = new HashSet<int>();
        for (int i = 0; i < Generators.Count; i++)
        {
            QstsGeneratorBinding gb = Generators[i];
            string who = $"generators entry {i + 1}";
            NeedProfile(gb.Profile, who, true);
            if (gb.Generator < 0 || gb.Generator >= model.Generators.Count) p.Add($"{who}: generator index {gb.Generator} is outside 0..{model.Generators.Count - 1}.");
            else if (!boundGens.Add(gb.Generator)) p.Add($"{who}: generator {gb.Generator} already takes another profile.");
        }
        var devNames = new HashSet<string>(StringComparer.Ordinal);
        void Name(string n, string who)
        {
            if (string.IsNullOrWhiteSpace(n)) p.Add($"{who}: a name is required.");
            else if (!devNames.Add(n)) p.Add($"{who}: the name '{n}' is used twice (device names must be unique).");
        }
        void Pos(double v, string key, string who, bool allowZero = false)
        {
            if (!double.IsFinite(v) || (allowZero ? v < 0 : v <= 0)) p.Add($"{who}: {key} must be a finite number {(allowZero ? "of 0 or more" : "above 0")} (got {v}).");
        }
        void OnBus(int bus, string who)
        {
            if (!busIds.Contains(bus)) p.Add($"{who}: bus {bus} is not in the case.");
        }

        for (int i = 0; i < LoadAdditions.Count; i++)
        {
            QstsLoadAddition a = LoadAdditions[i];
            string who = $"load addition '{a.Name}'";
            Name(a.Name, who);
            OnBus(a.Bus, who);
            if (!double.IsFinite(a.Mw)) p.Add($"{who}: mw must be finite.");
            if (!(a.PowerFactor > 0 && a.PowerFactor <= 1)) p.Add($"{who}: powerFactor must be in (0, 1].");
            NeedProfile(a.Profile, who, false);
        }
        foreach (QstsPvUnit pv in Pv)
        {
            string who = $"pv '{pv.Name}'";
            Name(pv.Name, who);
            OnBus(pv.Bus, who);
            Pos(pv.RatedMw, "ratedMw", who);
            Pos(pv.RatedMva, "ratedMva", who);
            if (double.IsFinite(pv.RatedMw) && double.IsFinite(pv.RatedMva) && pv.RatedMva < pv.RatedMw) p.Add($"{who}: ratedMva ({pv.RatedMva}) must be at least ratedMw ({pv.RatedMw}).");
            NeedProfile(pv.Profile, who, false);
            InverterSettings s = pv.Inverter;
            if (s.Function == InverterFunction.FixedPowerFactor && !(s.PowerFactor > 0 && s.PowerFactor <= 1)) p.Add($"{who}: powerFactor must be in (0, 1].");
            if (!(s.ResponseSeconds >= 0) || !double.IsFinite(s.ResponseSeconds)) p.Add($"{who}: responseSeconds must be 0 or more.");
            if (s.VoltageBus is int vb && !busIds.Contains(vb)) p.Add($"{who}: voltage bus {vb} is not in the case.");
            if (s.Function is InverterFunction.VoltVar or InverterFunction.VoltVarVoltWatt)
            {
                VoltVarCurve c = s.VoltVarCurveOrDefault;
                if (!(c.V1 < c.V2 && c.V2 <= c.V3 && c.V3 < c.V4) || !double.IsFinite(c.V1) || !double.IsFinite(c.V4))
                    p.Add($"{who}: the volt-var voltages must satisfy V1 < V2 <= V3 < V4.");
                foreach (double q in new[] { c.Q1, c.Q2, c.Q3, c.Q4 })
                    if (!(q >= -1 && q <= 1)) { p.Add($"{who}: volt-var Q values are fractions of the rating, in [-1, 1]."); break; }
            }
            if (s.Function is InverterFunction.VoltWatt or InverterFunction.VoltVarVoltWatt)
            {
                VoltWattCurve c = s.VoltWattCurveOrDefault;
                if (!(c.V1 < c.V2) || !double.IsFinite(c.V1) || !double.IsFinite(c.V2)) p.Add($"{who}: the volt-watt voltages must satisfy V1 < V2.");
                if (!(c.P1 >= 0 && c.P1 <= 1 && c.P2 >= 0 && c.P2 <= 1)) p.Add($"{who}: volt-watt P values are fractions of rated P, in [0, 1].");
            }
            if (s.Function != InverterFunction.Unity && busById.TryGetValue(pv.Bus, out Bus? pb) && pb.Type != BusType.Pq && s.Function != InverterFunction.VoltWatt)
                p.Add($"{who}: bus {pv.Bus} is a {pb.Type} bus, whose voltage a generator holds; a reactive power function there means nothing (use unity, or connect the unit to a PQ bus).");
            if (Mode == QstsControlMode.Steady && s.Function is InverterFunction.VoltVar or InverterFunction.VoltWatt or InverterFunction.VoltVarVoltWatt)
                p.Add($"{who}: volt-var / volt-watt need the timed control mode (steady mode runs only the existing tap and shunt loops).");
        }
        foreach (QstsStorageUnit st in Storage)
        {
            string who = $"storage '{st.Name}'";
            Name(st.Name, who);
            OnBus(st.Bus, who);
            Pos(st.RatedMw, "ratedMw", who);
            Pos(st.CapacityMwh, "capacityMwh", who);
            if (!(st.SocMin >= 0 && st.SocMin < st.SocMax && st.SocMax <= 1)) p.Add($"{who}: the SOC limits must satisfy 0 <= socMin < socMax <= 1.");
            if (!(st.InitialSoc >= st.SocMin && st.InitialSoc <= st.SocMax)) p.Add($"{who}: initialSoc must be within [socMin, socMax].");
            if (!(st.ChargeEfficiency > 0 && st.ChargeEfficiency <= 1)) p.Add($"{who}: chargeEfficiency must be in (0, 1].");
            if (!(st.DischargeEfficiency > 0 && st.DischargeEfficiency <= 1)) p.Add($"{who}: dischargeEfficiency must be in (0, 1].");
            switch (st.Mode)
            {
                case StorageMode.Schedule: NeedProfile(st.ScheduleProfile, who, true); break;
                case StorageMode.PeakShave:
                    if (st.MonitoredBranch is null || !branchNames.Contains(st.MonitoredBranch)) p.Add($"{who}: peak-shave needs a monitored branch name that is in the case (got '{st.MonitoredBranch}').");
                    if (!(st.HighMw > st.LowMw) || !double.IsFinite(st.HighMw) || !double.IsFinite(st.LowMw)) p.Add($"{who}: peak-shave needs highMw above lowMw.");
                    if (Mode == QstsControlMode.Steady) p.Add($"{who}: peak-shave needs the timed control mode.");
                    break;
                case StorageMode.SelfConsumption: break;
            }
        }
        foreach (QstsCapacitor c in Capacitors)
        {
            string who = $"capacitor '{c.Name}'";
            Name(c.Name, who);
            OnBus(c.Bus, who);
            if (!double.IsFinite(c.StepMvar) || c.StepMvar == 0) p.Add($"{who}: stepMvar must be finite and non-zero.");
            if (c.MinSteps > c.MaxSteps || c.InitialSteps < c.MinSteps || c.InitialSteps > c.MaxSteps)
                p.Add($"{who}: the steps must satisfy minSteps <= initialSteps <= maxSteps.");
            Pos(c.DelaySeconds, "delaySeconds", who, allowZero: true);
            Pos(c.OffDelaySeconds, "offDelaySeconds", who, allowZero: true);
            Pos(c.DeadTimeSeconds, "deadTimeSeconds", who, allowZero: true);
            if (c.ControlsBus is int cb && !busIds.Contains(cb)) p.Add($"{who}: controlled bus {cb} is not in the case.");
            switch (c.Control)
            {
                case CapacitorControl.Voltage:
                    if (!(c.OnValue > 0 && c.OffValue > c.OnValue) || !double.IsFinite(c.OffValue)) p.Add($"{who}: voltage control needs 0 < onPu < offPu.");
                    break;
                case CapacitorControl.Var:
                    if (c.MonitoredBranch is null || !branchNames.Contains(c.MonitoredBranch)) p.Add($"{who}: var control needs a monitored branch name that is in the case.");
                    if (!(c.OnValue > c.OffValue) || !double.IsFinite(c.OnValue) || !double.IsFinite(c.OffValue)) p.Add($"{who}: var control needs onMvar above offMvar.");
                    break;
                case CapacitorControl.Time:
                    if (!(c.OnValue >= 0 && c.OnValue < 86400 && c.OffValue >= 0 && c.OffValue < 86400 && c.OnValue != c.OffValue))
                        p.Add($"{who}: time control needs onSeconds and offSeconds of the day in [0, 86400), different.");
                    break;
            }
        }
        var oltcNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Oltc o in model.Oltcs) oltcNames.Add(o.Transformer);
        void CheckTiming(OltcTiming t, string who)
        {
            Pos(t.DelaySeconds, "delaySeconds", who, allowZero: true);
            Pos(t.InterTapSeconds, "interTapSeconds", who, allowZero: true);
            Pos(t.MinDelaySeconds, "minDelaySeconds", who, allowZero: true);
            if (!double.IsFinite(t.LdcRPu) || !double.IsFinite(t.LdcXPu)) p.Add($"{who}: the LDC impedance must be finite.");
            if (t.Characteristic == OltcCharacteristic.Inverse && !(t.DelaySeconds > 0)) p.Add($"{who}: inverse time needs delaySeconds above 0.");
        }
        CheckTiming(OltcDefault, "oltc default");
        foreach (KeyValuePair<string, OltcTiming> kv in OltcSettings)
        {
            if (!oltcNames.Contains(kv.Key)) p.Add($"oltc setting '{kv.Key}': the case has no OLTC record on that transformer.");
            CheckTiming(kv.Value, $"oltc setting '{kv.Key}'");
        }
        if (Mode == QstsControlMode.Timed && model.TransformerFlowControls.Count > 0 && OltcEnabled)
            p.Add($"the case has {model.TransformerFlowControls.Count} transformer flow control(s) (PSS/E COD 2/3); the timed mode does not model them: use mode steady, or remove them.");
        if (Mode == QstsControlMode.Steady)
        {
            foreach (KeyValuePair<string, OltcTiming> kv in OltcSettings)
                if (kv.Value.LdcRPu != 0 || kv.Value.LdcXPu != 0) p.Add($"oltc setting '{kv.Key}': line drop compensation needs the timed mode.");
            foreach (QstsCapacitor c in Capacitors)
                if (c.Control != CapacitorControl.Voltage) p.Add($"capacitor '{c.Name}': {c.Control} control needs the timed mode (steady mode runs the existing voltage-band shunt loop).");
        }
        foreach (string mb in MonitorBranches)
            if (!branchNames.Contains(mb)) p.Add($"monitored branch '{mb}' is not in the case.");
        if (!(Limits.VminPu > 0 && Limits.VmaxPu > Limits.VminPu) || !double.IsFinite(Limits.VmaxPu)) p.Add("limits: need 0 < vminPu < vmaxPu.");
        if (!(Limits.LoadingPercent > 0) || !double.IsFinite(Limits.LoadingPercent)) p.Add("limits: loadingPercent must be above 0.");

        if (p.Count > 0)
            throw new InvalidDataException($"QSTS study refused ({p.Count} problem{(p.Count == 1 ? "" : "s")}):\n  - " + string.Join("\n  - ", p));
    }
}
