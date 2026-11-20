using System.Globalization;

namespace GridSim.Core.Dynamics.UserModels;

/// <summary>
/// The dimension of a signal or parameter in a declarative model. The only physical dimension tracked is time
/// (exponent of seconds): per-unit quantities are dimensionless ratios to a declared base, so <c>pu</c> and <c>1</c> are
/// the same dimension and <c>pu/s</c> equals <c>1/s</c>. That is enough to refuse the errors that matter in a block
/// diagram: a time constant given as a per-unit number, a sum of a rate and a level, an integrator limit in the wrong
/// unit. Grammar: <c>term (('*' | '/') term)*</c> with <c>term</c> one of <c>pu</c>, <c>1</c>, <c>s</c>.
/// </summary>
public readonly record struct ModelUnit(int SecondsExponent)
{
    /// <summary>Per-unit or dimensionless.</summary>
    public static readonly ModelUnit One = new(0);

    /// <summary>Seconds.</summary>
    public static readonly ModelUnit Seconds = new(1);

    /// <summary>Product of two dimensions.</summary>
    public ModelUnit Times(ModelUnit o) => new(SecondsExponent + o.SecondsExponent);

    /// <summary>Quotient of two dimensions.</summary>
    public ModelUnit Over(ModelUnit o) => new(SecondsExponent - o.SecondsExponent);

    /// <summary>Canonical text: <c>pu</c>, <c>s</c>, <c>1/s</c>, <c>s^2</c>, <c>s^-2</c>.</summary>
    public override string ToString() => SecondsExponent switch
    {
        0 => "pu",
        1 => "s",
        -1 => "1/s",
        _ => "s^" + SecondsExponent.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Parse a unit text; null when it is outside the grammar.</summary>
    public static ModelUnit? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        int exp = 0;
        int i = 0;
        int sign = 1;
        while (i < t.Length)
        {
            int j = i;
            while (j < t.Length && t[j] != '*' && t[j] != '/') j++;
            string term = t.Substring(i, j - i).Trim();
            switch (term)
            {
                case "pu":
                case "1": break;
                case "s": exp += sign; break;
                default: return null;
            }
            if (j >= t.Length) break;
            sign = t[j] == '/' ? -1 : 1;
            // '/' applies to the next term only; '*' resets to positive. (a/b*c) is read left to right: a, then /b, then *c.
            i = j + 1;
            if (i >= t.Length) return null;
        }
        return new ModelUnit(exp);
    }
}
