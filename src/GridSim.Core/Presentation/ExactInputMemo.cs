namespace GridSim.Core.Presentation;

/// <summary>
/// R131 (October 2026 review, stream P): remembers the exact inputs of the last completed computation, so a presentation
/// surface can skip recomputing when nothing it reads has changed.
///
/// <para><b>Why.</b> The web TRIP / DEVICE tab and the TSGB what-if card recompute in <c>OnParametersSet</c>, which Blazor
/// calls on every Dashboard render (each live frame and each 4 s presence heartbeat). The TRIP tab's auto mode runs
/// <c>TsgbSizer.SizeForTargets</c> (up to 8 growth and 44 bisection swing simulations) plus two more swings every time,
/// although the inertia, demand, loss and the user's inputs have not changed.</para>
///
/// <para><b>Why the shown numbers cannot change.</b> The computations are pure functions of their inputs (no clock, no
/// random number, no shared state). This memo reports "unchanged" only when every input is the same to the BIT (doubles
/// compared by their IEEE 754 bit patterns, so +0 and -0, or two NaNs with different payloads, count as different; texts
/// compared ordinally), and the surface then keeps the results of the previous run on those very inputs, which are the
/// results a new run would give. The finding's suggestion, rounding the inputs to display precision before comparing,
/// is NOT used: two inputs that round alike can give different results, so it could show numbers computed from inputs
/// other than the current ones.</para>
///
/// <para>Use: call <see cref="IsUnchanged"/> before computing (skip when true), and <see cref="Remember"/> after the
/// computation completed. A computation that throws is never remembered, so it runs again next time. Not thread-safe
/// (one memo per component instance, used on its renderer's thread).</para>
/// </summary>
public sealed class ExactInputMemo
{
    private string? _text;
    private long[]? _bits;

    /// <summary>True when a computation has been remembered and <paramref name="text"/> and every one of
    /// <paramref name="inputs"/> are bit for bit the remembered ones (same count, same order).</summary>
    public bool IsUnchanged(string? text, params double[] inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (_bits is null) return false;
        if (!string.Equals(_text, text, StringComparison.Ordinal)) return false;
        if (_bits.Length != inputs.Length) return false;
        for (int i = 0; i < inputs.Length; i++)
            if (_bits[i] != BitConverter.DoubleToInt64Bits(inputs[i])) return false;
        return true;
    }

    /// <summary>Remember <paramref name="text"/> and <paramref name="inputs"/> as the inputs of the computation that has
    /// just completed (replacing whatever was remembered).</summary>
    public void Remember(string? text, params double[] inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var bits = new long[inputs.Length];
        for (int i = 0; i < inputs.Length; i++) bits[i] = BitConverter.DoubleToInt64Bits(inputs[i]);
        _text = text;
        _bits = bits;
    }

    /// <summary>Forget the remembered inputs, so the next <see cref="IsUnchanged"/> is false.</summary>
    public void Forget()
    {
        _text = null;
        _bits = null;
    }
}
