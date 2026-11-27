using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Protection;

namespace GridSim.Core.Dynamics.Models.Adaptive;

/// <summary>A unit relay stage the loop does not evaluate, with the reason (never silently dropped).</summary>
public sealed record UnitRelayNotEvaluated(string Unit, string Device, string Reason);

/// <summary>
/// RC5 stage 3: each generating unit's own relays in the adaptive RMS loop (opt-in). One entry per resolved stage of a
/// unit that is integrated as a synchronous machine: 81U / 81O on the machine's electrical frequency F0 (1 + w), 81R on
/// F0 dw/dt (the swing derivative itself; no relay measurement window is modelled), 27 / 59 on the magnitude of the unit's
/// terminal bus voltage (with a folded unit transformer, of the machine terminal on its generator side, V_hv + j X_t I with the
/// machine current I and X_t on the system base: see <see cref="UnitTerminal"/>), 32 on the machine's electrical power Re(E conj(I)) (listed as not evaluated on a unit with no rating, MBase 0, as the passive check lists it not checkable), and 78 on the jump of the terminal bus
/// voltage angle across a network change (a scheduled event, a branch relay trip or a unit trip). 40 is listed as not
/// evaluated: the adaptive path is the classical swing with frozen EMF, which has no field model to lose.
/// <para>
/// <b>Definite-time model.</b> As the R4-14 disc model with an operate time equal to the stage's delay: while the quantity is
/// beyond the pickup the fraction r grows at 1 / delay; it resets to 0 at once when the quantity comes back (the G59 / G99
/// RoCoF note: the delay resets when RoCoF falls below the threshold); the stage operates when r reaches 1. A stage whose
/// delay is not longer than the event tolerance operates on pickup. 78 is judged at the network change only and is
/// instantaneous; a 78 with a longer delay is listed as not evaluated. An operating stage trips its unit: the machine is
/// disconnected (no current, no mechanical power) and the run continues.
/// </para>
/// </summary>
public sealed class UnitRelaysInLoop
{
    private readonly List<Entry> _entries = new();
    private readonly List<UnitRelayNotEvaluated> _skipped = new();
    private readonly double _instantSeconds;

    /// <summary>One evaluated stage.</summary>
    /// <remarks><c>XtSys</c>: 27 / 59 of a unit with a folded unit transformer, its reactance on the system base (the stage reads
    /// the machine terminal); 0 otherwise (the stage reads the bus voltage, as before). <c>VoltageAt</c>: the record's statement of
    /// the voltage read when XtSys is above 0; null otherwise.</remarks>
    internal sealed record Entry(string Unit, int Slot, int BusIndex, double ThresholdMw, UnitRelayStage Stage, double XtSys = 0.0,
        string? VoltageAt = null);

