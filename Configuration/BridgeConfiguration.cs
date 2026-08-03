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

}
