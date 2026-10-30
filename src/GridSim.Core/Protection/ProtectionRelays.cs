using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Protection;

/// <summary>A relay element: it scans the solved state (<see cref="ProtectionContext"/>) and reports where
/// it would operate. Yields every operating location plus, for context, the single closest non-operating
/// one - so the report can state both "would fire on bus X" and "nearest to firing: bus Y at 92%".</summary>
public interface IProtectionRelay
{
    string Device { get; }
    string Function { get; }
    IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx);
}

/// <summary>The set of relay elements the assessment runs, in a FIXED order (the order verdicts are produced in).
/// D-DOC-224 (October 2026): this comment used to say "ordered device-number ascending", which the list is not (49 and
/// 50 / 51 / 21 come before 81U, and 87 before the 50N / 51N / 21N / 46 group). The order is kept exactly as it is,
/// because the verdict list and every report built from it follow it; it groups the elements as voltage (27, 59),
/// power (32), branch current (37, 49, 50, 51), impedance (21), system frequency (81U, 81O, 81R, 78), busbar (87) and
/// the sequence-quantity elements last.</summary>
public static class ProtectionRegistry
{
    public static IReadOnlyList<IProtectionRelay> All { get; } = new IProtectionRelay[]
    {
        new UnderVoltageRelay(),
        new OverVoltageRelay(),
        new DirectionalPowerRelay(),
        new UnderCurrentRelay(),
        new ThermalOverloadRelay(),
        new InstantaneousOvercurrentRelay(),
        new TimeOvercurrentRelay(),
        new DistanceRelay(),
        new UnderFrequencyRelay(),
        new OverFrequencyRelay(),
        new RocofRelay(),
        new VectorShiftLomRelay(),
        new DifferentialRelay(),
        new GroundFaultRelay(),
    };
}

/// <summary>Shared helper: track operating verdicts and the single closest non-operating one.</summary>
internal sealed class VerdictCollector
{
    private readonly List<RelayVerdict> _operating = new();
    private RelayVerdict? _closest;

    public void Add(bool operate, RelayVerdict v)
    {
        if (operate) { _operating.Add(v); return; }
        if (_closest is null || v.Margin > _closest.Margin) _closest = v; // largest (least-negative) margin
    }

    public IEnumerable<RelayVerdict> Emit()
    {
        foreach (RelayVerdict v in _operating) yield return v;
        if (_closest is not null) yield return _closest;
    }
}

