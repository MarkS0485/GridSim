using System;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The one rule for the MVA base a machine's per-unit dynamic data (inertia H, reactances X'd, X''d, ...) is
/// written on, shared by the classical (<see cref="Distributed.ClassicalInit"/>) and the subtransient
/// (<see cref="SubtransientInit"/>) initialisations (D-DOC-055, docs-session finding, Ruling R1: Mark delegated
/// 2026-10-04).
///
/// <para>The rule: a positive <see cref="Generator.MBase"/> is the machine base; a zero or negative MBase means "not
/// given" and the SYSTEM base (<see cref="GridModel.BaseMva"/>) is used instead, which is the MATPOWER convention (its
/// parser already reads a non-positive mBase as the system base, and the MATPOWER writer notes it). A NaN or infinite
/// MBase is not a base at all and is refused (fail loud): it used to turn every reactance and the inertia of that
/// machine into NaN or zero without a word.</para>
///
/// <para>Why one rule: before D-DOC-055 the two initialisations disagreed. SubtransientInit already fell back to the
/// system base; ClassicalInit floored the divisor at 1e-6 MVA (so a zero MBase made X'd on the system base 1e8 times
/// the machine value) and scaled H by the raw MBase (so a zero MBase gave H = 0 on the system base, which the swing
/// divides by). No bundled case reaches the fallback: every bundled generator with a non-positive capacity has H = 0,
/// so the classical study holds it as a constant injection (or refuses the case for want of inertia) before its MBase
/// is read, and the MATPOWER parser has already replaced a non-positive mBase with the system base.</para>
/// </summary>
public static class MachineBase
{
    /// <summary>
    /// The MVA base of <paramref name="gen"/>'s dynamic data: its <see cref="Generator.MBase"/> when that is positive,
    /// else <paramref name="systemBaseMva"/>. Throws <see cref="ArgumentException"/> for a NaN or infinite MBase.
    /// For every finite positive MBase the value returned is that MBase itself, so the callers' numbers are unchanged
    /// bit for bit on every such machine.
    /// </summary>
    /// <param name="gen">The generator whose base is wanted.</param>
    /// <param name="systemBaseMva">The model's system base (MVA), used when the generator gives no base of its own.</param>
    public static double EffectiveMva(Generator gen, double systemBaseMva)
    {
        ArgumentNullException.ThrowIfNull(gen);

        // A NaN or infinite base cannot be "not given": it is a corrupt value, and every per-unit conversion through
        // it would be NaN, zero or infinite. Refuse it, naming the unit.
        if (!double.IsFinite(gen.MBase))
            throw new ArgumentException(
                $"Generator {gen.Name ?? "(unnamed)"} at bus {gen.Bus} has MBase = {gen.MBase}: a machine base must be a "
                + "finite number (a zero or negative MBase means 'not given' and the system base is used).", nameof(gen));

        // Positive: the machine's own rating. Zero or negative: not given, so the system base (MATPOWER convention).
        return gen.MBase > 0.0 ? gen.MBase : systemBaseMva;
    }
}
