using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Workflow;

namespace GridSim.Core.Protection.Placed;

/// <summary>
/// One placed device at one fault (RC5 WP4). <see cref="Element"/> names what was evaluated (50/51, 50N/51N, 67, 67N,
/// 67Q, 21, 87L, fuse or breaker). <see cref="MeasuredValue"/> is in <see cref="Unit"/>: CT secondary amps for an
/// overcurrent, directional or differential element, secondary ohms (|Z|) for distance. <see cref="TimeS"/> is the
/// operating time, +infinity when the device does not operate; <see cref="EarliestTimeS"/> is the time a backup check
/// uses (a fuse's minimum melt, otherwise the same). <see cref="Direction"/> is the directional element's decision
/// (forward, reverse, or no polarising voltage); <see cref="FaultDirection"/> is where the fault lies by the supply-path
/// rule (true beyond the device, false behind, null in a mesh).
/// </summary>
public sealed record PlacedRow(
    string DeviceId, string Function, string Element, bool Evaluated, string? Reason,
    double MeasuredValue, string Unit, string? Loop, int? Zone, string? Direction, double TimeS, double EarliestTimeS, bool? FaultDirection)
{
    public bool Operates => Evaluated && !double.IsInfinity(TimeS);
    /// <summary>Distance: the deciding loop's apparent impedance, secondary ohms (NaN otherwise).</summary>
    public Complex ApparentImpedanceOhms { get; init; } = new(double.NaN, double.NaN);
    /// <summary>Directional: arg Iop - arg Vpol of the deciding quantity, degrees, each argument in (-180, 180] (NaN otherwise).</summary>
    public double AngleDifferenceDeg { get; init; } = double.NaN;
    /// <summary>Differential: the deciding phase's differential, bias and threshold currents, secondary amps (NaN otherwise).</summary>
    public double DifferentialA { get; init; } = double.NaN;
    public double BiasA { get; init; } = double.NaN;
    public double ThresholdA { get; init; } = double.NaN;
    /// <summary>RC5 WP17, distance: the zones any measured loop lies in, bit k - 1 for zone k (0 when none or not distance).
    /// Read by the scheme logic; not part of the CSV.</summary>
    public int ZonesPicked { get; init; }
    /// <summary>RC5 (2.1.122), distance: the zones the loops of the faulted phases lie in (see
    /// <see cref="PlacedElementEvaluator.IsFaultLoop"/>), bit k - 1 for zone k. When none of those loops was measured (an
    /// earth fault on a phase-loop relay, or earth loops not released) it is <see cref="ZonesPicked"/>. The DCB carrier stop
    /// reads it, so a healthy-phase loop that load carries into a forward zone does not stop the block.</summary>
    public int FaultLoopZonesPicked { get; init; }
    /// <summary>RC5 WP17, distance: true when the switch-onto-fault element set the time.</summary>
    public bool SwitchOntoFault { get; init; }
    /// <summary>RC5 WP17, distance: the number of phase loops the load blinder blocked.</summary>
    public int LoadBlockedLoops { get; init; }
}

/// <summary>
/// The placed-element evaluation (RC5 WP4): each placed device measured on the phasors of one fault
/// (<see cref="FaultPhasors"/>). Phase overcurrent (50/51) operates on the largest phase current, earth fault (50N/51N)
/// on |3 I0|, both timed by <see cref="DeviceTiming.Seconds"/> unchanged. A 67 is a phase element with quadrature
/// polarisation (Ia with Vbc, Ib with Vca, Ic with Vab), 67N with zero-sequence polarisation (3 I0 against -3 V0) and 67Q
/// with negative-sequence polarisation (I2 against -V2); it is forward when cos(arg Iop - arg Vpol - RCA) &gt; 0 (NPAG ch.
/// 9) and operates only when the direction matches its setting and its overcurrent element operates. A 21 measures the
/// phase loops (Vx - Vy) / (Ix - Iy) and the earth loops Vp / (Ip + k0 3 I0), the earth loops released only when |3 I0|
/// exceeds the overlay's residual release fraction of the largest phase current; the fastest zone hit wins. An 87L is the
/// dual-slope biased differential of NPAG ch. 10 on the two ends of its own branch. Each relay uses its own bus voltage
/// and the branch current into the branch at its end. Devices are in device-number order.
/// </summary>
public static class PlacedElementEvaluator
{
    /// <summary>The line zero-sequence multiple of Z1 where a line carries no R0 / X0 (the default of
    /// <see cref="FaultAnalysis.Unbalanced"/>, which the evaluation calls with it).</summary>
    public const double LineZeroSeqRatio = 3.0;