    /// <summary>
    /// Build from the model's unit protection. <paramref name="slotOfGenerator"/> maps <c>model.Generators</c> to machine
    /// slots (-1 when the unit is not integrated as a machine); <paramref name="busIndex"/> is the network's bus index;
    /// <paramref name="nominalHz"/> the run's nominal frequency.
    /// </summary>
    internal UnitRelaysInLoop(GridModel model, UnitProtectionSet units, IReadOnlyList<int> slotOfGenerator,
        IReadOnlyDictionary<int, int> busIndex, int busCount, double baseMva, double nominalHz, double instantSeconds)
    {
        _instantSeconds = instantSeconds;
        BusCount = busCount;
        BaseMva = baseMva;
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int g = 0; g < model.Generators.Count; g++) if (model.Generators[g].Name is { } n) byName.TryAdd(n, g);
        foreach (ResolvedUnitProtection u in units.Resolved)
        {
            int g = byName.GetValueOrDefault(u.Unit, -1);
            foreach (UnitRelayStage st in u.Stages)
            {
                string? why = null;
                if (g < 0) why = "the unit is out of service";
                else if (slotOfGenerator[g] < 0) why = "the unit is not integrated as a synchronous machine in this run (converter plant, a held unit or a power-flow device)";
                else if (st.Function == UnitRelayFunction.LossOfExcitation)
                    why = "loss of excitation needs a field model; the adaptive path is the classical swing with its EMF frozen, so the terminal impedance after a field failure cannot be computed";
                else if (st.Function is UnitRelayFunction.UnderFrequency or UnitRelayFunction.OverFrequency
                         && BitConverter.DoubleToInt64Bits(u.SettingsNominalHz) != BitConverter.DoubleToInt64Bits(nominalHz))
                    why = $"its pickup is written for a {F(u.SettingsNominalHz)} Hz system and this run is {F(nominalHz)} Hz";
                else if (st.Function == UnitRelayFunction.VectorShift && st.Delay.Value > instantSeconds)
                    why = $"a vector shift is a step at a network change; a {F(st.Delay.Value)} s delay on it is not modelled";
                else if (st.Function == UnitRelayFunction.ReversePower && (!(model.Generators[g].MBase > 0.0) || !double.IsFinite(model.Generators[g].MBase)))
                    why = $"the unit has no rating (MBase {model.Generators[g].MBase.ToString("R", CultureInfo.InvariantCulture)} MVA), so a reverse-power pickup in pu of it has no value";
                else if (st.Function is UnitRelayFunction.UnderVoltage or UnitRelayFunction.OverVoltage
                         && model.Generators[g].UnitTransformerXPu > 0.0 && !double.IsFinite(model.Generators[g].UnitTransformerXPu))
                    why = $"the unit transformer reactance is not finite ({model.Generators[g].UnitTransformerXPu.ToString("R", CultureInfo.InvariantCulture)} pu), so the machine terminal voltage cannot be recovered";
                if (why is not null) { _skipped.Add(new UnitRelayNotEvaluated(u.Unit, st.Device, why)); continue; }
                Generator gen = model.Generators[g];
                if (st.Function is UnitRelayFunction.UnderVoltage or UnitRelayFunction.OverVoltage && gen.UnitTransformerXPu > 0.0)
                {
                    // The VTs are on the generator side of the folded unit transformer: the stage reads the machine terminal.
                    _entries.Add(new Entry(u.Unit, slotOfGenerator[g], busIndex[gen.Bus], st.Pickup.Value * gen.MBase, st,
                        UnitTerminal.SystemBaseXt(gen, baseMva), UnitTerminal.TerminalPhrase(gen.UnitTransformerXPu, gen.Bus)));
                    HasTerminalVoltage = true;
                    continue;
                }
                _entries.Add(new Entry(u.Unit, slotOfGenerator[g], busIndex[gen.Bus], st.Pickup.Value * gen.MBase, st));
            }
        }
    }

    /// <summary>Number of evaluated stages.</summary>
    public int Count => _entries.Count;

    /// <summary>The network's bus count (the size of the bus voltage measurement).</summary>
    internal int BusCount { get; }

    /// <summary>The system base (MVA): the machine power in pu times this is MW.</summary>
    internal double BaseMva { get; }

    /// <summary>Whether any evaluated 27 / 59 stage reads a machine terminal behind a folded unit transformer.</summary>
    internal bool HasTerminalVoltage { get; }

    /// <summary>
    /// The machine terminal voltage magnitude of every 27 / 59 stage behind a unit transformer into <paramref name="vTerm"/>
    /// (per stage), from the bus voltages <paramref name="vBus"/> and the machine currents <paramref name="im"/> (into the
    /// network, system base) of one state. Other entries are left untouched.
    /// </summary>
    internal void TerminalVoltages(Complex[] vBus, Complex[] im, double[] vTerm)
    {
        for (int k = 0; k < _entries.Count; k++)
        {
            Entry e = _entries[k];
            if (e.XtSys > 0.0) vTerm[k] = UnitTerminal.Magnitude(vBus[e.BusIndex], im[e.Slot], e.XtSys);
        }
    }

    /// <summary>Whether any evaluated stage is a 78 (vector shift), judged across network changes.</summary>
    internal bool HasVectorShift => _entries.Any(e => e.Stage.Function == UnitRelayFunction.VectorShift);

    /// <summary>The stages listed as not evaluated, with reasons.</summary>
    public IReadOnlyList<UnitRelayNotEvaluated> NotEvaluated => _skipped;

    internal Entry Get(int k) => _entries[k];

    /// <summary>A delay counts as instantaneous when it is not longer than the event tolerance.</summary>
    internal bool Instant(int k) => _entries[k].Stage.Delay.Value <= _instantSeconds;

    /// <summary>
    /// Whether stage <paramref name="k"/> is beyond its pickup at a measured state (78 is never "beyond" here: it is
    /// judged only across a network change). <paramref name="hz"/>, <paramref name="rocof"/>, <paramref name="pMw"/> are
    /// per machine slot; <paramref name="vMag"/> per bus; <paramref name="vTerm"/> per stage (the machine terminal of a 27 / 59
    /// behind a unit transformer, <see cref="TerminalVoltages"/>).
    /// </summary>
    internal bool Beyond(int k, double[] hz, double[] rocof, double[] pMw, double[] vMag, double[] vTerm)
    {
        Entry e = _entries[k];
        double p = e.Stage.Pickup.Value;
        return e.Stage.Function switch
        {
            UnitRelayFunction.UnderFrequency => hz[e.Slot] < p,
            UnitRelayFunction.OverFrequency => hz[e.Slot] > p,
            UnitRelayFunction.Rocof => Math.Abs(rocof[e.Slot]) > p,
            UnitRelayFunction.UnderVoltage => (e.XtSys > 0.0 ? vTerm[k] : vMag[e.BusIndex]) < p,
            UnitRelayFunction.OverVoltage => (e.XtSys > 0.0 ? vTerm[k] : vMag[e.BusIndex]) > p,
            UnitRelayFunction.ReversePower => pMw[e.Slot] < -e.ThresholdMw,
            _ => false,
        };
    }

    /// <summary>The quantity stage <paramref name="k"/> reads, for the record (Hz, Hz/s, pu, MW).</summary>
    internal double Quantity(int k, double[] hz, double[] rocof, double[] pMw, double[] vMag, double[] vTerm)
    {
        Entry e = _entries[k];
        return e.Stage.Function switch
        {
            UnitRelayFunction.UnderFrequency or UnitRelayFunction.OverFrequency => hz[e.Slot],
            UnitRelayFunction.Rocof => rocof[e.Slot],
            UnitRelayFunction.UnderVoltage or UnitRelayFunction.OverVoltage => e.XtSys > 0.0 ? vTerm[k] : vMag[e.BusIndex],
            UnitRelayFunction.ReversePower => pMw[e.Slot],
            _ => double.NaN,
        };
    }

    /// <summary>The vector shift (deg, magnitude, wrapped to [0, 180]) between two bus voltage phasors.</summary>
    internal static double ShiftDeg(Complex before, Complex after)
    {
        // The phase through DetMath (fdlibm atan2), not Complex.Phase: the same bits on every CPU and runtime.
        double d = (Numerics.DetMath.Atan2(after.Imaginary, after.Real) - Numerics.DetMath.Atan2(before.Imaginary, before.Real)) * 180.0 / Math.PI;
        d = Math.IEEERemainder(d, 360.0);
        return Math.Abs(d);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>One unit relay stage's behaviour over an adaptive run.</summary>
/// <param name="Unit">The unit (generator name).</param>
/// <param name="Device">The stage (for example 81U-1, 81R, 32).</param>
/// <param name="Tripped">Whether it operated and tripped the unit.</param>
/// <param name="PickupSeconds">When it last picked up before its trip (or at the end if picked up and not tripped); null if never.</param>
/// <param name="TripSeconds">The trip time (the applied time of the located event), or null.</param>
/// <param name="TripBracketLowSeconds">The low end of the trip bracket.</param>
/// <param name="TripQuantity">The quantity it read at the trip (Hz, Hz/s, pu, MW, deg).</param>
/// <param name="Settings">The settings phrase: each value with its grade (placeholder or sourced), layer and provenance tag.</param>
public sealed record AdaptiveUnitRelayRecord(string Unit, string Device, bool Tripped, double? PickupSeconds, double? TripSeconds,
    double? TripBracketLowSeconds, double? TripQuantity, string Settings)
{
    /// <summary>27 / 59 of a unit with a folded unit transformer: the voltage the stage read (the machine terminal behind the
    /// transformer, not the network bus). Null (and not written) when the stage read the unit's network bus.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VoltageMeasuredAt { get; init; }
}
