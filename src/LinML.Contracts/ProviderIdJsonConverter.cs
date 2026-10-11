using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinML.Contracts;

/// <summary>
/// Serializes a <see cref="ProviderId"/> as its plain string value
/// (e.g. <c>"CPUExecutionProvider"</c>), matching diagnostics-schema.md.
/// </summary>
public sealed class ProviderIdJsonConverter : JsonConverter<ProviderId>
{
    public override ProviderId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value is null ? null : new ProviderId(value);
    }

    public override void Write(Utf8JsonWriter writer, ProviderId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
