using System.Text.Json;

internal static class PlaneMentionMapLoader
{
    public static IReadOnlyDictionary<string, string> Load(
        string? json,
        ILogger logger)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "The Plane mention map must be a JSON object.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var identifier = property.Name.Trim();

                var email = property.Value.ValueKind == JsonValueKind.String
                    ? ZulipUserResolver.NormalizeEmail(
                        property.Value.GetString())
                    : null;

                if (string.IsNullOrWhiteSpace(identifier) || email is null)
                {
                    logger.LogWarning(
                        "Ignoring invalid Plane mention mapping for {Identifier}",
                        property.Name);

                    continue;
                }

                result[identifier] = email;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not load the Plane mention map");
        }

        return result;
    }
}
