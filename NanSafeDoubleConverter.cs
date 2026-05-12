// Core/NanSafeDoubleConverter.cs
// System.Text.Json refuses to write double.NaN / Infinity as JSON number literals
// because the JSON spec has no concept of IEEE 754 special values.
// This converter maps NaN / ±Infinity → JSON null on write, and
// JSON null → double.NaN on read, keeping the object model intact.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkTopologyProbing.Core;

/// <summary>
/// A <see cref="JsonConverter{T}"/> for <c>double</c> that serialises
/// <c>NaN</c> and <c>±Infinity</c> as JSON <c>null</c> and deserialises
/// JSON <c>null</c> back to <c>double.NaN</c>.
/// Register once in every <see cref="JsonSerializerOptions"/> instance that
/// may encounter models with uninitialised metric fields.
/// </summary>
public sealed class NanSafeDoubleConverter : JsonConverter<double>
{
    public static readonly NanSafeDoubleConverter Instance = new();

    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            reader.Read();   // consume the null token
            return double.NaN;
        }
        return reader.GetDouble();
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            writer.WriteNullValue();
        else
            writer.WriteNumberValue(value);
    }
}
