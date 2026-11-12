using System.Text.Json;
using System.Text.Json.Serialization;

namespace GridSim.Web.Api;

/// <summary>
/// Emits non-finite doubles (+/-inf / NaN) as JSON <c>null</c> instead of throwing. GDA generators can carry
/// +/-inf Q-limits and RP1 purchased time can be +inf by design; strict Web serialization would otherwise
/// throw mid-send (GS-H7). Shared by the /ws/sim stream and the REST endpoints - /api/solve used to 500 on
/// any model carrying an infinite limit because only the WebSocket path registered the converter.
/// </summary>
public sealed class NonFiniteDoubleConverter : JsonConverter<double>
{
    // GR-717 (November 2026): a custom converter replaces the serializer's own double handling, including the ASP.NET
    // "Web" default that reads a number written as a string ("1.5"). This one used to call GetDouble() alone, which
    // throws on a string token, so the same request body read differently with and without the converter. It now reads a
    // string the way the serializer's own reader does for the options it is given: only when
    // JsonNumberHandling.AllowReadingFromString is set (the Web defaults set it), only a plain invariant number (an
    // optional sign, digits, a decimal point, an exponent; no spaces, no thousands separators), and the names NaN,
    // Infinity and -Infinity only when AllowNamedFloatingPointLiterals is set. Anything else, and a number too large for a
    // double, is a JsonException (fail loud), exactly as the serializer would throw. Writing is unchanged: a non-finite
    // double is still written as null.
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) return reader.GetDouble();

        ArgumentNullException.ThrowIfNull(options);
        if ((options.NumberHandling & JsonNumberHandling.AllowReadingFromString) == 0)
            throw new JsonException("A number written as a string is not accepted here (JsonNumberHandling.AllowReadingFromString is not set).");

        string text = reader.GetString() ?? "";
        if ((options.NumberHandling & JsonNumberHandling.AllowNamedFloatingPointLiterals) != 0)
        {
            if (text == "NaN") return double.NaN;
            if (text == "Infinity") return double.PositiveInfinity;
            if (text == "-Infinity") return double.NegativeInfinity;
        }
        const System.Globalization.NumberStyles plain = System.Globalization.NumberStyles.AllowLeadingSign
            | System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowExponent;
        if (double.TryParse(text, plain, System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
            return value;
        throw new JsonException($"The string \"{text}\" is not a number that fits a double.");
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}
