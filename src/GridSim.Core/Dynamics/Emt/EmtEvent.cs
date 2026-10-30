namespace GridSim.Core.Dynamics.Emt;

/// <summary>A scheduled discrete change to the network at a given instant. Applied on the first timestep whose
/// time reaches <see cref="AtSeconds"/>; the engine damps the resulting discontinuity with a Critical Damping
/// Adjustment (two backward-Euler half-steps) unless that is disabled.</summary>
public abstract record EmtEvent(double AtSeconds);

/// <summary>Open or close a <see cref="SwitchElement"/> at <see cref="EmtEvent.AtSeconds"/>. A fault is applied
/// by closing a bus-to-ground switch, and cleared by opening it.</summary>
/// <param name="AtSeconds">When the switch changes state.</param>
/// <param name="ElementId">The <see cref="SwitchElement.Id"/> to operate.</param>
/// <param name="Close">True to close (conductance -&gt; 1/Ron), false to open (-&gt; 1/Roff).</param>
public sealed record SwitchEvent(double AtSeconds, int ElementId, bool Close) : EmtEvent(AtSeconds);