    private static readonly double ResidualRelease = new ProtectionSettings().EarthDistanceResidualReleaseFraction;

    public static IReadOnlyList<PlacedRow> Evaluate(BoundDeviceSet bound, FaultPhasors fault)
    {
        ArgumentNullException.ThrowIfNull(bound); ArgumentNullException.ThrowIfNull(fault);
        var rows = new List<PlacedRow>(bound.Devices.Count);
        foreach (PlacedDevice d in bound.Devices) rows.Add(One(bound, fault, d));
        return rows;
    }

    private static PlacedRow Not(PlacedDevice d, string element, string reason) =>
        new(d.Id, d.Function, element, false, reason, double.NaN, "", null, null, null, double.PositiveInfinity, double.PositiveInfinity, null);

    private static string ElementOf(PlacedDevice d) => d.Type switch
    {
        DeviceType.Fuse => "fuse",
        DeviceType.Breaker => "breaker",
        _ => d.Function switch
        {
            "" or "50" or "51" => "50/51",
            "50N" or "51N" => "50N/51N",
            "67" => d.Directional?.Polarisation switch
            {
                Polarisation.ZeroSequence => "67N", Polarisation.NegativeSequence => "67Q", _ => "67",
            },
            "21" => "21",
            "87" => d.TransformerDifferential is not null ? "87T" : d.BusDifferential is not null ? "87B" : "87L",
            _ => d.Function,
        },
    };

    private static PlacedRow One(BoundDeviceSet bound, FaultPhasors f, PlacedDevice d)
    {
        string element = ElementOf(d);
        if (f.NotEvaluated is not null) return Not(d, element, "the fault could not be applied: " + f.NotEvaluated);
        // RC5 WP18: the busbar differential has no branch of its own to measure; its CTs are in its setting.
        if (d.BusDifferential is not null) return Bus87(bound, f, d);
        if (bound.OutOfService(d)) return Not(d, element, "the device's branch is out of service");
        FaultNetwork net = f.Network!;
        int seg = PlacedTopology.FaultedBranch(net, d);
        bool fromEnd = d.Location.AtFromEnd;
        Branch fb = net.Model.Branches[seg];
        int nearBus = fromEnd ? fb.From : fb.To;
        if (!f.TryBus(nearBus, out int bi)) return Not(d, element, $"bus {nearBus} is not in the fault study");
        double kv = net.Model.Buses[bi].BaseKv;
        if (!(kv > 0.0)) return Not(d, element, $"bus {nearBus} has no base kV, so amps and ohms are not defined");
        bool? where = PlacedTopology.Direction(net, d, seg);
        double ibaseA = IBaseA(net.Model.BaseMva, kv);
        (Complex i0, Complex i1, Complex i2) = f.Sequence(seg, fromEnd);
        (Complex ia, Complex ib, Complex ic) = FaultAnalysis.Phases(i0, i1, i2);
        double maxPhasePu = Math.Max(ia.Magnitude, Math.Max(ib.Magnitude, ic.Magnitude));

        switch (d.Type)
        {
            case DeviceType.Breaker:
                return new PlacedRow(d.Id, d.Function, element, true, "breaker: no time-current characteristic", maxPhasePu * ibaseA / d.CtRatio, "A sec",
                    null, null, null, double.PositiveInfinity, double.PositiveInfinity, where);
            case DeviceType.Fuse:
            {
                double amps = maxPhasePu * ibaseA;
                double clear = DeviceTiming.Seconds(d, 0, TimingRole.Clearing, amps, bound.Library, out _);
                double melt = DeviceTiming.Seconds(d, 0, TimingRole.Earliest, amps, bound.Library, out _);
                return new PlacedRow(d.Id, d.Function, element, true, null, amps / d.CtRatio, "A sec", null, null, null, clear, melt, where);
            }
        }

        switch (d.Function)
        {
            case "" or "50" or "51":
                return Overcurrent(bound, d, element, maxPhasePu * ibaseA, where);
            case "50N" or "51N":
                return Overcurrent(bound, d, element, (3.0 * i0).Magnitude * ibaseA, where);
            case "67":
                if (d.Directional is not { } dir) return Not(d, element, "a 67 needs a directional setting (directional) for this evaluation");
                return Directional(bound, f, d, dir, element, bi, ibaseA, i0, i2, (ia, ib, ic), where);
            case "21":
                if (d.Distance is not { } dist) return Not(d, element, "a 21 needs distance zones (distance) for this evaluation");
                return Distance(bound, f, d, dist, bi, kv, i0, (ia, ib, ic), maxPhasePu, ibaseA, where);
            case "87" when d.TransformerDifferential is not null:
                return Transformer87(f, d, where);
            case "87":
                return Differential(f, net, d, d.Differential!, ibaseA, (ia, ib, ic), where);
            default:
                return Not(d, element, $"function {d.Function} is not evaluated");
        }
    }

