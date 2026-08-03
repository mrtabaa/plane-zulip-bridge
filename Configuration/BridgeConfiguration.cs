using System.Text.Json;

internal static class BridgeConfiguration
{
    public static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required environment variable {name} is missing.");
        }

        return value;
    }

    public static string? LoadJsonConfiguration(
        string fileEnvironmentVariable,
        string inlineEnvironmentVariable,
        string defaultFile)
    {
        var configuredFile = Environment.GetEnvironmentVariable(
            fileEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(configuredFile))
        {
            return ReadConfigurationFile(
                configuredFile.Trim(),
                fileEnvironmentVariable);
        }

        var inlineJson = Environment.GetEnvironmentVariable(
            inlineEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(inlineJson))
            return inlineJson;

        var defaultPath = ResolveConfigurationPath(defaultFile);

        return File.Exists(defaultPath)
            ? File.ReadAllText(defaultPath)
            : null;
    }

    public static Dictionary<string, ProjectInfo> LoadProjects()
    {
        var json = LoadJsonConfiguration(
            "PMS_PROJECTS_FILE",
            "PMS_PROJECTS_JSON",
            "./config/pms-projects.json");
        var result = new Dictionary<string, ProjectInfo>(
            StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "PMS_PROJECTS_JSON must be a JSON object.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var projectId = property.Name;
                var value = property.Value;

                if (value.ValueKind == JsonValueKind.String)
                {
                    var projectName = value.GetString();

                    if (!string.IsNullOrWhiteSpace(projectName))
                    {
                        result[projectId] = new ProjectInfo(
                            projectName.Trim(),
                            "");
                    }

                    continue;
                }

                if (value.ValueKind != JsonValueKind.Object)
                    continue;

                var name = String(value, "name");
                var identifier = String(value, "identifier");

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = !string.IsNullOrWhiteSpace(identifier)
                        ? identifier
                        : $"Project {ShortId(projectId)}";
                }

                result[projectId] = new ProjectInfo(
                    name.Trim(),
                    identifier?.Trim() ?? "");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "PMS_PROJECTS_JSON contains invalid JSON.",
                exception);
        }
    }

    public static ProjectInfo ResolveProject(
        string? projectId,
        IReadOnlyDictionary<string, ProjectInfo> projects)
    {
        if (!string.IsNullOrWhiteSpace(projectId) &&
            projects.TryGetValue(projectId, out var project))
        {
            return project;
        }

        return new ProjectInfo(
            string.IsNullOrWhiteSpace(projectId)
                ? "Unknown project"
                : $"Project {ShortId(projectId)}",
            "");
    }

    public static void LoadDotEnv(string fileName = ".env")
    {
        var paths = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        };
        var path = paths.FirstOrDefault(File.Exists);

        if (path is null)
            return;

        foreach (var originalLine in File.ReadAllLines(path))
        {
            var line = originalLine.Trim();

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                line = line[7..].Trim();

            var separatorIndex = line.IndexOf('=');

            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(key) ||
                Environment.GetEnvironmentVariable(key) is not null)
            {
                continue;
            }

            if (value.Length >= 2)
            {
                if (value.StartsWith('\'') && value.EndsWith('\''))
                {
                    value = value[1..^1];
                }
                else if (value.StartsWith('"') && value.EndsWith('"'))
                {
                    value = value[1..^1]
                        .Replace("\\n", "\n")
                        .Replace("\\r", "\r")
                        .Replace("\\t", "\t")
                        .Replace("\\\"", "\"")
                        .Replace("\\\\", "\\");
                }
            }

            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string ReadConfigurationFile(
        string configuredPath,
        string environmentVariable)
    {
        var path = ResolveConfigurationPath(configuredPath);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Configuration file '{configuredPath}' from " +
                $"{environmentVariable} was not found at '{path}'.");
        }

        return File.ReadAllText(path);
    }

    private static string ResolveConfigurationPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
            return configuredPath;

        var currentDirectoryPath = Path.GetFullPath(
            configuredPath,
            Directory.GetCurrentDirectory());

        return File.Exists(currentDirectoryPath)
            ? currentDirectoryPath
            : Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
    }

    private static string? String(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string ShortId(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value[..Math.Min(8, value.Length)];
}

internal sealed record ProjectInfo(string Name, string Identifier);
