using System.Globalization;
using System.Text.Json;

namespace GridSim.Core.IO.Json;

/// <summary>
/// Reads a JSON integer member the way the schema validator judges one: any whole number in the int range. The schema
/// accepts <c>1.0</c> as an integer (JSON Schema does), but <see cref="JsonElement.GetInt32"/> throws a bare
/// <see cref="FormatException"/> on it, which named no member and was not an <see cref="InvalidDataException"/>, so a
/// file written by a tool that prints whole numbers as <c>1.0</c> validated and then failed to load. A fraction or a
/// value beyond the int range (<c>3e9</c> also passes the schema) is refused with <see cref="InvalidDataException"/>
/// naming the member.
/// </summary>
internal static class JsonWhole
{
    /// <summary>The whole-number value of <paramref name="value"/>, member <paramref name="name"/>.</summary>
    public static int Int(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out int i)) return i;
            if (value.TryGetDouble(out double d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue)
                return (int)d;
        }
        throw new InvalidDataException(
            $"'{name}' is {value.GetRawText()}; it must be a whole number between {int.MinValue.ToString(CultureInfo.InvariantCulture)} " +
            $"and {int.MaxValue.ToString(CultureInfo.InvariantCulture)}.");
    }

    /// <summary>The whole-number value of member <paramref name="p"/>.</summary>
    public static int Int(JsonProperty p) => Int(p.Value, p.Name);
}
