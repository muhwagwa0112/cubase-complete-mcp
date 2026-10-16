using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

/// <summary>
/// Locale-independent canonical JSON used for approval and idempotency hashes. Object keys are sorted
/// recursively (ordinal), array order is preserved, only finite numbers are accepted, and control fields that
/// identify a request rather than describe it (confirmation_id, operation_id) are excluded at the top level.
/// No other argument is ever dropped from the hash.
/// </summary>
public static class CanonicalJson
{
    public static readonly string[] ControlFields = ["confirmation_id", "operation_id"];

    public static string Serialize(JsonNode? node, bool excludeControlFields = true)
    {
        var builder = new StringBuilder();
        Write(builder, node, excludeControlFields);
        return builder.ToString();
    }

    public static string Hash(string domain, JsonNode? node, bool excludeControlFields = true) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(domain + "\n" + Serialize(node, excludeControlFields))))
            .ToLowerInvariant();

    private static void Write(StringBuilder builder, JsonNode? node, bool excludeControlFields)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var pair in obj.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    if (excludeControlFields && ControlFields.Contains(pair.Key, StringComparer.Ordinal)) continue;
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(pair.Key));
                    builder.Append(':');
                    Write(builder, pair.Value, false);
                }
                builder.Append('}');
                return;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    Write(builder, array[i], false);
                }
                builder.Append(']');
                return;
            case JsonValue value:
                WriteValue(builder, value);
                return;
        }
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        if (value.TryGetValue<double>(out var raw) && !double.IsFinite(raw))
            throw new ArgumentException("Only finite numbers are allowed in canonical JSON.");
        var element = JsonSerializer.SerializeToElement(value);
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            case JsonValueKind.Number:
                var number = element.GetDouble();
                if (!double.IsFinite(number)) throw new ArgumentException("Only finite numbers are allowed in canonical JSON.");
                // Integers keep an exact decimal form; other values use round-trip formatting independent of culture.
                builder.Append(element.TryGetInt64(out var integer) ? integer.ToString(CultureInfo.InvariantCulture)
                    : number == Math.Floor(number) && Math.Abs(number) < 1e15
                        ? ((long)number).ToString(CultureInfo.InvariantCulture)
                        : number.ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                throw new ArgumentException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }
}

/// <summary>
/// Numeric reads that work for both wire-parsed values (JsonElement-backed) and values built in code, whose
/// JsonValue only converts to its own CLR type.
/// </summary>
public static class JsonNumbers
{
    public static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var direct)) return direct;
        var text = value.ToJsonString();
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;
    }

    public static long? ReadLong(JsonNode? node) =>
        ReadDouble(node) is { } number && double.IsFinite(number) && Math.Abs(number) < 9.0e15 ? (long)Math.Round(number) : null;
}
