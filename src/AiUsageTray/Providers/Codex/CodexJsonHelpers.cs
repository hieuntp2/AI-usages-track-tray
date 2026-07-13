using System.Globalization;
using System.Text.Json;

namespace AiUsageTray.Providers.Codex;

/// <summary>Defensive JsonElement readers: absent/wrong-typed fields become null, never an exception or a zero.</summary>
internal static class CodexJsonHelpers
{
    /// <summary>Chains onto a possibly-absent parent (e.g. <c>result.TryGetProperty("a").TryGetProperty("b")</c>).</summary>
    public static JsonElement? TryGetProperty(this JsonElement? element, string name) =>
        element is { } e ? e.TryGetProperty(name) : null;

    public static JsonElement? TryGetProperty(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            return value;
        }

        return null;
    }

    public static string? GetStringOrNull(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public static decimal? GetDecimalOrNull(this JsonElement? element)
    {
        if (element is not { } e)
        {
            return null;
        }

        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public static int? GetIntOrNull(this JsonElement? element)
    {
        if (element is not { } e)
        {
            return null;
        }

        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(e.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    public static long? GetLongOrNull(this JsonElement? element)
    {
        if (element is not { } e)
        {
            return null;
        }

        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt64(out var i) => i,
            JsonValueKind.String when long.TryParse(e.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Safely converts a timestamp field that may arrive as Unix seconds, Unix milliseconds, or
    /// an ISO-8601 string. Returns null (never "now" or epoch) when the value can't be interpreted.
    /// </summary>
    public static DateTimeOffset? GetTimestampOrNull(this JsonElement? element)
    {
        if (element is not { } e)
        {
            return null;
        }

        try
        {
            if (e.ValueKind == JsonValueKind.String)
            {
                var s = e.GetString();
                if (!string.IsNullOrWhiteSpace(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
                {
                    return parsedDate;
                }

                return null;
            }

            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var numeric))
            {
                // Heuristic: values above ~10^12 are milliseconds, otherwise seconds.
                return numeric > 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                    : DateTimeOffset.FromUnixTimeSeconds(numeric);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return null;
    }
}