    /// <summary>The base current of a bus, amps: MVA / (sqrt 3 kV) x 1000.</summary>
    public static double IBaseA(double baseMva, double kv) => baseMva / (Math.Sqrt(3.0) * kv) * 1000.0;

    /// <summary>The secondary ohms per pu ohm at a relay: Zbase (kV^2 / MVA) times CT ratio over VT ratio.</summary>
    public static double OhmsPerPu(double baseMva, double kv, PlacedDevice d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return kv * kv / baseMva * d.CtRatio / d.VtRatio;
    }

    private static PlacedRow Overcurrent(BoundDeviceSet bound, PlacedDevice d, string element, double primaryA, bool? where)
    {
        double t = DeviceTiming.Seconds(d, 0, TimingRole.Clearing, primaryA, bound.Library, out _);
        return new PlacedRow(d.Id, d.Function, element, true, null, primaryA / d.CtRatio, "A sec", null, null, null, t, t, where);
    }

    private static double ArgDeg(Complex z) => DetMath.Phase(z) * 180.0 / Math.PI;

    private static PlacedRow Directional(BoundDeviceSet bound, FaultPhasors f, PlacedDevice d, DirectionalSetting s, string element, int bi, double ibaseA,
        Complex i0, Complex i2, (Complex A, Complex B, Complex C) ip, bool? where)
    {
        (Complex va, Complex vb, Complex vc) = f.PhaseVoltages(bi);
        var cands = new List<(string Loop, Complex Iop, Complex Vpol)>(3);
        switch (s.Polarisation)
        {
            case Polarisation.Quadrature:
                cands.Add(("a", ip.A, vb - vc)); cands.Add(("b", ip.B, vc - va)); cands.Add(("c", ip.C, va - vb));
                break;
            case Polarisation.ZeroSequence:
                cands.Add(("3I0", 3.0 * i0, -3.0 * f.V0[bi]));
                break;
            default:
                cands.Add(("I2", i2, -f.V2[bi]));
                break;
        }
        // The deciding candidate: the fastest operating one; when none operates, the one with the largest current
        // (first in order on a tie).
        int best = -1; double bestT = double.PositiveInfinity, bestI = double.NegativeInfinity;
        var dirs = new string[cands.Count];
        var times = new double[cands.Count];
        var angles = new double[cands.Count];
        for (int k = 0; k < cands.Count; k++)
        {
            (_, Complex iop, Complex vpol) = cands[k];
            double primaryA = iop.Magnitude * ibaseA;
            angles[k] = double.NaN;
            times[k] = double.PositiveInfinity;
            bool hasV = vpol != Complex.Zero && vpol.Magnitude >= s.MinPolarisingVPu;
            bool hasI = iop.Magnitude > ProtectionContext.MinApparentPowerPu;
            if (!hasV) dirs[k] = "no polarising voltage";
            else if (!hasI) dirs[k] = "no operating current";
            else
            {
                angles[k] = ArgDeg(iop) - ArgDeg(vpol);
                bool forward = DetMath.Cos((angles[k] - s.CharacteristicAngleDeg) * Math.PI / 180.0) > 0.0;
                dirs[k] = forward ? "forward" : "reverse";
                if (forward == s.Forward) times[k] = DeviceTiming.Seconds(d, 0, TimingRole.Clearing, primaryA, bound.Library, out _);
            }
            bool better = best < 0 || times[k] < bestT || (double.IsInfinity(bestT) && double.IsInfinity(times[k]) && primaryA > bestI);
            if (better) { best = k; bestT = times[k]; bestI = primaryA; }
        }
        double measured = cands[best].Iop.Magnitude * ibaseA / d.CtRatio;
        string? reason = dirs[best] is "no polarising voltage" or "no operating current" ? dirs[best] : null;
        return new PlacedRow(d.Id, d.Function, element, true, reason, measured, "A sec", cands[best].Loop, null, dirs[best], times[best], times[best], where)
        { AngleDifferenceDeg = angles[best] };
    }

