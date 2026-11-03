using System.Buffers;
using System.Text;
using System.Text.Json;
using Rag.NET.Models;

namespace Silvia.Storage.Sqlite;

/// <summary>
/// Stores chunk metadata as JSON that keeps each value's <see cref="MetadataValueKind"/>, so a
/// number round-trips as a number and a date as a date. Each key maps to an object
/// <c>{"k": kind, "v": value}</c>, where <c>k</c> is <c>s</c>, <c>n</c>, <c>b</c> or <c>d</c>.
/// Filters compare the <c>k</c> and <c>v</c> fields with <c>json_extract</c>, so the database
/// never has to parse text it does not need to.
/// </summary>
internal static class MetadataCodec
{
    public static string Encode(IDictionary<string, MetadataValue>? metadata)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (metadata is not null)
            {
                foreach (var (key, value) in metadata)
                {
                    ValidateKey(key);
                    writer.WriteStartObject(key);
                    writer.WriteString("k", KindCode(value.Kind));
                    WriteValue(writer, value);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static IDictionary<string, MetadataValue> Decode(string json)
    {
        var result = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var kind = property.Value.GetProperty("k").GetString();
            var value = property.Value.GetProperty("v");
            result[property.Name] = kind switch
            {
                "s" => value.GetString() ?? string.Empty,
                "n" => value.GetDouble(),
                "b" => value.GetBoolean(),
                "d" => DateTimeOffset.Parse(value.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
                _ => throw new InvalidDataException($"Unknown metadata kind '{kind}' for key '{property.Name}'."),
            };
        }

        return result;
    }

    /// <summary>
    /// The JSON paths of a key's kind and value fields. Keys are restricted to characters that
    /// need no escaping in a JSON path, so a key can never change the shape of the query.
    /// </summary>
    public static (string KindPath, string ValuePath) Paths(string key)
    {
        ValidateKey(key);
        return ($"$.\"{key}\".k", $"$.\"{key}\".v");
    }

    /// <summary>The kind code and the value a filter parameter must match for <paramref name="value"/>.</summary>
    public static (string Kind, object Value) FilterParameter(MetadataValue value) => value.Kind switch
    {
        MetadataValueKind.String => ("s", value.StringValue),
        MetadataValueKind.Number => ("n", value.NumberValue),
        MetadataValueKind.Boolean => ("b", value.BooleanValue ? 1L : 0L),
        MetadataValueKind.DateTimeOffset => ("d", FormatDate(value.DateTimeOffsetValue)),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unsupported metadata kind."),
    };

    private static void WriteValue(Utf8JsonWriter writer, MetadataValue value)
    {
        switch (value.Kind)
        {
            case MetadataValueKind.String:
                writer.WriteString("v", value.StringValue);
                break;
            case MetadataValueKind.Number:
                writer.WriteNumber("v", value.NumberValue);
                break;
            case MetadataValueKind.Boolean:
                writer.WriteBoolean("v", value.BooleanValue);
                break;
            case MetadataValueKind.DateTimeOffset:
                writer.WriteString("v", FormatDate(value.DateTimeOffsetValue));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unsupported metadata kind.");
        }
    }

    private static string KindCode(MetadataValueKind kind) => kind switch
    {
        MetadataValueKind.String => "s",
        MetadataValueKind.Number => "n",
        MetadataValueKind.Boolean => "b",
        MetadataValueKind.DateTimeOffset => "d",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported metadata kind."),
    };

    // Dates are stored as UTC with a fixed-width format, so text comparison matches time order.
    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        foreach (var ch in key)
        {
            if (ch is '"' or '\\' || char.IsControl(ch))
            {
                throw new ArgumentException($"Metadata key '{key}' contains a character that is not allowed.", nameof(key));
            }
        }
    }
}
