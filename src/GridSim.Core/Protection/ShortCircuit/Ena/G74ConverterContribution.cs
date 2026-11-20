namespace GridSim.Core.Protection.ShortCircuit.Ena;

/// <summary>
/// Converter-interfaced plant under the R4-06 method. The positive-sequence model is the incumbent one: a current
/// source <c>I = k I_r</c> (<see cref="Model.Generator.EffectiveConverterFaultCurrentPu"/>) reaching the fault as
/// <c>|Z_kj| / |Z_kk|</c> of itself, with no AC decay (a controlled current). ENA ER G74 Issue 2 is reported to improve
/// the representation of the current-limited contribution of converters; the negative-sequence behaviour and any
/// time or voltage dependence it prescribes are AWAITING STANDARD TEXT and are NOT modelled: this build adds no
/// negative- or zero-sequence converter infeed, exactly as <c>fault</c> and <c>iec60909</c> do today.
/// </summary>
public static class G74ConverterContribution
{
    public const string PositiveSequenceModel = "current-limited positive sequence (I = k x rated, constant in time)";
    public const string NegativeSequenceModel = "none: G74 negative-sequence behaviour AWAITING STANDARD TEXT (no converter negative-sequence infeed added)";
}