/// <summary>27 - Undervoltage. Vm below the pickup: a depressed bus (voltage-collapse precursor).</summary>
public sealed class UnderVoltageRelay : IProtectionRelay
{
    public string Device => "27";
    public string Function => "Undervoltage";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        var c = new VerdictCollector();
        double pu = ctx.Settings.UnderVoltagePu;
        for (int i = 0; i < ctx.Vm.Length; i++)
        {
            double vm = ctx.VmPhaseMinPu[i]; int id = ctx.BusIds[i];   // N117: the lowest phase voltage (= Vm without a sequence study)
            double margin = pu - vm;
            bool op = vm < pu;
            c.Add(op, new RelayVerdict(Device, Function, ProtectionLocation.Bus, id, ctx.BusName(id), op,
                vm, "pu", pu, margin, null,
                op ? $"Vm {vm:F3} pu at {ctx.BusName(id)} is below the {pu:F2} pu undervoltage pickup"
                   : $"Vm {vm:F3} pu ({(vm - pu):+0.000;-0.000} of pickup)",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }
}

/// <summary>59 - Overvoltage. Vm above the pickup: light-load / Ferranti overvoltage.</summary>
public sealed class OverVoltageRelay : IProtectionRelay
{
    public string Device => "59";
    public string Function => "Overvoltage";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        var c = new VerdictCollector();
        double pu = ctx.Settings.OverVoltagePu;
        for (int i = 0; i < ctx.Vm.Length; i++)
        {
            double vm = ctx.VmPhaseMaxPu[i]; int id = ctx.BusIds[i];   // N117: the highest phase voltage (= Vm without a sequence study)
            double margin = vm - pu;
            bool op = vm > pu;
            c.Add(op, new RelayVerdict(Device, Function, ProtectionLocation.Bus, id, ctx.BusName(id), op,
                vm, "pu", pu, margin, null,
                op ? $"Vm {vm:F3} pu at {ctx.BusName(id)} is above the {pu:F2} pu overvoltage pickup"
                   : $"Vm {vm:F3} pu ({(pu - vm):+0.000;-0.000} below pickup)",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }
}

/// <summary>32 - Reverse/directional power. A generator absorbing real power (motoring).</summary>
public sealed class DirectionalPowerRelay : IProtectionRelay
{
    public string Device => "32";
    public string Function => "Reverse power";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        var c = new VerdictCollector();
        double thr = ctx.Settings.ReversePowerMw;
        IReadOnlyList<Generator> gens = ctx.Model.Generators;
        for (int g = 0; g < gens.Count; g++)
        {
            double pg = gens[g].Pg;
            double margin = -pg - thr; // reverse (pg negative) beyond threshold => margin>0
            bool op = pg < -thr;
            string nm = gens[g].Name ?? $"gen@bus {gens[g].Bus}";
            c.Add(op, new RelayVerdict(Device, Function, ProtectionLocation.Generator, g, nm, op,
                pg, "MW", -thr, margin, null,
                op ? $"{nm} is absorbing {(-pg):F1} MW (reverse/motoring past the {thr:F0} MW pickup)"
                   : $"{nm} exporting {pg:F1} MW",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }
}

/// <summary>37 - Undercurrent. A branch in service carrying essentially NO current: a de-energized /
/// open-ended circuit. Tested against an absolute floor, so a normally lightly-loaded line (a small but
/// real current) is not flagged - only an effectively dead one.</summary>
public sealed class UnderCurrentRelay : IProtectionRelay
{
    public string Device => "37";
    public string Function => "Undercurrent";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        var c = new VerdictCollector();
        double floor = ctx.Settings.UnderCurrentFloorPu;
        for (int l = 0; l < ctx.Flows.Count; l++)
        {
            // D-DOC-091 (October 2026): the element watches circuits that are IN SERVICE. An out-of-service branch keeps
            // a zero-flow entry in the line flows (so indices stay aligned), and this element used to read that zero
            // as "a dead circuit still marked in service" and operate on every switched-out branch. A circuit the model
            // has taken out of service is open on purpose, so it is skipped (no verdict, operating or not).
            if (!ctx.BranchInService[l]) continue;
            double i = Math.Max(ctx.IFromPu[l], ctx.IToPu[l]);
            double margin = floor - i; // operate when i below the floor
            bool op = i < floor;
            LineFlow lf = ctx.Flows[l];
            string nm = lf.Name.Length > 0 ? lf.Name : $"branch {lf.From}-{lf.To}";
            c.Add(op, new RelayVerdict(Device, Function, ProtectionLocation.Branch, l, nm, op,
                i, "pu", floor, margin, null,
                op ? $"{nm} carrying {i:E2} pu current (effectively de-energized, below the {floor:E1} pu undercurrent floor)"
                   : $"{nm} carrying {i:F3} pu current",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }
}

/// <summary>49 - Thermal overload. Branch loading above its rating, with a first-order thermal replica for
/// the operating time.</summary>
public sealed class ThermalOverloadRelay : IProtectionRelay
{
    public string Device => "49";
    public string Function => "Thermal overload";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        var c = new VerdictCollector();
        for (int l = 0; l < ctx.Flows.Count; l++)
        {
            if (ctx.IRatedPu[l] <= 0) continue;
            ProtectionSettings s = ctx.SettingsFor(l); // site-specific override if registered, else generic
            double pickup = s.ThermalPickupFraction;
            double tau = s.ThermalTimeConstantS;
            double load = ctx.LoadingFraction[l];
            double margin = load - pickup;
            bool op = load > pickup;
            double? t = null;
            if (op)
            {
                double l2 = load * load, p2 = pickup * pickup;
                t = l2 > p2 ? tau * DetMath.Log(l2 / (l2 - p2)) : (double?)null;
            }
            LineFlow lf = ctx.Flows[l];
            string nm = lf.Name.Length > 0 ? lf.Name : $"branch {lf.From}-{lf.To}";
            c.Add(op, new RelayVerdict(Device, Function, ProtectionLocation.Branch, l, nm, op,
                load, "xRateA", pickup, margin, t,
                op ? $"{nm} loaded to {load * 100:F0}% of RateA ({lf.RatingMva:F0} MVA); thermal replica trips in {t:F0} s"
                   : $"{nm} at {load * 100:F0}% of RateA",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }
}

/// <summary>50 - Instantaneous overcurrent. |I| above a multiple of rated current (a close-in fault
/// magnitude); definite/instantaneous operation.</summary>
public sealed class InstantaneousOvercurrentRelay : IProtectionRelay
{
    public string Device => "50";
    public string Function => "Instantaneous overcurrent";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        var c = new VerdictCollector();
        for (int l = 0; l < ctx.Flows.Count; l++)
        {
            double rated = ctx.IRatedPu[l];
            if (rated <= 0) continue;
            double mult = ctx.SettingsFor(l).InstantaneousOcMultiple; // site-specific override if registered
            double i = Math.Max(ctx.IFromPu[l], ctx.IToPu[l]);
            double m = i / rated;
            double margin = m - mult;
            bool op = m > mult;
            LineFlow lf = ctx.Flows[l];
            string nm = lf.Name.Length > 0 ? lf.Name : $"branch {lf.From}-{lf.To}";
            c.Add(op, new RelayVerdict(Device, Function, ProtectionLocation.Branch, l, nm, op,
                m, "xIn", mult, margin, op ? 0.0 : (double?)null,
                op ? $"{nm} current {m:F2}x rated exceeds the {mult:F2}x instantaneous pickup (trips instantly)"
                   : $"{nm} at {m:F2}x rated current",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }
}

/// <summary>51 - Time (inverse-time) overcurrent. |I| above pickup, operating time from the IEC 60255
/// standard-inverse curve t = TMS.0.14 / (M^0.02 - 1).</summary>
public sealed class TimeOvercurrentRelay : IProtectionRelay
{
    public string Device => "51";
    public string Function => "Time overcurrent";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        var c = new VerdictCollector();
        for (int l = 0; l < ctx.Flows.Count; l++)
        {
            double rated = ctx.IRatedPu[l];
            if (rated <= 0) continue;
            ProtectionSettings st = ctx.SettingsFor(l); // site-specific override if registered
            double mult = st.TimeOcMultiple;
            double tms = st.TimeOcTms;
            double i = Math.Max(ctx.IFromPu[l], ctx.IToPu[l]);
            double m = i / rated;
            double margin = m - mult;
            // IEC 60255-151 standard-inverse curve argument is M = I/Is, where Is is the PICKUP
            // setting (mult x rated), not the rated current. Using m = I/rated directly made the
            // operating time finite at pickup (~3.8 s at 1.20x with TMS=0.1) instead of tending to
            // infinity, and ran ~23x too fast just above pickup. Divide by the pickup multiple.
            double bigM = mult > 0 ? m / mult : m;
            bool op = bigM > 1.0;
            // R193: the IEC standard-inverse constants (k = 0.14, alpha = 0.02) come from the ONE curve table,
            // OvercurrentCoordination.IecConstants, rather than a private copy. Same values, same expression order.
            (double k, double alpha) = Coordination.OvercurrentCoordination.IecConstants(
                Coordination.OvercurrentCurve.IecStandardInverse);
            double? t = op ? tms * k / (DetMath.Pow(bigM, alpha) - 1.0) : (double?)null;
            LineFlow lf = ctx.Flows[l];
            string nm = lf.Name.Length > 0 ? lf.Name : $"branch {lf.From}-{lf.To}";
            c.Add(op, new RelayVerdict(Device, Function, ProtectionLocation.Branch, l, nm, op,
                m, "xIn", mult, margin, t,
                op ? $"{nm} current {m:F2}x rated over the {mult:F2}x pickup; IEC standard-inverse trips in {t:F1} s"
                   : $"{nm} at {m:F2}x rated current",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }
}

/// <summary>21 - Distance / impedance. The apparent impedance V^2/conj(S) seen by the relay at EACH end of the
/// line, tested against three forward zones scaled from the line's own series impedance (load encroachment
/// shows here as a heavily-loaded line whose apparent impedance shrinks into zone 3). One verdict per line,
/// from the end that operates in the fastest zone (or, if neither operates, the end closest to operating), so
/// the result no longer depends on which way round the branch is stored (R041, October 2026: only the From end
/// used to be evaluated). On a tie the From end is reported.</summary>
public sealed class DistanceRelay : IProtectionRelay
{
    /// <summary>A line whose series impedance magnitude is at or below this (pu) has no usable reach (a
    /// zero-impedance bus coupler or jumper) and is skipped by the distance relay (R193).</summary>
    public const double MinLineImpedancePu = 1e-9;

    public string Device => "21";
    public string Function => "Distance";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        var c = new VerdictCollector();
        for (int l = 0; l < ctx.Flows.Count; l++)
        {
            ProtectionSettings s = ctx.SettingsFor(l); // site-specific override if registered
            double zline = ctx.ZLineMagPu[l];
            if (zline <= MinLineImpedancePu) continue;
            LineFlow lf = ctx.Flows[l];
            string nm = lf.Name.Length > 0 ? lf.Name : $"branch {lf.From}-{lf.To}";

            // Both relays of the line, then the one that decides the verdict.
            EndView from, to;
            string fromLoop = "", toLoop = "";
            if (ctx.Sequence is { } seq && l < seq.Branches.Count)
            {
                // N117 (October 2026): in an unbalanced study each relay measures its three phase-to-phase loops,
                // Z = (Vx - Vy) / (Ix - Iy), from the phase voltages and currents of the sequence study, and the loop in
                // the fastest zone (or closest to operating) decides that end. A balanced state gives every loop the
                // positive-sequence apparent impedance, so this only differs where the fault is unbalanced. (The earth
                // loops are the 21N element of GroundFaultRelay.)
                BranchSequenceCurrents bc = seq.Branches[l];
                (from, fromLoop) = BestPhaseLoop(seq, lf.From, bc.I0From, bc.I1From, bc.I2From, ctx.ZLineAngleDeg[l], zline, s);
                (to, toLoop) = BestPhaseLoop(seq, lf.To, bc.I0To, bc.I1To, bc.I2To, ctx.ZLineAngleDeg[l], zline, s);
            }
            else
            {
                from = See(ctx.ZAppMagPu[l], ctx.ZAppAngleDeg[l], ctx.ZLineAngleDeg[l], zline, s);
                to = See(ctx.ZAppToMagPu[l], ctx.ZAppToAngleDeg[l], ctx.ZLineAngleDeg[l], zline, s);
            }
            bool useTo = Decides(to, from);
            EndView e = useTo ? to : from;
            string loop = useTo ? toLoop : fromLoop;
            string endNote = (useTo ? $" (relay at the to end, bus {lf.To})" : "") + (loop.Length > 0 ? $" [{loop} loop]" : "");

            bool op = e.Hit is not null;
            double outerReach = s.DistanceZone3Reach * zline;
            string dev = op ? $"21-Z{e.Hit!.Value.zone}" : "21";
            c.Add(op, new RelayVerdict(dev, Function, ProtectionLocation.Branch, l, nm, op,
                e.Zapp, "pu", op ? e.Hit!.Value.reachFrac * zline : outerReach, e.Margin, op ? e.Hit!.Value.t : (double?)null,
                op ? $"{nm} apparent Z {e.Zapp:F3} pu is inside Zone {e.Hit!.Value.zone} (reach {e.Hit!.Value.reachFrac * zline:F3} pu, line Z {zline:F3}); operates in {e.Hit!.Value.t:F1} s{endNote}"
                   : $"{nm} apparent Z {e.Zapp:F2} pu (line Z {zline:F3}, {(e.Forward ? "forward" : "reverse")}){endNote}",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }

    /// <summary>N117: the best of one end's three phase-to-phase loops (see Evaluate), with the loop's name. Phase
    /// voltages are those of the end's bus; currents flow into the branch at that end.</summary>
    private static (EndView View, string Loop) BestPhaseLoop(UnbalancedFaultResult seq, int bus,
        System.Numerics.Complex i0, System.Numerics.Complex i1, System.Numerics.Complex i2, double zlineAngleDeg, double zline, ProtectionSettings s)
    {
        int bi = -1;
        for (int i = 0; i < seq.BusIds.Count; i++) if (seq.BusIds[i] == bus) { bi = i; break; }
        if (bi < 0) return (See(double.PositiveInfinity, 0.0, zlineAngleDeg, zline, s), "");
        (System.Numerics.Complex va, System.Numerics.Complex vb, System.Numerics.Complex vc) = FaultAnalysis.Phases(seq.V0[bi], seq.V1[bi], seq.V2[bi]);
        (System.Numerics.Complex ia, System.Numerics.Complex ib, System.Numerics.Complex ic) = FaultAnalysis.Phases(i0, i1, i2);
        EndView best = default;
        string bestLoop = "";
        bool first = true;
        foreach ((System.Numerics.Complex dv, System.Numerics.Complex di, string name) in new[] { (va - vb, ia - ib, "a-b"), (vb - vc, ib - ic, "b-c"), (vc - va, ic - ia, "c-a") })
        {
            // No measurable loop current reads as an infinite impedance, which no zone reaches (the positive-sequence rule).
            (double mag, double ang) = di.Magnitude > ProtectionContext.MinApparentPowerPu
                ? ((dv / di).Magnitude, DetMath.Phase(dv / di) * 180.0 / Math.PI)
                : (double.PositiveInfinity, 0.0);
            EndView v = See(mag, ang, zlineAngleDeg, zline, s);
            if (first || Decides(v, best)) { best = v; bestLoop = name; first = false; }
        }
        return (best, bestLoop);
    }

    /// <summary>What one end's relay sees: its apparent impedance, whether that is in the forward direction, the
    /// zone it falls in (null = none) and its signed margin to the outer zone (&gt; 0 = operated).</summary>
    private readonly record struct EndView(double Zapp, bool Forward, (int zone, double reachFrac, double t)? Hit, double Margin);

    private static EndView See(double zapp, double zappAngleDeg, double zlineAngleDeg, double zline, ProtectionSettings s)
    {
        double angDiff = Math.Abs(zappAngleDeg - zlineAngleDeg);
        bool forward = angDiff <= s.DistanceAngleWindowDeg;
        (int zone, double reachFrac, double t)? hit = null;
        if (forward)
        {
            if (zapp <= s.DistanceZone1Reach * zline) hit = (1, s.DistanceZone1Reach, s.DistanceZone1TimeS);
            else if (zapp <= s.DistanceZone2Reach * zline) hit = (2, s.DistanceZone2Reach, s.DistanceZone2TimeS);
            else if (zapp <= s.DistanceZone3Reach * zline) hit = (3, s.DistanceZone3Reach, s.DistanceZone3TimeS);
        }
        // Margin: signed distance past pickup (>0 = operated). A reverse (directionally blocked)
        // element cannot operate regardless of |Zapp|, so it must never report a positive margin
        // nor win the 'closest to firing' pick over a forward line genuinely near a zone edge.
        double outerReach = s.DistanceZone3Reach * zline;
        double margin = forward ? (outerReach - zapp) / zline : double.NegativeInfinity;
        return new EndView(zapp, forward, hit, margin);
    }

    /// <summary>True when the to-end view <paramref name="to"/> should decide the line's verdict over the from-end
    /// view <paramref name="from"/>: an operating end beats a non-operating one, a faster (lower) zone beats a
    /// slower one, and between two non-operating ends the larger margin (closer to operating) wins. Ties keep the
    /// from end, so a line whose to end adds nothing reports exactly as before.</summary>
    private static bool Decides(EndView to, EndView from)
    {
        if (to.Hit is { } th)
            return from.Hit is not { } fh || th.zone < fh.zone;
        if (from.Hit is not null) return false;
        return to.Margin > from.Margin;
    }
}

/// <summary>81U - Underfrequency. The run's minimum frequency (or the steady operating frequency) below the
/// statutory pickup.</summary>
public sealed class UnderFrequencyRelay : IProtectionRelay
{
    public string Device => "81U";
    public string Function => "Underfrequency";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        // The steady state runs at the model's nominal frequency, which is also the system nominal the settings must fit.
        yield return Verdict(ctx.Trajectory, ctx.FrequencyHz, ctx.Settings, ctx.FrequencyHz);
    }

    /// <summary>The verdict on a trajectory (or, without one, the operating frequency). Shared by <see cref="Evaluate"/>
    /// and <see cref="ProtectionAssessment.AssessTrajectory"/> (D-DOC-220), so both give the same verdict.
    /// <paramref name="systemNominalHz"/> is the nominal frequency of the system judged; when it differs from
    /// <see cref="ProtectionSettings.SettingsNominalHz"/> the absolute pickup does not apply and the verdict is
    /// not-checkable (D2 nominal frequency, see <see cref="FrequencySettingsMismatch"/>).</summary>
    internal static RelayVerdict Verdict(ProtectionTrajectory? trajectory, double operatingHz, ProtectionSettings settings,
        double systemNominalHz)
    {
        if (FrequencySettingsMismatch("81U", "Underfrequency", settings.UnderFrequencyHz, settings, systemNominalHz) is { } notCheckable)
            return notCheckable;
        double f = trajectory?.MinFrequencyHz ?? operatingHz;
        double pu = settings.UnderFrequencyHz;
        bool op = f < pu;
        string src = trajectory is not null ? "run minimum" : "operating";
        return new RelayVerdict("81U", "Underfrequency", ProtectionLocation.System, -1, "system", op,
            f, "Hz", pu, pu - f, null,
            op ? $"{src} frequency {f:F3} Hz is below the {pu:F1} Hz underfrequency pickup"
               : $"{src} frequency {f:F3} Hz (>{pu:F1} Hz)",
            ProtectionConfidence.Grounded);
    }

    /// <summary>
    /// D2 nominal frequency (October 2026, with N017; Ruling R1: Mark delegated EE calls 2026-10-04): the not-checkable
    /// verdict of an absolute-Hz frequency element (81U / 81O) whose settings were written for another nominal frequency
    /// than the system judged, or null when they match (bit for bit) and the element is evaluated as before. The GB
    /// defaults (G99, 50 Hz) on a 60 Hz system would otherwise operate 81O on its healthy 60 Hz steady state. The verdict
    /// has the RoCoF element's not-checkable shape (no operation, margin minus infinity) and names both frequencies and
    /// the way to supply settings for the system. Fail loud: a system nominal that is not finite and above 0 Hz throws.
    /// </summary>
    internal static RelayVerdict? FrequencySettingsMismatch(string device, string function, double pickupHz,
        ProtectionSettings settings, double systemNominalHz)
    {
        if (!(systemNominalHz > 0.0) || !double.IsFinite(systemNominalHz))
            throw new ArgumentOutOfRangeException(nameof(systemNominalHz), systemNominalHz,
                "The nominal frequency of the system judged must be a positive, finite number of hertz.");
        if (BitConverter.DoubleToInt64Bits(systemNominalHz) == BitConverter.DoubleToInt64Bits(settings.SettingsNominalHz))
            return null;
        return new RelayVerdict(device, function, ProtectionLocation.System, -1, "system", false,
            0, "Hz", pickupHz, double.NegativeInfinity, null,
            $"the {pickupHz:F1} Hz pickup is written for a {settings.SettingsNominalHz:0.###} Hz system and this system is "
            + $"{systemNominalHz:0.###} Hz, so it does not apply; give settings for a {systemNominalHz:0.###} Hz system "
            + "(protection-check --uf and --of together)",
            ProtectionConfidence.NotCheckable);
    }
}

/// <summary>81O - Overfrequency.</summary>
public sealed class OverFrequencyRelay : IProtectionRelay
{
    public string Device => "81O";
    public string Function => "Overfrequency";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        yield return Verdict(ctx.Trajectory, ctx.FrequencyHz, ctx.Settings, ctx.FrequencyHz);
    }

    /// <summary>The verdict on a trajectory or the operating frequency (shared, D-DOC-220); not checkable when the
    /// settings' nominal is not the system's (D2, <see cref="UnderFrequencyRelay.FrequencySettingsMismatch"/>).</summary>
    internal static RelayVerdict Verdict(ProtectionTrajectory? trajectory, double operatingHz, ProtectionSettings settings,
        double systemNominalHz)
    {
        if (UnderFrequencyRelay.FrequencySettingsMismatch("81O", "Overfrequency", settings.OverFrequencyHz, settings, systemNominalHz) is { } notCheckable)
            return notCheckable;
        double f = trajectory?.MaxFrequencyHz ?? operatingHz;
        double pu = settings.OverFrequencyHz;
        bool op = f > pu;
        string src = trajectory is not null ? "run maximum" : "operating";
        return new RelayVerdict("81O", "Overfrequency", ProtectionLocation.System, -1, "system", op,
            f, "Hz", pu, f - pu, null,
            op ? $"{src} frequency {f:F3} Hz is above the {pu:F1} Hz overfrequency pickup"
               : $"{src} frequency {f:F3} Hz (<{pu:F1} Hz)",
            ProtectionConfidence.Grounded);
    }
}

/// <summary>81R - RoCoF. The run's peak rate-of-change-of-frequency against the modern ALoMCP limit. Needs
/// a dynamic run; reports not-checkable against a steady state.</summary>
public sealed class RocofRelay : IProtectionRelay
{
    public string Device => "81R";
    public string Function => "RoCoF";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        yield return Verdict(ctx.Trajectory, ctx.Settings);
    }

    /// <summary>The verdict on a trajectory, not checkable without one (shared, D-DOC-220).</summary>
    internal static RelayVerdict Verdict(ProtectionTrajectory? trajectory, ProtectionSettings settings)
    {
        if (trajectory is null)
            return new RelayVerdict("81R", "RoCoF", ProtectionLocation.System, -1, "system", false,
                0, "Hz/s", settings.RocofTripHzPerS, double.NegativeInfinity, null,
                "RoCoF needs a dynamic run (a frequency trajectory); a steady-state snapshot has no df/dt",
                ProtectionConfidence.NotCheckable);
        double r = trajectory.MaxAbsRocofHzPerS;
        double pu = settings.RocofTripHzPerS;
        bool op = r > pu;
        return new RelayVerdict("81R", "RoCoF", ProtectionLocation.System, -1, "system", op,
            r, "Hz/s", pu, r - pu, null,
            op ? $"peak |df/dt| {r:F3} Hz/s exceeds the {pu:F2} Hz/s RoCoF trip"
               : $"peak |df/dt| {r:F3} Hz/s (<{pu:F2} Hz/s)",
            ProtectionConfidence.Grounded);
    }
}

/// <summary>78 - Vector shift / loss-of-mains. A voltage-angle jump (or RoCoF) past the pickup - the
/// islanding-detection element. Needs a dynamic run.</summary>
public sealed class VectorShiftLomRelay : IProtectionRelay
{
    public string Device => "78";
    public string Function => "Vector shift / LoM";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        yield return Verdict(ctx.Trajectory, ctx.Settings);
    }

    /// <summary>The verdict on a trajectory (shared, D-DOC-220). Not checkable without a trajectory, and (D-DOC-220) not
    /// checkable on a trajectory that carries no voltage-angle transient (<see cref="ProtectionTrajectory.MaxAbsVectorShiftDeg"/>
    /// not finite: a centre-of-inertia frequency run has a frequency but no bus voltage angle, so its vector shift is
    /// unknown, not zero).</summary>
    internal static RelayVerdict Verdict(ProtectionTrajectory? trajectory, ProtectionSettings settings)
    {
        if (trajectory is null || !double.IsFinite(trajectory.MaxAbsVectorShiftDeg))
            return new RelayVerdict("78", "Vector shift / LoM", ProtectionLocation.System, -1, "system", false,
                0, "deg", settings.VectorShiftTripDeg, double.NegativeInfinity, null,
                trajectory is null
                    ? "vector shift / loss-of-mains needs a dynamic run (a voltage-angle transient)"
                    : "vector shift / loss-of-mains needs a voltage-angle transient; this run is a frequency (centre-of-inertia) trajectory with no bus voltage angle",
                ProtectionConfidence.NotCheckable);
        double vs = trajectory.MaxAbsVectorShiftDeg;
        double pu = settings.VectorShiftTripDeg;
        bool op = vs > pu;
        return new RelayVerdict("78", "Vector shift / LoM", ProtectionLocation.System, -1, "system", op,
            vs, "deg", pu, vs - pu, null,
            op ? $"peak vector shift {vs:F1} deg exceeds the {pu:F0} deg loss-of-mains pickup"
               : $"peak vector shift {vs:F1} deg (<{pu:F0} deg)",
            ProtectionConfidence.Generic);
    }
}

/// <summary>87 - Busbar differential. The KCL current residual at each bus; a converged solve balances
/// power so this is ~machine-epsilon - the element bites only under an injected internal fault, reported
/// honestly as secure with the largest residual shown.</summary>
public sealed class DifferentialRelay : IProtectionRelay
{
    public string Device => "87";
    public string Function => "Differential";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        var c = new VerdictCollector();
        double pu = ctx.Settings.DifferentialPickupPu;
        for (int i = 0; i < ctx.DiffCurrentPu.Length; i++)
        {
            double d = ctx.DiffCurrentPu[i]; int id = ctx.BusIds[i];
            double margin = d - pu;
            bool op = d > pu;
            c.Add(op, new RelayVerdict(Device, Function, ProtectionLocation.Bus, id, ctx.BusName(id), op,
                // D-DOC-158 (October 2026): an operating busbar differential trips INSTANTLY (high-speed unit protection,
                // no intentional delay), so its operating time is 0, as the 50 element's is. It used to carry no time,
                // which sorted the protection of the faulted busbar itself behind every timed element.
                d, "pu", pu, margin, op ? 0.0 : (double?)null,
                op ? $"busbar differential {d:E2} pu at {ctx.BusName(id)} exceeds the {pu:F2} pu bias pickup (internal fault)"
                   : $"busbar differential {d:E2} pu at {ctx.BusName(id)} (balanced)",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }
}

/// <summary>50N/51N/21N/46 - Ground/earth and negative-sequence elements. Over a positive-sequence state (a power
/// flow, or a balanced fault) there is no zero- or negative-sequence quantity to measure, so the element is reported
/// not-checkable rather than given a fabricated verdict (unchanged). F21 (October 2026): when the context carries an
/// unbalanced fault study (<see cref="ProtectionContext.Sequence"/>), the four elements are evaluated on its branch
/// sequence currents and bus sequence voltages:
/// <list type="bullet">
/// <item><b>50N</b> instantaneous earth fault: the larger end residual |3 I0| over
/// <see cref="ProtectionSettings.EarthFaultInstantaneousMultiple"/> x rated current.</item>
/// <item><b>51N</b> time earth fault: residual over <see cref="ProtectionSettings.EarthFaultTimeMultiple"/> x rated,
/// time from the IEC standard-inverse curve (the 51 element's curve) at <see cref="ProtectionSettings.EarthFaultTms"/>.</item>
/// <item><b>46</b> negative sequence: |I2| over <see cref="ProtectionSettings.NegativeSequenceMultiple"/> x rated,
/// definite time <see cref="ProtectionSettings.NegativeSequenceTimeS"/>.</item>
/// <item><b>21N</b> earth-fault distance (lines only): at each end, for each phase p, the residual-compensated
/// loop impedance <c>Z = V_p / (I_p + k0 3 I0)</c> with <c>k0 = (Z0L - Z1L) / (3 Z1L)</c> (the textbook earth-loop
/// measurement), tested against the 21 element's forward zones; the end and phase in the fastest zone decide.</item>
/// </list>
/// Pickups are generic (<see cref="ProtectionConfidence.Generic"/>, [VERIFY] in <see cref="ProtectionSettings"/>).</summary>
public sealed class GroundFaultRelay : IProtectionRelay
{
    public string Device => "50N/51N/21N/46";
    public string Function => "Ground / negative-sequence";
    public IEnumerable<RelayVerdict> Evaluate(ProtectionContext ctx)
    {
        if (ctx.Sequence is null)
        {
            yield return new RelayVerdict(Device, Function, ProtectionLocation.System, -1, "system", false,
                double.NaN, "-", double.NaN, double.NegativeInfinity, null,
                "earth-fault (residual/zero-sequence) and negative-sequence elements need an unbalanced " +
                "sequence-impedance model; GridSim is positive-sequence, so there is no residual current to " +
                "evaluate - stated honestly rather than fabricated",
                ProtectionConfidence.NotCheckable);
            yield break;
        }
        foreach (RelayVerdict v in EarthOvercurrent(ctx)) yield return v;
        foreach (RelayVerdict v in NegativeSequence(ctx)) yield return v;
        foreach (RelayVerdict v in EarthDistance(ctx)) yield return v;
    }

    private static string NameOf(ProtectionContext ctx, int l)
    {
        LineFlow lf = ctx.Flows[l];
        return lf.Name.Length > 0 ? lf.Name : $"branch {lf.From}-{lf.To}";
    }

    private static IEnumerable<RelayVerdict> EarthOvercurrent(ProtectionContext ctx)
    {
        UnbalancedFaultResult seq = ctx.Sequence!;
        var inst = new VerdictCollector();
        var timed = new VerdictCollector();
        (double k, double alpha) = Coordination.OvercurrentCoordination.IecConstants(Coordination.OvercurrentCurve.IecStandardInverse);
        for (int l = 0; l < ctx.Flows.Count && l < seq.Branches.Count; l++)
        {
            double rated = ctx.IRatedPu[l];
            if (rated <= 0) continue;
            ProtectionSettings st = ctx.SettingsFor(l);
            string nm = NameOf(ctx, l);
            double ires = Math.Max(seq.Branches[l].ResidualFromPu, seq.Branches[l].ResidualToPu);
            double m = ires / rated;

            double mi = st.EarthFaultInstantaneousMultiple;
            bool opI = m > mi;
            inst.Add(opI, new RelayVerdict("50N", "Instantaneous earth fault", ProtectionLocation.Branch, l, nm, opI,
                m, "xIn", mi, m - mi, opI ? 0.0 : (double?)null,
                opI ? $"{nm} residual current {m:F2}x rated exceeds the {mi:F2}x instantaneous earth-fault pickup (trips instantly)"
                    : $"{nm} residual current {m:F2}x rated",
                ProtectionConfidence.Generic));

            double mt = st.EarthFaultTimeMultiple;
            double bigM = mt > 0 ? m / mt : m;
            bool opT = bigM > 1.0;
            double? t = opT ? st.EarthFaultTms * k / (DetMath.Pow(bigM, alpha) - 1.0) : (double?)null;
            timed.Add(opT, new RelayVerdict("51N", "Time earth fault", ProtectionLocation.Branch, l, nm, opT,
                m, "xIn", mt, m - mt, t,
                opT ? $"{nm} residual current {m:F2}x rated over the {mt:F2}x pickup; IEC standard-inverse trips in {t:F2} s"
                    : $"{nm} residual current {m:F2}x rated",
                ProtectionConfidence.Generic));
        }
        foreach (RelayVerdict v in inst.Emit()) yield return v;
        foreach (RelayVerdict v in timed.Emit()) yield return v;
    }

    private static IEnumerable<RelayVerdict> NegativeSequence(ProtectionContext ctx)
    {
        UnbalancedFaultResult seq = ctx.Sequence!;
        var c = new VerdictCollector();
        for (int l = 0; l < ctx.Flows.Count && l < seq.Branches.Count; l++)
        {
            double rated = ctx.IRatedPu[l];
            if (rated <= 0) continue;
            ProtectionSettings st = ctx.SettingsFor(l);
            string nm = NameOf(ctx, l);
            double m = Math.Max(seq.Branches[l].NegativeSequenceFromPu, seq.Branches[l].NegativeSequenceToPu) / rated;
            double p = st.NegativeSequenceMultiple;
            bool op = m > p;
            c.Add(op, new RelayVerdict("46", "Negative sequence", ProtectionLocation.Branch, l, nm, op,
                m, "xIn", p, m - p, op ? st.NegativeSequenceTimeS : (double?)null,
                op ? $"{nm} negative-sequence current {m:F2}x rated exceeds the {p:F2}x pickup; definite time {st.NegativeSequenceTimeS:F2} s"
                   : $"{nm} negative-sequence current {m:F2}x rated",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }

    private static IEnumerable<RelayVerdict> EarthDistance(ProtectionContext ctx)
    {
        UnbalancedFaultResult seq = ctx.Sequence!;
        var busIdx = new Dictionary<int, int>(seq.BusIds.Count);
        for (int i = 0; i < seq.BusIds.Count; i++) busIdx.TryAdd(seq.BusIds[i], i);
        var c = new VerdictCollector();
        for (int l = 0; l < ctx.Flows.Count && l < seq.Branches.Count && l < ctx.Model.Branches.Count; l++)
        {
            Branch br = ctx.Model.Branches[l];
            if (br.IsTransformer || !br.InService) continue;
            var z1l = new System.Numerics.Complex(br.R, br.X);
            if (!(z1l.Magnitude > DistanceRelay.MinLineImpedancePu)) continue;
            var z0l = br.R0 != 0.0 || br.X0 != 0.0 ? new System.Numerics.Complex(br.R0, br.X0) : seq.LineZeroSeqRatio * z1l;
            System.Numerics.Complex k0 = (z0l - z1l) / (3.0 * z1l);
            ProtectionSettings s = ctx.SettingsFor(l);
            string nm = NameOf(ctx, l);
            BranchSequenceCurrents bc = seq.Branches[l];

            (double zapp, bool forward, int zone, double reach, double time, int end, char phase) best = (double.PositiveInfinity, false, 0, 0, 0, 0, 'a');
            double bestMargin = double.NegativeInfinity;
            bool anyEndReleased = false;   // D-DOC-088: did any end see enough residual current to measure an earth loop?
            foreach ((int bus, System.Numerics.Complex i0, System.Numerics.Complex i1, System.Numerics.Complex i2, int end) in
                     new[] { (br.From, bc.I0From, bc.I1From, bc.I2From, 0), (br.To, bc.I0To, bc.I1To, bc.I2To, 1) })
            {
                if (!busIdx.TryGetValue(bus, out int bi)) continue;
                (System.Numerics.Complex va, System.Numerics.Complex vb, System.Numerics.Complex vc) = FaultAnalysis.Phases(seq.V0[bi], seq.V1[bi], seq.V2[bi]);
                (System.Numerics.Complex ia, System.Numerics.Complex ib, System.Numerics.Complex ic) = FaultAnalysis.Phases(i0, i1, i2);

                // D-DOC-088 (October 2026): the residual-current release. A real earth-fault distance element measures
                // its phase-to-earth loops only once it has detected an earth fault, i.e. a residual current |3 I0| at its
                // own end above a fraction of the largest phase current there (ProtectionSettings.
                // EarthDistanceResidualReleaseFraction). Without that gate a line-to-line fault, which has I0 = 0 exactly,
                // made the loop impedance V_p / I_p of a faulted phase look like an earth fault and 21N operated. An end
                // that is not released measures nothing (the element stays reset there). The phase-to-phase loops of the
                // 21 element (DistanceRelay) are what clears a line-to-line fault.
                double residual = (3.0 * i0).Magnitude;
                double maxPhase = Math.Max(ia.Magnitude, Math.Max(ib.Magnitude, ic.Magnitude));
                bool released = residual > ProtectionContext.MinApparentPowerPu
                    && residual > s.EarthDistanceResidualReleaseFraction * maxPhase;
                if (!released) continue;
                anyEndReleased = true;
                System.Numerics.Complex comp = k0 * 3.0 * i0;
                foreach ((System.Numerics.Complex v, System.Numerics.Complex ip, char ph) in new[] { (va, ia, 'a'), (vb, ib, 'b'), (vc, ic, 'c') })
                {
                    System.Numerics.Complex iloop = ip + comp;
                    if (!(iloop.Magnitude > ProtectionContext.MinApparentPowerPu)) continue;
                    System.Numerics.Complex z = v / iloop;
                    double ang = Math.Abs(DetMath.Phase(z) * 180.0 / Math.PI - DetMath.Phase(z1l) * 180.0 / Math.PI);
                    bool fwd = ang <= s.DistanceAngleWindowDeg;
                    double zl = z1l.Magnitude, zm = z.Magnitude;
                    int zone = 0; double reach = 0, time = 0;
                    if (fwd)
                    {
                        if (zm <= s.DistanceZone1Reach * zl) { zone = 1; reach = s.DistanceZone1Reach; time = s.DistanceZone1TimeS; }
                        else if (zm <= s.DistanceZone2Reach * zl) { zone = 2; reach = s.DistanceZone2Reach; time = s.DistanceZone2TimeS; }
                        else if (zm <= s.DistanceZone3Reach * zl) { zone = 3; reach = s.DistanceZone3Reach; time = s.DistanceZone3TimeS; }
                    }
                    double margin = fwd ? (s.DistanceZone3Reach * zl - zm) / zl : double.NegativeInfinity;
                    bool better = zone > 0
                        ? best.zone == 0 || zone < best.zone || (zone == best.zone && zm < best.zapp)
                        : best.zone == 0 && margin > bestMargin;
                    if (better) { best = (zm, fwd, zone, reach, time, end, ph); bestMargin = margin; }
                }
            }
            bool op = best.zone > 0;
            double zlm = z1l.Magnitude;
            string endNote = best.end == 1 ? $" (relay at the to end, bus {br.To})" : "";
            c.Add(op, new RelayVerdict(op ? $"21N-Z{best.zone}" : "21N", "Earth-fault distance", ProtectionLocation.Branch, l, nm, op,
                best.zapp, "pu", op ? best.reach * zlm : s.DistanceZone3Reach * zlm, bestMargin, op ? best.time : (double?)null,
                op ? $"{nm} phase-{best.phase} earth-loop Z {best.zapp:F3} pu is inside Zone {best.zone} (reach {best.reach * zlm:F3} pu, line Z {zlm:F3}); operates in {best.time:F1} s{endNote}"
                   : anyEndReleased
                       ? $"{nm} earth-loop Z {best.zapp:F2} pu (line Z {zlm:F3}){endNote}"
                       : $"{nm} no residual current above the {s.EarthDistanceResidualReleaseFraction * 100.0:F0}% release at either end: earth loops not measured (line Z {zlm:F3})",
                ProtectionConfidence.Generic));
        }
        return c.Emit();
    }
}