    /// <summary>k0 = (Z0L - Z1L) / (3 Z1L): the setting's, or the protected line's own (R0 / X0, or
    /// <see cref="LineZeroSeqRatio"/> x Z1 where the line carries none). Null when the line has no usable Z1.</summary>
    public static Complex? K0(PlacedDevice d, Branch line)
    {
        ArgumentNullException.ThrowIfNull(d); ArgumentNullException.ThrowIfNull(line);
        if (d.Distance?.K0 is { } k) return DetMath.FromPolar(k.Magnitude, k.AngleDeg * Math.PI / 180.0);
        var z1 = new Complex(line.R, line.X);
        if (!(z1.Magnitude > DistanceRelay.MinLineImpedancePu)) return null;
        Complex z0 = line.R0 != 0.0 || line.X0 != 0.0 ? new Complex(line.R0, line.X0) : LineZeroSeqRatio * z1;
        return (z0 - z1) / (3.0 * z1);
    }

    /// <summary>RC5 WP17: the zero-sequence mutual compensation term k0m 3 I0m at the relay, in pu current: I0m is the
    /// zero-sequence current into the parallel line at the relay's bus (on the fault network) and k0m the setting's, or
    /// s Z0m / (3 Z1L) from the case's mutual data, with s = +1 when the parallel line runs the same way as the protected
    /// one and -1 when it runs the other way (the data's Z0m is signed for each line's From-to-To direction).</summary>
    public static Complex MutualTerm(BoundDeviceSet bound, FaultPhasors fault, PlacedDevice d)
    {
        ArgumentNullException.ThrowIfNull(bound); ArgumentNullException.ThrowIfNull(fault); ArgumentNullException.ThrowIfNull(d);
        if (d.Distance?.Mutual is not { } mu) return Complex.Zero;
        Branch par = bound.Model.Branches[mu.ParallelBranch];
        int nearBus = d.Location.NearBus;
        bool parFromEnd = par.From == nearBus;
        int seg = parFromEnd ? FaultNetwork.FromSegment(mu.ParallelBranch) : fault.Network!.ToSegment(mu.ParallelBranch);
        Complex i0m = fault.ZeroSequenceAt(seg, parFromEnd);
        return K0m(bound, d)!.Value * 3.0 * i0m;
    }

    /// <summary>RC5 WP17: k0m for a device with a mutual setting (null without one).</summary>
    public static Complex? K0m(BoundDeviceSet bound, PlacedDevice d)
    {
        ArgumentNullException.ThrowIfNull(bound); ArgumentNullException.ThrowIfNull(d);
        if (d.Distance?.Mutual is not { } mu) return null;
        if (mu.K0m is { } k) return DetMath.FromPolar(k.Magnitude, k.AngleDeg * Math.PI / 180.0);
        Branch own = bound.Model.Branches[d.Location.BranchIndex], par = bound.Model.Branches[mu.ParallelBranch];
        double sign = par.From == own.From ? 1.0 : -1.0;
        foreach (ZeroSequenceMutual m in bound.Model.ZeroSequenceMutuals)
            if ((m.BranchA == d.Location.BranchIndex && m.BranchB == mu.ParallelBranch) || (m.BranchB == d.Location.BranchIndex && m.BranchA == mu.ParallelBranch))
                return sign * new Complex(m.RPu, m.XPu) / (3.0 * new Complex(own.R, own.X));
        return null;
    }

