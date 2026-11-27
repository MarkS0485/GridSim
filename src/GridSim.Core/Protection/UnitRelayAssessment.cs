using System.Globalization;
using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Protection;

/// <summary>
/// RC5 per-unit protection, passive: each generating unit's own relays (<see cref="UnitProtectionSet"/>) judged on a solved
/// state or a dynamic run's trajectory with the unit's own resolved settings. Every verdict names the unit
/// (<see cref="RelayVerdict.LocationName"/>), the relay stage (<see cref="RelayVerdict.Device"/>, for example 81U-1) and the
/// grade and source of each setting it used (<see cref="RelayVerdict.UnitStage"/> and the basis text: placeholder or
/// sourced, unit override or class default). It never calls a solver or changes the model.
///
/// <para><b>Time delays.</b> A steady solved state is a sustained condition, so a stage beyond its pickup operates whatever
/// its definite delay (as the network elements read a steady state). A fault snapshot and a trajectory summary carry no
/// duration: a stage with a delay above zero that is beyond its pickup there is reported not checkable, naming the pickup
/// it crossed and the delay that could not be judged, never as a trip (the RMS loop, opt-in, times it).</para>
///
/// <para><b>What each element reads.</b> 27 / 59: the unit's terminal bus voltage (lowest / highest phase in a sequence
/// study); with a unit transformer folded into the unit (<see cref="Generator.UnitTransformerXPu"/>) that bus is its HV side and
/// the relay's VTs are on the generator side, so it reads the machine terminal |V_t| = |V + j X_t I| (I from the unit's solved
/// output; not checkable on a fault snapshot or without a solved output). 32: the unit's real output (solved, for a unit on the slack bus), against reversePowerPu of its rating. 40: the
/// apparent impedance at the terminals, Z = |V|^2 / conj(S), in per unit of the unit's own rating (with a unit transformer
/// folded into the unit, <see cref="Generator.UnitTransformerXPu"/>, at the machine terminal: Z + j X_t), inside an offset mho
/// circle below the origin (centre at -j(offset + diameter / 2), radius diameter / 2); not checkable when the unit's reactive
/// output is unknown. 81U / 81O: the operating frequency, or the run's minimum / maximum; not checkable when the unit's
/// settings are written for another nominal frequency. 81R: the run's peak |df/dt|. 78: the run's peak vector shift.</para>
/// </summary>
public static class UnitRelayAssessment
{
    /// <summary>The verdicts of every unit's own relays on a solved state, in unit order (the set's order), each unit's stages
    /// in the fixed stage order. <paramref name="faultSnapshot"/>: the state is a fault study's snapshot (no duration).</summary>
    internal static IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx, UnitProtectionSet units, bool faultSnapshot)
    {
        IReadOnlyList<Generator> gens = ctx.Model.Generators;
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int g = 0; g < gens.Count; g++) if (gens[g].Name is { } n) indexByName.TryAdd(n, g);
        PowerFlowResult res = ctx.Result;
        bool solved = res.Converged && res.BusIds.Count == ctx.Model.BusCount && res.PInjMw.Length == ctx.Model.BusCount
            && res.QInjMvar.Length == ctx.Model.BusCount && res.Vm.Length == ctx.Model.BusCount;
        bool timed = ctx.Trajectory is not null || faultSnapshot;
        string snapshot = ctx.Trajectory is not null ? "a run summary (its extremes)" : "a fault snapshot";

        foreach (ResolvedUnitProtection u in units.Resolved)
        {
            if (!indexByName.TryGetValue(u.Unit, out int g))
            {
                foreach (UnitRelayStage st in u.Stages)
                    yield return NotCheckable(st, -1, u.Unit, "the unit is out of service, so its relays measure nothing");
                continue;
            }
            Generator gen = gens[g];
            int bi = ctx.BusIndex(gen.Bus);
            foreach (UnitRelayStage st in u.Stages)
            {
                RelayVerdict v = st.Function switch
                {
                    UnitRelayFunction.UnderVoltage or UnitRelayFunction.OverVoltage => Voltage(ctx, st, g, gen, bi, solved, faultSnapshot),
                    UnitRelayFunction.ReversePower => ReversePower(ctx, st, g, gen, solved),
                    UnitRelayFunction.LossOfExcitation => LossOfExcitation(ctx, st, g, gen, bi, solved),
                    UnitRelayFunction.UnderFrequency or UnitRelayFunction.OverFrequency =>
                        Frequency(st, g, u, ctx.Trajectory, ctx.FrequencyHz, ctx.FrequencyHz),
                    UnitRelayFunction.Rocof => Rocof(st, g, u.Unit, ctx.Trajectory),
                    _ => VectorShift(st, g, u.Unit, ctx.Trajectory),
                };
                yield return Timed(v, st, timed, snapshot);
            }
        }
    }

    /// <summary>
    /// The verdicts of every unit's own frequency relays (81U, 81O, 81R, 78) on a dynamic run's trajectory with no solved
    /// network (the twin of <see cref="ProtectionAssessment.AssessTrajectory"/>): every unit sees the trajectory's frequency
    /// (a centre-of-inertia run gives every unit the same one). The other stages are reported not checkable (they need a
    /// solved network). <see cref="RelayVerdict.LocationId"/> is -1 (no generator list is known here).
    /// </summary>
    public static IReadOnlyList<RelayVerdict> AssessTrajectory(UnitProtectionSet units, ProtectionTrajectory trajectory,
        double systemNominalHz = Dynamics.FrequencyLimits.NominalHz)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(trajectory);
        var list = new List<RelayVerdict>();
        foreach (ResolvedUnitProtection u in units.Resolved)
            foreach (UnitRelayStage st in u.Stages)
            {
                RelayVerdict v = st.Function switch
                {
                    UnitRelayFunction.UnderFrequency or UnitRelayFunction.OverFrequency =>
                        Frequency(st, -1, u, trajectory, double.NaN, systemNominalHz),
                    UnitRelayFunction.Rocof => Rocof(st, -1, u.Unit, trajectory),
                    UnitRelayFunction.VectorShift => VectorShift(st, -1, u.Unit, trajectory),
                    _ => NotCheckable(st, -1, u.Unit, "a frequency trajectory has no solved network to read the unit's voltage, power or impedance from"),
                };
                list.Add(Timed(v, st, true, "a run summary (its extremes)"));
            }
        return list;
    }

    // ------------------------------------------------------------------------------------------------ elements

    private static RelayVerdict Voltage(ProtectionContext ctx, UnitRelayStage st, int g, Generator gen, int bi, bool solved, bool faultSnapshot)
    {
        if (bi < 0) return NotCheckable(st, g, gen.Name!, $"the unit's bus {gen.Bus} is not in the solved state");
        bool under = st.Function == UnitRelayFunction.UnderVoltage;
        double vm;
        string what;
        double xt = gen.UnitTransformerXPu;
        if (xt > 0.0)
        {
            // A folded unit transformer: the solved bus is its HV side and the VTs are on the generator side, so the relay reads
            // the machine terminal V_t = V_hv + j X_t I (UnitTerminal, the 40 convention), I the unit's current into the network.
            string hv = $"bus {gen.Bus} is the HV side of the unit's {F(xt)} pu unit transformer";
            if (!double.IsFinite(xt))
                return NotCheckable(st, g, gen.Name!, $"the unit transformer reactance is not finite ({xt.ToString("R", CultureInfo.InvariantCulture)} pu), so the machine terminal voltage cannot be recovered");
            if (faultSnapshot)
                return NotCheckable(st, g, gen.Name!, $"{hv}, and a fault snapshot carries no current for the unit, so the machine terminal voltage its VTs read cannot be recovered");
            if (!solved)
                return NotCheckable(st, g, gen.Name!, $"{hv}, and the state carries no solved output for the unit, so the machine terminal voltage its VTs read cannot be recovered");
            if (!double.IsFinite(gen.MBase))
                return NotCheckable(st, g, gen.Name!, $"the unit's rating is not finite (MBase {gen.MBase.ToString("R", CultureInfo.InvariantCulture)} MVA), so the unit transformer reactance has no base");
            double vhv = ctx.Vm[bi];
            if (!(vhv > 0.0))
                return NotCheckable(st, g, gen.Name!, $"{hv}, and its solved voltage is {F(vhv)} pu, so the unit's current is undefined");
            (double p, double q) = SolvedGeneration.UnitOutput(ctx.Model, ctx.Result, g);
            // In the HV voltage's own frame (|V_t| does not depend on the angle reference): V = |V_hv|, and
            // I = conj(S / V) = (P - jQ) / (MBase |V_hv|) in pu of the unit rating, the base X_t is on.
            double k = 1.0 / (MachineBase.EffectiveMva(gen, ctx.Model.BaseMva) * vhv);
            vm = UnitTerminal.Magnitude(new Complex(vhv, 0.0), new Complex(p * k, -q * k), xt);
            what = $"machine terminal voltage {F(vm)} pu at {UnitTerminal.TerminalPhrase(xt, gen.Bus)}; HV bus {F(vhv)} pu at {ctx.BusName(gen.Bus)}, P {F(p)} MW, Q {F(q)} Mvar";
        }
        else
        {
            vm = under ? ctx.VmPhaseMinPu[bi] : ctx.VmPhaseMaxPu[bi];
            what = $"terminal voltage {F(vm)} pu at {ctx.BusName(gen.Bus)}";
        }
        double pu = st.Pickup.Value;
        bool op = under ? vm < pu : vm > pu;
        double margin = under ? pu - vm : vm - pu;
        return Make(st, g, gen.Name!, op, vm, "pu", pu, margin,
            op ? $"{what} is {(under ? "below" : "above")} the {F(pu)} pu pickup"
               : $"{what} ({(under ? "above" : "below")} the {F(pu)} pu pickup)");
    }

    private static RelayVerdict ReversePower(ProtectionContext ctx, UnitRelayStage st, int g, Generator gen, bool solved)
    {
        if (!(gen.MBase > 0.0) || !double.IsFinite(gen.MBase))
            return NotCheckable(st, g, gen.Name!, $"the unit has no rating (MBase {gen.MBase.ToString("R", CultureInfo.InvariantCulture)} MVA), so a pickup in pu of it has no value");
        double pg = gen.Pg;
        if (solved && ctx.Model.Buses.Any(b => b.Id == gen.Bus && b.Type == BusType.Slack))
            pg = SolvedGeneration.UnitOutput(ctx.Model, ctx.Result, g).PMw;
        double thr = st.Pickup.Value * gen.MBase;
        bool op = pg < -thr;
        return Make(st, g, gen.Name!, op, pg, "MW", -thr, -pg - thr,
            op ? $"the unit absorbs {F(-pg)} MW, past the {F(thr)} MW reverse-power pickup ({F(st.Pickup.Value)} pu of {F(gen.MBase)} MVA)"
               : $"the unit exports {F(pg)} MW (reverse-power pickup {F(thr)} MW)");
    }

    private static RelayVerdict LossOfExcitation(ProtectionContext ctx, UnitRelayStage st, int g, Generator gen, int bi, bool solved)
    {
        if (bi < 0) return NotCheckable(st, g, gen.Name!, $"the unit's bus {gen.Bus} is not in the solved state");
        if (!solved) return NotCheckable(st, g, gen.Name!, "the state carries no solved reactive output for the unit, so its terminal impedance is unknown");
        if (!(gen.MBase > 0.0) || !double.IsFinite(gen.MBase))
            return NotCheckable(st, g, gen.Name!, $"the unit has no rating (MBase {gen.MBase.ToString("R", CultureInfo.InvariantCulture)} MVA), so its impedance base is unknown");
        (double p, double q) = SolvedGeneration.UnitOutput(ctx.Model, ctx.Result, g);
        double s2 = p * p + q * q;
        if (!(s2 > 0.0)) return NotCheckable(st, g, gen.Name!, "the unit carries no power, so its apparent impedance is unbounded (outside every mho circle)");
        double vm = ctx.Vm[bi];
        double k = vm * vm * gen.MBase / s2;           // Z = |V|^2 / conj(S) = |V|^2 S / |S|^2, in pu of the unit rating
        var z = new Complex(k * p, k * q);
        // A folded unit transformer (UnitTransformerXPu, on the unit rating): the solved bus is its HV side and the relay sees the
        // machine terminal, V_t = V_hv + j X_t I with I the unit's current into the network (generator convention), so
        // Z_t = V_t / I = Z_hv + j X_t. Without one (X_t = 0) z is untouched.
        double xt = gen.UnitTransformerXPu;
        bool behindXt = xt > 0.0;
        if (behindXt)
        {
            if (!double.IsFinite(xt))
                return NotCheckable(st, g, gen.Name!, $"the unit transformer reactance is not finite ({xt.ToString("R", CultureInfo.InvariantCulture)} pu), so the machine terminal cannot be recovered");
            z = new Complex(z.Real, z.Imaginary + xt);
        }
        double radius = 0.5 * st.Diameter!.Value;
        var centre = new Complex(0.0, -(st.Pickup.Value + radius));
        double d = (z - centre).Magnitude;
        bool op = d < radius;
        string zs = $"apparent impedance {F(z.Real)} {(z.Imaginary < 0 ? "-" : "+")} j{F(Math.Abs(z.Imaginary))} pu (P {F(p)} MW, Q {F(q)} Mvar)";
        if (behindXt) zs += $" at the machine terminal, behind the {F(xt)} pu unit transformer (Z = |V|^2 / conj(S) at bus {gen.Bus} + j X_t)";
        return Make(st, g, gen.Name!, op, d, "pu", radius, radius - d,
            (op ? $"{zs} is inside" : $"{zs} is outside")
            + $" the mho circle (offset {F(st.Pickup.Value)} pu, diameter {F(st.Diameter.Value)} pu); a steady operating point, not a field-failure transient");
    }

    private static RelayVerdict Frequency(UnitRelayStage st, int g, ResolvedUnitProtection u, ProtectionTrajectory? trajectory,
        double operatingHz, double systemNominalHz)
    {
        if (BitConverter.DoubleToInt64Bits(systemNominalHz) != BitConverter.DoubleToInt64Bits(u.SettingsNominalHz))
            return NotCheckable(st, g, u.Unit,
                $"the {F(st.Pickup.Value)} Hz pickup is written for a {u.SettingsNominalHz.ToString("0.###", CultureInfo.InvariantCulture)} Hz system and this system is "
                + $"{systemNominalHz.ToString("0.###", CultureInfo.InvariantCulture)} Hz, so it does not apply (give relayNominalHz and pickups for this system)");
        bool under = st.Function == UnitRelayFunction.UnderFrequency;
        double f = trajectory is null ? operatingHz : under ? trajectory.MinFrequencyHz : trajectory.MaxFrequencyHz;
        double pu = st.Pickup.Value;
        bool op = under ? f < pu : f > pu;
        string src = trajectory is null ? "operating" : under ? "run minimum" : "run maximum";
        return Make(st, g, u.Unit, op, f, "Hz", pu, under ? pu - f : f - pu,
            op ? $"{src} frequency {F(f)} Hz is {(under ? "below" : "above")} the {F(pu)} Hz pickup"
               : $"{src} frequency {F(f)} Hz ({(under ? "above" : "below")} the {F(pu)} Hz pickup)");
    }

    private static RelayVerdict Rocof(UnitRelayStage st, int g, string unit, ProtectionTrajectory? trajectory)
    {
        if (trajectory is null)
            return NotCheckable(st, g, unit, "RoCoF needs a dynamic run (a frequency trajectory); a steady-state snapshot has no df/dt");
        double r = trajectory.MaxAbsRocofHzPerS, pu = st.Pickup.Value;
        bool op = r > pu;
        return Make(st, g, unit, op, r, "Hz/s", pu, r - pu,
            op ? $"peak |df/dt| {F(r)} Hz/s exceeds the {F(pu)} Hz/s pickup" : $"peak |df/dt| {F(r)} Hz/s (below the {F(pu)} Hz/s pickup)");
    }

    private static RelayVerdict VectorShift(UnitRelayStage st, int g, string unit, ProtectionTrajectory? trajectory)
    {
        if (trajectory is null || !double.IsFinite(trajectory.MaxAbsVectorShiftDeg))
            return NotCheckable(st, g, unit, trajectory is null
                ? "vector shift needs a dynamic run (a voltage-angle transient)"
                : "vector shift needs a voltage-angle transient; this run is a frequency (centre-of-inertia) trajectory with no bus voltage angle");
        double vs = trajectory.MaxAbsVectorShiftDeg, pu = st.Pickup.Value;
        bool op = vs > pu;
        return Make(st, g, unit, op, vs, "deg", pu, vs - pu,
            op ? $"peak vector shift {F(vs)} deg exceeds the {F(pu)} deg pickup" : $"peak vector shift {F(vs)} deg (below the {F(pu)} deg pickup)");
    }

    // ------------------------------------------------------------------------------------------------ shared

    /// <summary>A stage with a delay above zero beyond its pickup on a state with no duration: not checkable, never a trip.</summary>
    private static RelayVerdict Timed(RelayVerdict v, UnitRelayStage st, bool noDuration, string what)
    {
        if (!noDuration || !v.Operate || st.Delay.Value == 0.0) return v;
        return v with
        {
            Operate = false,
            Margin = double.NegativeInfinity,
            Confidence = ProtectionConfidence.NotCheckable,
            Basis = v.Basis.Replace("; settings:", $", but {what} carries no duration, so whether it persists for the {F(st.Delay.Value)} s delay cannot be judged; settings:", StringComparison.Ordinal),
        };
    }

    private static RelayVerdict Make(UnitRelayStage st, int g, string unit, bool op, double quantity, string qUnit, double pickup,
        double margin, string what)
    {
        bool sourced = !st.Pickup.IsPlaceholder && !st.Delay.IsPlaceholder && !(st.Diameter?.IsPlaceholder ?? false);
        return new RelayVerdict(st.Device, $"{st.FunctionName} (unit)", ProtectionLocation.Generator, g, unit, op, quantity, qUnit,
            pickup, margin, st.Delay.Value, $"unit {unit} relay {st.Device}: {what}; settings: {SettingsPhrase(st)}",
            sourced ? ProtectionConfidence.Grounded : ProtectionConfidence.Generic)
        { UnitStage = st };
    }

    private static RelayVerdict NotCheckable(UnitRelayStage st, int g, string unit, string why) =>
        new(st.Device, $"{st.FunctionName} (unit)", ProtectionLocation.Generator, g, unit, false, 0, "", st.Pickup.Value,
            double.NegativeInfinity, null, $"unit {unit} relay {st.Device}: not checkable: {why}; settings: {SettingsPhrase(st)}",
            ProtectionConfidence.NotCheckable)
        { UnitStage = st };

    /// <summary>The settings phrase of every unit verdict: each value with its grade (placeholder or sourced), the layer it came
    /// from (unit or class, and any preset) and its provenance tag.</summary>
    public static string SettingsPhrase(UnitRelayStage st)
    {
        string One(string what, UnitSetting s, string unit) =>
            $"{what} {F(s.Value)}{unit} ({s.Grade}, {s.Layer}, {s.Provenance.Tag})";
        string pickupUnit = st.Function switch
        {
            UnitRelayFunction.UnderVoltage or UnitRelayFunction.OverVoltage => " pu",
            UnitRelayFunction.ReversePower => " pu of rating",
            UnitRelayFunction.LossOfExcitation => " pu",
            UnitRelayFunction.UnderFrequency or UnitRelayFunction.OverFrequency => " Hz",
            UnitRelayFunction.Rocof => " Hz/s",
            _ => " deg",
        };
        string text = One(st.Function == UnitRelayFunction.LossOfExcitation ? "offset" : "pickup", st.Pickup, pickupUnit);
        if (st.Diameter is { } d) text += "; " + One("diameter", d, " pu");
        return text + "; " + One("delay", st.Delay, " s");
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
