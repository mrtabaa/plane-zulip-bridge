using System.Text.Json;

internal static class PmsPayload
{
    public static JsonElement Object(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return default;
    }

    public static JsonElement Property(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value)
            ? value
            : default;
    }

    public static string? String(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static long? Number(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var number)
            ? number
            : null;
    }

    public static string PersonName(JsonElement person)
    {
        var displayName = String(person, "display_name");

        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName.Trim();

        var firstName = String(person, "first_name");
        var lastName = String(person, "last_name");
        var fullName = $"{firstName} {lastName}".Trim();

        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName;

        return String(person, "email") ?? "Someone";
    }
}