    /// <summary>RC5 WP17: whether <paramref name="z"/> lies in a zone, a reverse zone testing -Z (its characteristic mirrored
    /// through the origin). A forward zone is <see cref="DistanceCharacteristics.InZone"/> unchanged.</summary>
    public static bool InZoneDirected(Complex z, DistanceZone zone, DistanceSetting setting)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return zone.Reverse ? DistanceCharacteristics.InZone(-z, zone, setting) : DistanceCharacteristics.InZone(z, zone, setting);
    }

    private static PlacedRow Distance(BoundDeviceSet bound, FaultPhasors f, PlacedDevice d, DistanceSetting s, int bi, double kv,
        Complex i0, (Complex A, Complex B, Complex C) ip, double maxPhasePu, double ibaseA, bool? where)
    {
        (Complex va, Complex vb, Complex vc) = f.PhaseVoltages(bi);
        double ohms = OhmsPerPu(f.Network!.Model.BaseMva, kv, d);
        // Memory: 0 = measured (Z = V / I); +1 / -1 = the loop voltage collapsed (ANA-02) and the prefault (memory)
        // voltage puts the fault forward / reverse; Z is then reported as 0 (the fault is at the relay point).
        var loops = new List<(string Name, Complex Z, int Memory)>(6);
        int loadBlocked = 0, collapsed = 0;
        Complex vpre = bi < f.PrefaultV1.Length ? f.PrefaultV1[bi] : Complex.Zero;
        (Complex pa, Complex pb, Complex pc) = FaultAnalysis.Phases(Complex.Zero, vpre, Complex.Zero);
        Complex lineRot = s.Zones.Count > 0 ? DetMath.FromPolar(1.0, -s.Zones[0].AngleDeg * Math.PI / 180.0) : Complex.One;
        // ANA-02: a loop voltage at or below MinDistanceLoopVoltageFraction of nominal is a bolted fault at the relay point.
        // The direction is the memory-polarised one: forward when Vmem / I lies within 90 degrees of the zone-1 angle.
        // Returns 0 (measured), +1 / -1 (collapsed, forward / reverse) or 2 (collapsed with no prefault voltage: the loop
        // cannot be measured and is left out).
        int noMemory = 0;
        int Memory(Complex v, Complex i, Complex vmem, double nominalPu)
        {
            if (!(v.Magnitude <= ProtectionContext.MinDistanceLoopVoltageFraction * nominalPu)) return 0;
            if (vmem == Complex.Zero) { noMemory++; return 2; }
            collapsed++;
            Complex w = vmem * Complex.Conjugate(i) * lineRot;
            return w.Real > 0.0 ? 1 : -1;
        }
        double phaseNominal = Math.Sqrt(3.0);
        if (s.Loops != DistanceLoops.Earth)
        {
            foreach ((string n, Complex v, Complex i, Complex vm) in new[] { ("ab", va - vb, ip.A - ip.B, pa - pb), ("bc", vb - vc, ip.B - ip.C, pb - pc), ("ca", vc - va, ip.C - ip.A, pc - pa) })
                if (i.Magnitude > ProtectionContext.MinApparentPowerPu)
                {
                    int mem = Memory(v, i, vm, phaseNominal);
                    if (mem == 2) continue;
                    if (mem != 0) { loops.Add((n, Complex.Zero, mem)); continue; }
                    Complex zl = v / i * ohms;
                    // RC5 WP17: the load blinder blocks a phase loop in the load region (only when it is set).
                    if (s.LoadEncroachment is { } le && LoadBlinder.Blocks(zl, le)) { loadBlocked++; continue; }
                    loops.Add((n, zl, 0));
                }
        }
        string? earthNote = null;
        if (s.Loops != DistanceLoops.Phase)
        {
            double residual = (3.0 * i0).Magnitude;
            Complex? k0 = K0(d, bound.Model.Branches[d.Location.BranchIndex]);
            if (k0 is null) earthNote = "earth loops not measured: the line has no usable Z1 for k0";
            else if (!(residual > ProtectionContext.MinApparentPowerPu && residual > ResidualRelease * maxPhasePu))
                earthNote = "earth loops not released (no residual current above the release fraction)";
            else
            {
                Complex comp = k0.Value * 3.0 * i0;
                // RC5 WP17: the parallel line's zero-sequence current, only when a mutual setting is given.
                if (s.Mutual is not null) comp += MutualTerm(bound, f, d);
                foreach ((string n, Complex v, Complex i, Complex vm) in new[] { ("a", va, ip.A, pa), ("b", vb, ip.B, pb), ("c", vc, ip.C, pc) })
                {
                    Complex il = i + comp;
                    if (!(il.Magnitude > ProtectionContext.MinApparentPowerPu)) continue;
                    int mem = Memory(v, il, vm, 1.0);
                    if (mem == 2) continue;
                    loops.Add(mem != 0 ? (n, Complex.Zero, mem) : (n, v / il * ohms, 0));
                }
            }
        }
        if (loops.Count == 0)
        {
            string none = earthNote ?? (loadBlocked > 0 ? "every phase loop is in the load region (blocked by the load blinder)"
                : noMemory > 0 ? "close-in fault, loop voltage collapsed and no prefault (memory) voltage: not measured" : "no loop carries current");
            return new PlacedRow(d.Id, d.Function, "21", true, none, double.NaN, "ohm sec", null, null, null,
                double.PositiveInfinity, double.PositiveInfinity, where) { LoadBlockedLoops = loadBlocked };
        }
        // A collapsed loop (fault at the relay point) lies in every zone of its memory direction and in none of the other.
        bool InLoopZone(int k, DistanceZone zone) => loops[k].Memory == 0
            ? InZoneDirected(loops[k].Z, zone, s)
            : (zone.Reverse ? loops[k].Memory < 0 : loops[k].Memory > 0);
        int best = -1, bestZone = 0; double bestT = double.PositiveInfinity;
        int picked = 0, pickedFaultLoops = 0;
        bool anyFaultLoop = false;
        for (int k = 0; k < loops.Count; k++)
        {
            bool faultLoop = IsFaultLoop(loops[k].Name, f.Type);
            anyFaultLoop |= faultLoop;
            for (int z = 0; z < s.Zones.Count; z++)
                if (InLoopZone(k, s.Zones[z]))
                {
                    picked |= 1 << z;
                    if (faultLoop) pickedFaultLoops |= 1 << z;
                }
            for (int z = 0; z < s.Zones.Count; z++)
            {
                if (!InLoopZone(k, s.Zones[z])) continue;
                double t = s.Zones[z].TimeS;
                if (t < bestT || (t == bestT && z + 1 < bestZone)) { best = k; bestZone = z + 1; bestT = t; }
                break;
            }
        }
        if (best < 0)
        {
            // No zone: report the loop with the smallest apparent impedance (first on a tie).
            best = 0;
            for (int k = 1; k < loops.Count; k++) if (loops[k].Z.Magnitude < loops[best].Z.Magnitude) best = k;
        }
        Complex zb = loops[best].Z;
        // RC5 WP17: switch-onto-fault, only on a fault the line has just been closed onto, only for the relay on that line
        // (a relay elsewhere was not just closed and keeps its zone timers), and only when it is set. Mode Z2 trips when
        // zone 1 or zone 2 picks up; nondirectional when a phase current exceeds the CT primary rating.
        bool sotf = false;
        if (s.Sotf is { } so && f.Fault.Energisation && f.Fault.Branch == d.Location.BranchIndex)
        {
            bool trips = so.Mode == SotfMode.Z2 ? (picked & 0b11) != 0 : maxPhasePu * ibaseA > d.CtPrimaryA;
            if (trips && bestT > 0.0) { bestT = 0.0; sotf = true; }
        }
        string? reason = earthNote;
        if (collapsed > 0)
        {
            string cn = loops[best].Memory != 0
                ? $"close-in fault, loop voltage collapsed: direction {(loops[best].Memory > 0 ? "forward" : "reverse")} from the prefault (memory) voltage"
                : "close-in fault: a loop voltage collapsed (direction from the prefault voltage)";
            reason = reason is null ? cn : reason + "; " + cn;
        }
        if (sotf) reason = reason is null ? "switch-onto-fault" : reason + "; switch-onto-fault";
        if (loadBlocked > 0)
        {
            string lb = $"{loadBlocked} phase loop(s) blocked by the load blinder";
            reason = reason is null ? lb : reason + "; " + lb;
        }
        return new PlacedRow(d.Id, d.Function, "21", true, reason, zb.Magnitude, "ohm sec", loops[best].Name, bestZone > 0 ? bestZone : null, null,
            bestT, bestT, where)
        {
            ApparentImpedanceOhms = zb, ZonesPicked = picked, SwitchOntoFault = sotf, LoadBlockedLoops = loadBlocked,
            FaultLoopZonesPicked = anyFaultLoop ? pickedFaultLoops : picked,
        };
    }

    /// <summary>RC5 (2.1.122): whether a measured loop is a loop of the faulted phases (the phase selection of the fault
    /// type, faults as <see cref="FaultAnalysis"/> applies them): three-phase every loop; single line to ground (phase a)
    /// the earth loop a; line to line (b-c) the phase loop bc; double line to ground (b-c) the loops bc, b and c.</summary>
    public static bool IsFaultLoop(string loop, FaultType type)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return type switch
        {
            FaultType.ThreePhase => true,
            FaultType.SingleLineToGround => loop == "a",
            FaultType.LineToLine => loop == "bc",
            FaultType.DoubleLineToGround => loop is "bc" or "b" or "c",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown fault type"),
        };
    }

    /// <summary>The dual-slope bias threshold (NPAG ch. 10): k1 Ib + Is1 up to Is2, then k2 Ib - (k2 - k1) Is2 + Is1.</summary>
    public static double DifferentialThreshold(double biasA, DifferentialSetting s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return biasA <= s.Is2A ? s.K1 * biasA + s.Is1A : s.K2 * biasA - (s.K2 - s.K1) * s.Is2A + s.Is1A;
    }

    /// <summary>One phase of the biased differential: Id = |I_near + I_far|, Ib = (|I_near| + |I_far|) / 2, both secondary
    /// amps into the protected branch; operate when Id exceeds the threshold.</summary>
    public static (double IdA, double IbA, double ThresholdA, bool Operates) DifferentialPhase(Complex nearA, Complex farA, DifferentialSetting s)
    {
        double id = (nearA + farA).Magnitude;
        double ibias = 0.5 * (nearA.Magnitude + farA.Magnitude);
        double thr = DifferentialThreshold(ibias, s);
        return (id, ibias, thr, id > thr);
    }

    private static PlacedRow Differential(FaultPhasors f, FaultNetwork net, PlacedDevice d, DifferentialSetting s, double ibaseNearA,
        (Complex A, Complex B, Complex C) near, bool? where)
    {
        int orig = d.Location.BranchIndex;
        bool farFromEnd = !d.Location.AtFromEnd;
        int farSeg = d.Location.AtFromEnd ? net.ToSegment(orig) : FaultNetwork.FromSegment(orig);
        Branch fb = net.Model.Branches[farSeg];
        int farBus = farFromEnd ? fb.From : fb.To;
        if (!f.TryBus(farBus, out int fi)) return Not(d, "87L", $"remote bus {farBus} is not in the fault study");
        double kvFar = net.Model.Buses[fi].BaseKv;
        if (!(kvFar > 0.0)) return Not(d, "87L", $"remote bus {farBus} has no base kV");
        double ibaseFarA = IBaseA(net.Model.BaseMva, kvFar);
        (Complex fa, Complex fbb, Complex fc) = f.PhaseCurrents(farSeg, farFromEnd);
        double nk = ibaseNearA / d.CtRatio, fk = ibaseFarA / s.RemoteCtRatio;
        string[] names = ["a", "b", "c"];
        Complex[] n = [near.A, near.B, near.C];
        Complex[] r = [fa, fbb, fc];
        int best = 0; double bestExcess = double.NegativeInfinity;
        var res = new (double Id, double Ib, double Thr, bool Op)[3];
        for (int p = 0; p < 3; p++)
        {
            res[p] = DifferentialPhase(n[p] * nk, r[p] * fk, s);
            double excess = res[p].Id - res[p].Thr;
            if (excess > bestExcess) { bestExcess = excess; best = p; }
        }
        bool op = res[best].Op;
        return new PlacedRow(d.Id, d.Function, "87L", true, null, res[best].Id, "A sec", names[best], null, null,
            op ? 0.0 : double.PositiveInfinity, op ? 0.0 : double.PositiveInfinity, where)
        { DifferentialA = res[best].Id, BiasA = res[best].Ib, ThresholdA = res[best].Thr };
    }

    private static readonly string[] PhaseNames = ["a", "b", "c"];

    /// <summary>RC5 WP18: an 87T row. The measured value and the differential columns are in pu of the transformer rating.</summary>
    private static PlacedRow Transformer87(FaultPhasors f, PlacedDevice d, bool? where)
    {
        TransformerDifferentialResult? r = TransformerDifferential.Evaluate(f, d, out string? why);
        if (r is null) return Not(d, "87T", why ?? "not evaluated");
        int p = r.DecidingPhase;
        double t = r.Operates ? 0.0 : double.PositiveInfinity;
        return new PlacedRow(d.Id, d.Function, "87T", true, null, r.IdPu[p], "pu", PhaseNames[p], null, null, t, t, where)
        { DifferentialA = r.IdPu[p], BiasA = r.IbPu[p], ThresholdA = r.ThresholdPu[p] };
    }

    /// <summary>RC5 WP18: an 87B row, in secondary amps of the reference CT. It has no direction (FaultDirection null).</summary>
    private static PlacedRow Bus87(BoundDeviceSet bound, FaultPhasors f, PlacedDevice d)
    {
        BusDifferentialResult? r = BusDifferential.Evaluate(bound, f, d, out string? why);
        if (r is null) return Not(d, "87B", why ?? "not evaluated");
        BusZoneResult z = r.Zone;
        int p = z.DecidingPhase;
        string? reason = z.Operates && r.CheckZone is { Operates: false } ? "the check zone does not operate" : null;
        double t = r.Operates ? 0.0 : double.PositiveInfinity;
        return new PlacedRow(d.Id, d.Function, "87B", true, reason, z.IdA[p], "A sec", PhaseNames[p], null, null, t, t, null)
        { DifferentialA = z.IdA[p], BiasA = z.IbA[p], ThresholdA = z.ThresholdA[p] };
    }

    // ---- canonical CSV --------------------------------------------------------------------------------------------

    private static string N(double v) => double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "";

    /// <summary>The rows as CSV, invariant culture and round-trip numbers (an empty cell for none, never-operates or NaN).</summary>
    public static string ToCsv(IReadOnlyList<PlacedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.Append("device,function,element,evaluated,reason,measured,unit,loop,zone,direction,angle_deg,z_real_ohm,z_imag_ohm,id_a,ib_a,threshold_a,time_s,operates\n");
        foreach (PlacedRow r in rows)
        {
            // B31 (CWE-1236): the device id (user text) and the reason pass the formula-injection guard; numbers never do.
            sb.Append(GridSim.Core.IO.CsvFormulaGuard.Neutralise(r.DeviceId)).Append(',').Append(r.Function).Append(',').Append(r.Element).Append(',').Append(r.Evaluated ? "true" : "false").Append(',')
              .Append(r.Reason is null ? "" : "\"" + GridSim.Core.IO.CsvFormulaGuard.Neutralise(r.Reason).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"").Append(',')
              .Append(N(r.MeasuredValue)).Append(',').Append(r.Unit).Append(',').Append(r.Loop).Append(',')
              .Append(r.Zone is int z ? z.ToString(CultureInfo.InvariantCulture) : "").Append(',').Append(r.Direction).Append(',')
              .Append(N(r.AngleDifferenceDeg)).Append(',').Append(N(r.ApparentImpedanceOhms.Real)).Append(',').Append(N(r.ApparentImpedanceOhms.Imaginary)).Append(',')
              .Append(N(r.DifferentialA)).Append(',').Append(N(r.BiasA)).Append(',').Append(N(r.ThresholdA)).Append(',')
              .Append(N(r.TimeS)).Append(',').Append(r.Operates ? "true" : "false").Append('\n');
        }
        return sb.ToString();
    }
}
