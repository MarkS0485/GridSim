using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Impedance;

/// <summary>How much inner-loop control detail a converter admittance model carries.</summary>
public enum ImpedanceModelDetail
{
    /// <summary>Only the behavioural outer P-f / df-dt loop; the PLL is ideal (grid-following: its gain frozen at
    /// 1/Vd0) and the current loop is the fast stand-in roll-off. A first screen when no inner-loop parameters are
    /// available. The CLI selects it with `impedance-stability --outer-loop-only` (R082, October 2026; grid-following
    /// only, since the grid-forming admittance does not use the detail level).</summary>
    OuterLoopOnly,
    /// <summary>Outer loop + the SRF-PLL closed loop (the weak-grid negative-resistance physics). The current loop is
    /// NOT unity: grid-following models represent it as a fast first-order roll-off at
    /// <c>GridFollowingImpedance.IdealCurrentLoopHz</c> (1,000 Hz), well above the PLL band. (R082: this doc used to
    /// say "ideal (unity)".)</summary>
    PllOuter,
    /// <summary>Outer loop + SRF-PLL + a first-order current loop (super-/sub-synchronous fidelity).</summary>
    PllCurrentOuter,
}

/// <summary>
/// The linearisation operating point of a converter, taken from the solved power flow at its terminal bus:
/// the aligned dq terminal voltage and injected current, and the active/reactive power. The PLL aligns the d
/// axis to the terminal voltage, so <c>Vq0 ~ 0</c> and <c>Vd0 ~ |V|</c>; <see cref="Guarded"/> clamps
/// <c>Vd0</c> away from zero (the <c>1/Vd0</c> singularity of the PLL gain - the E=0 analog of this
/// workstream) and documents it.
/// </summary>
/// <param name="Vd0Pu">Aligned d-axis terminal voltage (pu), ~|V|.</param>
/// <param name="Vq0Pu">Aligned q-axis terminal voltage (pu), ~0 after PLL alignment.</param>
/// <param name="Id0Pu">Injected d-axis current (pu on the converter rating).</param>
/// <param name="Iq0Pu">Injected q-axis current (pu on the converter rating).</param>
/// <param name="PPu">Injected active power (pu on the converter rating).</param>
/// <param name="QPu">Injected reactive power (pu on the converter rating).</param>
public sealed record ConverterOperatingPoint(
    double Vd0Pu, double Vq0Pu, double Id0Pu, double Iq0Pu, double PPu, double QPu)
{
    /// <summary>The smallest d-axis voltage the model will linearise about. Below this the SRF-PLL gain
    /// <c>1/Vd0</c> blows up and the small-signal model is meaningless, so the value is clamped and the
    /// clamp is reported (see <see cref="Guarded"/>). 0.05 pu = a deeply depressed weak-grid terminal.</summary>
    public const double MinVd0Pu = 0.05;

    /// <summary>True when the raw d-axis voltage was below <see cref="MinVd0Pu"/> and had to be clamped by
    /// <see cref="Guarded"/> (recorded at construction, since after clamping <c>Vd0Pu == MinVd0Pu</c>).</summary>
    public bool Vd0WasClamped { get; init; }

    /// <summary>
    /// A copy with <c>Vd0</c> clamped to at least <see cref="MinVd0Pu"/>. Aligning the PLL sets
    /// <c>Vq0 = 0</c> and <c>Vd0 = sqrt(Vd0^2 + Vq0^2)</c>; this factory both aligns and guards, so no caller
    /// can hand a <c>Vd0 -&gt; 0</c> point to the admittance derivation and silently produce garbage.
    /// <para><paramref name="vaRad"/> is accepted for call-site symmetry but NOT used: the point is expressed in the
    /// PLL-aligned dq frame, where the terminal angle is zero by construction, so the bus angle carries no
    /// information here (R082: it used to be discarded without saying so).</para>
    /// </summary>
    public static ConverterOperatingPoint Guarded(double vmPu, double vaRad, double idPu, double iqPu, double pPu, double qPu)
    {
        _ = vaRad;   // unused by design: see the remarks above (dq frame aligned to the terminal voltage)
        double vMag = Math.Abs(vmPu);
        double vd0 = Math.Max(MinVd0Pu, vMag);   // PLL-aligned d axis = |V|, guarded away from 0
        return new ConverterOperatingPoint(vd0, 0.0, idPu, iqPu, pPu, qPu) { Vd0WasClamped = vMag < MinVd0Pu };
    }
}

/// <summary>
/// The small-signal admittance model of a converter about a fixed operating point - the object the
/// impedance-based (generalised-Nyquist) stability screen forms the return ratio from. Grid-following and
/// grid-forming units implement this from their authored control parameters plus the opt-in inner-loop
/// parameters (PLL / current-loop bandwidth) the behavioural time-domain models lack.
/// </summary>
public interface IConverterImpedanceModel
{
    /// <summary>Terminal bus id.</summary>
    int Bus { get; }

    /// <summary>The device archetype (grid-following / grid-forming).</summary>
    AssetKind Kind { get; }

    /// <summary>How much inner-loop detail this model carries.</summary>
    ImpedanceModelDetail Detail { get; }

    /// <summary>Provenance of the model: <see cref="ProvenanceTag.Recorded"/> when the inner-loop parameters
    /// are measured, else <see cref="ProvenanceTag.CuratedPlaceholder"/> (literature-typical, [VERIFY]).</summary>
    ProvenanceTag Provenance { get; }

    /// <summary>The linearisation operating point (guarded).</summary>
    ConverterOperatingPoint OperatingPoint { get; }

    /// <summary>
    /// The small-signal dq admittance <c>Y_dq(jw)</c> [pu] the converter DRAWS at its terminal, at
    /// perturbation angular frequency <paramref name="omega"/> (rad/s). A genuine full 2x2 (both
    /// off-diagonals nonzero when the axes are coupled by the PLL and the outer loop).
    /// </summary>
    Complex2x2 AdmittanceDq(double omega);

    /// <summary>
    /// The positive-sequence small-signal admittance <c>Y+(jw)</c> [pu] - the modified-sequence reduction of
    /// <see cref="AdmittanceDq"/>, <c>Y_pp = 1/2[(Y_dd+Y_qq) + j(Y_qd - Y_dq)]</c> - used by the scalar
    /// minor-loop Nyquist.
    /// </summary>
    Complex PositiveSequenceAdmittance(double omega);
}
