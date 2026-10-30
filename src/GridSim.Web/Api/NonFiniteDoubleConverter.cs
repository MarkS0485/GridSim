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
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}
