using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Dynamics.Models.Adaptive;

/// <summary>Where the relays read their primary currents from: given the network in force and the machine EMF phasors of
/// the current state, fill <paramref name="amps"/> (one primary current magnitude in amperes per relay, in the order of
/// <see cref="RelayInLoop"/>). A pure function of its arguments.</summary>
public interface IRelayCurrentSource
{
    /// <summary>Write the relay primary currents (A) for this network and these EMFs.</summary>
    void PrimaryCurrentsA(IRmsNetwork network, Complex[] emf, double[] amps);
}

/// <summary>What a relay is doing at a given current: <see cref="Inactive"/> below pickup, <see cref="Timed"/> while its
/// operate time is a finite time that integrates, <see cref="Instant"/> when its operate time is no longer than the event
/// time tolerance (it trips when it enters this class).</summary>
public enum RelayClass { Inactive, Timed, Instant }

/// <summary>A device of the set that the loop does not evaluate, with the reason (never silently dropped).</summary>
public sealed record RelayNotEvaluated(string DeviceId, string Reason);

/// <summary>
/// RC4 R4-14: the relays-in-the-loop adapter over the R4-07 placed devices
/// (<see cref="BoundDeviceSet"/>). Each evaluated device (relay or recloser with function 50, 51 or none, and fuses) is
/// driven by its own operate-time characteristic through <see cref="DeviceTiming.Seconds"/> (shot 0, the clearing time),
/// the same function the coordination workflow uses, so the loop and the workflow can never disagree about a curve.
/// <para>
/// <b>Disc model.</b> Each device carries one state, the fraction <c>r</c> of its operate time used up. While the current
/// is above pickup, <c>dr/dt = 1 / t_op(I(t))</c>; the device trips when <c>r</c> reaches 1. At a constant current this
/// gives a trip delay of exactly <c>t_op(I)</c>. When the current falls below pickup the fraction resets to zero at once
/// (an instantaneous reset; the reset characteristic of IEC 60255-151 is not modelled and is stated as such). A device
/// whose operate time at the present current is not longer than the event tolerance (an instantaneous element) trips as
/// soon as it picks up.
/// </para>
/// <para>
/// Recloser sequences are not run in the loop: a recloser contributes its first shot and its trip stays open (stated in
/// the result). Functions 50N, 51N, 67 and 21 and breakers are listed as not evaluated.
/// </para>
/// </summary>
public sealed class RelayInLoop
{
    private readonly List<PlacedDevice> _devices = new();
    private readonly List<RelayNotEvaluated> _skipped = new();
    private readonly CurveLibrary _library;
    private readonly double _instantSeconds;

    /// <summary>Build the loop devices from a bound device set. <paramref name="instantSeconds"/> is the operate time at or below
    /// which a device counts as instantaneous (the engine passes its event time tolerance).</summary>
    public RelayInLoop(BoundDeviceSet bound, double instantSeconds)
    {
        ArgumentNullException.ThrowIfNull(bound);
        if (!(instantSeconds > 0.0) || !double.IsFinite(instantSeconds))
            throw new ArgumentOutOfRangeException(nameof(instantSeconds), instantSeconds, "The instantaneous threshold must be positive and finite.");
        _library = bound.Library;
        _instantSeconds = instantSeconds;
        foreach (PlacedDevice d in bound.Devices)
        {
            if (bound.OutOfService(d)) { _skipped.Add(new RelayNotEvaluated(d.Id, "its branch is out of service in the case")); continue; }
            if (d.Type == DeviceType.Breaker) { _skipped.Add(new RelayNotEvaluated(d.Id, "a breaker has no protection function of its own (it is the actuator)")); continue; }
            if (d.Type is DeviceType.Relay or DeviceType.Recloser && !d.IsEvaluatedFunction)
            { _skipped.Add(new RelayNotEvaluated(d.Id, $"function {d.Function} is not evaluated from the balanced RMS currents")); continue; }
            double kv = bound.Model.Buses.FirstOrDefault(b => b.Id == d.Location.NearBus)?.BaseKv ?? 0.0;
            if (!(kv > 0.0)) { _skipped.Add(new RelayNotEvaluated(d.Id, $"bus {d.Location.NearBus} has no base voltage, so a per-unit current cannot be converted to amperes")); continue; }
            _devices.Add(d);
        }
    }

    /// <summary>A loop over an explicit device list and library (tests and callers that bind themselves); every device is
    /// taken as evaluated.</summary>
    public RelayInLoop(IReadOnlyList<PlacedDevice> devices, CurveLibrary library, double instantSeconds)
    {
        ArgumentNullException.ThrowIfNull(devices); ArgumentNullException.ThrowIfNull(library);
        if (!(instantSeconds > 0.0) || !double.IsFinite(instantSeconds))
            throw new ArgumentOutOfRangeException(nameof(instantSeconds), instantSeconds, "The instantaneous threshold must be positive and finite.");
        _library = library;
        _instantSeconds = instantSeconds;
        _devices.AddRange(devices);
    }

    /// <summary>Number of evaluated devices.</summary>
    public int Count => _devices.Count;

    /// <summary>Device <paramref name="i"/> (device-number order of the set).</summary>
    public PlacedDevice Device(int i) => _devices[i];

    /// <summary>Devices that are in the set but not evaluated, with reasons.</summary>
    public IReadOnlyList<RelayNotEvaluated> NotEvaluated => _skipped;

    /// <summary>The operate time (s) of device <paramref name="i"/> at a constant primary current: the curve equation or table
    /// of the first shot (the clearing time of a fuse); +infinity below pickup.</summary>
    public double OperateSeconds(int i, double primaryAmps) =>
        DeviceTiming.Seconds(_devices[i], 0, TimingRole.Clearing, primaryAmps, _library, out _);

    /// <summary>The class of device <paramref name="i"/> at this current.</summary>
    public RelayClass Classify(int i, double primaryAmps)
    {
        double t = OperateSeconds(i, primaryAmps);
        if (double.IsInfinity(t) || double.IsNaN(t)) return RelayClass.Inactive;
        return t <= _instantSeconds ? RelayClass.Instant : RelayClass.Timed;
    }

    /// <summary>The rate <c>dr/dt</c> of a timed device at this current (zero for any other class).</summary>
    public double Rate(int i, double primaryAmps) => Classify(i, primaryAmps) == RelayClass.Timed ? 1.0 / OperateSeconds(i, primaryAmps) : 0.0;
}
