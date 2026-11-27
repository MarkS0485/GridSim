using System;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.LongTerm;

/// <summary>
/// WP21 (RC5): the timing of one on-load tap changer in a long-term run. The tap changer itself (target, deadband, step,
/// limits) is the model's <see cref="Oltc"/> record named by <see cref="Transformer"/>; the timing is kept here, in the
/// long-term input, not on the record.
/// </summary>
/// <param name="Transformer">The <see cref="Oltc.Transformer"/> name of the model's tap changer.</param>
/// <param name="FirstDelaySeconds">Tdel: how long the controlled voltage must stay out of band before the first move (s).</param>
/// <param name="NextDelaySeconds">Tdelstep: the time between later moves while it stays out (s).</param>
public sealed record OltcTimerSpec(string Transformer, double FirstDelaySeconds, double NextDelaySeconds);

/// <summary>
/// WP21 (RC5): the timer of one tap changer. Each QSS step it reads the controlled voltage of the step's equilibrium. Out of
/// the band <c>target +/- deadband</c> it counts one more whole step; when the count reaches <c>ceil(Tdel / h)</c> steps
/// (first move) or <c>ceil(Tdelstep / h)</c> steps (later moves) the tap moves one step toward the band, effective at the
/// next equilibrium, and the count restarts. Back in band the count and the first-move state reset. The time is counted in
/// whole steps, never summed from h. The direction rule is the steady-state tap loop's
/// (<see cref="GridSim.Core.PowerFlow.SteadyStateSolver"/>): the tap sits on the From end, so a controlled bus at the From
/// end rises with the tap and any other falls with it; a tap at a limit holds.
/// </summary>
internal sealed class OltcTimer
{
    private readonly Oltc _o;
    private readonly OltcTimerSpec _t;
    private readonly bool _controlsTappedEnd;
    private long _count;
    private bool _moved;

    internal OltcTimer(Oltc record, OltcTimerSpec timing, Branch transformer, int branchIndex)
    {
        _o = record;
        _t = timing;
        BranchIndex = branchIndex;
        _controlsTappedEnd = record.ControlsBus == transformer.From && record.ControlsBus != transformer.To;
        Tap = transformer.TapRatio;
    }

    /// <summary>The transformer's branch index in the model.</summary>
    internal int BranchIndex { get; }

    /// <summary>The controlled bus.</summary>
    internal int ControlsBus => _o.ControlsBus;

    /// <summary>The transformer name.</summary>
    internal string Transformer => _o.Transformer;

    /// <summary>The present tap ratio.</summary>
    internal double Tap { get; private set; }

    /// <summary>The model's tap changer record.</summary>
    internal Oltc Record => _o;

    /// <summary>Advance one step <paramref name="h"/> with the controlled voltage <paramref name="vPu"/>. Returns true when
    /// the tap moved (the new tap is <see cref="Tap"/>).</summary>
    internal bool Advance(double vPu, double h)
    {
        bool low = vPu < _o.TargetVpu - _o.DeadbandPu, high = vPu > _o.TargetVpu + _o.DeadbandPu;
        if (!low && !high)
        {
            _count = 0;
            _moved = false;
            return false;
        }
        _count++;
        double delay = _moved ? _t.NextDelaySeconds : _t.FirstDelaySeconds;
        if (!StepTimer.Reached(_count, delay, h)) return false;
        double tap = Tap;
        double TapDown() => tap <= _o.MinTap ? tap : Math.Max(_o.MinTap, tap - _o.StepPu);
        double TapUp() => tap >= _o.MaxTap ? tap : Math.Min(_o.MaxTap, tap + _o.StepPu);
        double newTap = low ? (_controlsTappedEnd ? TapUp() : TapDown()) : (_controlsTappedEnd ? TapDown() : TapUp());
        if (newTap == tap) return false;   // at a limit: the tap holds (and stays due)
        Tap = newTap;
        _moved = true;
        _count = 0;
        return true;
    }
}
