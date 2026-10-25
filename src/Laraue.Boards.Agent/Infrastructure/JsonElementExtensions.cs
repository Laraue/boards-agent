using System.Text.Json;

namespace Laraue.Boards.Agent.Infrastructure;

/// <summary>Reading optional values from JSON the runner doesn't own (agents' output, gh): missing or mistyped is null.</summary>
static class JsonElementExtensions
{
    public static JsonElement? Property(this JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
    }

    public static string? String(this JsonElement element, string name)
    {
        return element.Property(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    public static long? Long(this JsonElement element, string name)
    {
        return element.Property(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;
    }

    public static decimal? Decimal(this JsonElement element, string name)
    {
        return element.Property(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var number) ? number : null;
    }

    public static bool Bool(this JsonElement element, string name)
    {
        return element.Property(name) is { ValueKind: JsonValueKind.True };
    }

    /// <summary>The items of an array property; none when it's missing or not an array.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element, string name)
    {
        return element.Property(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    }
}
