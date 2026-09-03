using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

internal enum KimiWireLineKind
{
    // Not an object, no string type, or an unrecognized record type.
    Ignored,
    // metadata with protocol_version "1.x".
    MetadataRecognized,
    // metadata with any other/absent protocol version.
    MetadataUnrecognized,
    // A known activity record type (llm.request, turn.prompt, ...).
    Activity,
    // A valid usage.record; Record is set.
    UsageRecord,
    // Unparseable JSON or a usage.record with an invalid shape.
    Invalid
}

internal readonly record struct KimiWireLine(KimiWireLineKind Kind, AgentUsageRecord? Record);

/// <summary>
/// Shared parser for Kimi Code session wire (wire.jsonl) lines. Recognition is
/// selected by record shape so historical logs survive runtime upgrades;
/// package versions are diagnostic hints, never routing keys.
/// </summary>
internal static class KimiWireUsageParser
{
    private static readonly HashSet<string> ActivityRecordTypes = new(StringComparer.Ordinal)
    {
        "llm.request",
        "turn.prompt",
        "context.append_loop_event"
    };

    public static KimiWireLine ParseLine(ReadOnlyMemory<byte> line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new KimiWireLine(KimiWireLineKind.Invalid, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProperty)
                || typeProperty.ValueKind != JsonValueKind.String)
            {
                return new KimiWireLine(KimiWireLineKind.Ignored, null);
            }

            var type = typeProperty.GetString();
            switch (type)
            {
                case "metadata":
                    return new KimiWireLine(
                        IsRecognizedProtocol(root)
                            ? KimiWireLineKind.MetadataRecognized
                            : KimiWireLineKind.MetadataUnrecognized,
                        null);
                case "usage.record":
                    return TryParseUsageRecord(root, out var record)
                        ? new KimiWireLine(KimiWireLineKind.UsageRecord, record)
                        : new KimiWireLine(KimiWireLineKind.Invalid, null);
                default:
                    return new KimiWireLine(
                        type != null && ActivityRecordTypes.Contains(type)
                            ? KimiWireLineKind.Activity
                            : KimiWireLineKind.Ignored,
                        null);
            }
        }
    }

    private static bool IsRecognizedProtocol(JsonElement root) =>
        root.TryGetProperty("protocol_version", out var protocol)
        && protocol.ValueKind == JsonValueKind.String
        && protocol.GetString() is { } value
        && value.StartsWith("1.", StringComparison.Ordinal);

    private static bool TryParseUsageRecord(
        JsonElement root,
        out AgentUsageRecord? record)
    {
        record = null;
        if (!root.TryGetProperty("time", out var time)
            || time.ValueKind != JsonValueKind.Number
            || !time.TryGetInt64(out var unixMilliseconds)
            || !root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !TryGetToken(usage, "inputOther", out var input)
            || !TryGetToken(usage, "output", out var output)
            || !TryGetToken(usage, "inputCacheRead", out var cacheRead)
            || !TryGetToken(usage, "inputCacheCreation", out var cacheCreation))
        {
            return false;
        }

        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var model = root.TryGetProperty("model", out var modelProperty)
            && modelProperty.ValueKind == JsonValueKind.String
            ? modelProperty.GetString() ?? "unknown"
            : "unknown";
        record = new AgentUsageRecord(
            timestamp, model, input, output, cacheRead, cacheCreation);
        return true;
    }

    private static bool TryGetToken(
        JsonElement usage,
        string propertyName,
        out long value)
    {
        value = 0;
        return usage.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value >= 0;
    }
}
