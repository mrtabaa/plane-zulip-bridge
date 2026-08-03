using System.Text.Json;

internal sealed class ZulipMentionFormatter
{
    private readonly IZulipUserResolver _resolver;
    private readonly ILogger<ZulipMentionFormatter> _logger;
    private readonly IReadOnlyDictionary<string, string> _userMap;

    public ZulipMentionFormatter(
        IZulipUserResolver resolver,
        ILogger<ZulipMentionFormatter> logger,
        IReadOnlyDictionary<string, string>? userMap = null)
    {
        _resolver = resolver;
        _logger = logger;
        _userMap = userMap ??
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask<string> FormatUserAsync(
        PmsUserRef user,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = ZulipUserResolver.NormalizeEmail(user.Email);

        if (normalizedEmail is not null)
        {
            if (_userMap.TryGetValue(normalizedEmail, out var mappedFullName) &&
                !IsBroadcastName(mappedFullName))
            {
                return Mention(new ZulipUser(
                    null,
                    normalizedEmail,
                    mappedFullName));
            }

            ZulipUser? zulipUser = null;

            try
            {
                zulipUser = await _resolver.FindByEmailAsync(
                    normalizedEmail,
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Could not resolve PMS user email {Email} to a Zulip user",
                    normalizedEmail);
            }

            if (zulipUser is not null &&
                !IsBroadcastName(zulipUser.FullName))
            {
                return Mention(zulipUser);
            }

            _logger.LogWarning(
                "Could not resolve PMS user email {Email} to a Zulip user",
                normalizedEmail);
        }

        return PlainUser(user);
    }

    internal static Dictionary<string, string> LoadUserMap(
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
                    "ZULIP_USER_MAP_JSON must be a JSON object.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var email = ZulipUserResolver.NormalizeEmail(property.Name);
                var fullName = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()?.Trim()
                    : null;

                if (email is null || string.IsNullOrWhiteSpace(fullName))
                {
                    logger.LogWarning(
                        "Ignoring invalid Zulip user map entry for {Email}",
                        property.Name);
                    continue;
                }

                result[email] = fullName;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not parse ZULIP_USER_MAP_JSON; explicit user mappings are disabled");
        }

        logger.LogInformation(
            "Loaded {Count} explicit Zulip user mappings",
            result.Count);

        return result;
    }

    public async ValueTask<IReadOnlyList<string>> FormatDistinctUsersAsync(
        IEnumerable<PmsUserRef> users,
        CancellationToken cancellationToken)
    {
        var formatted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            var key = DistinctKey(user);

            if (key is null || !seen.Add(key))
                continue;

            formatted.Add(await FormatUserAsync(user, cancellationToken));
        }

        return formatted;
    }

    public static string Mention(ZulipUser user) =>
        $"@**{EscapeMentionName(user.FullName)}**";

    public static string EscapeMentionName(string name) =>
        name
            .Trim()
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);

    public static bool IsBroadcastName(string? name)
    {
        var normalized = name?.Trim().TrimStart('@');

        return normalized is not null &&
               (normalized.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("everyone", StringComparison.OrdinalIgnoreCase));
    }

    public static string PlainUser(PmsUserRef user)
    {
        var name = string.IsNullOrWhiteSpace(user.DisplayName)
            ? null
            : user.DisplayName!.Trim();

        var email = ZulipUserResolver.NormalizeEmail(user.Email);

        if (!string.IsNullOrWhiteSpace(name) &&
            !string.Equals(name, email, StringComparison.OrdinalIgnoreCase))
        {
            return email is null
                ? Markdown.Escape(name)
                : $"{Markdown.Escape(name)} ({Markdown.Escape(email)})";
        }

        if (email is not null)
            return Markdown.Escape(email);

        return !string.IsNullOrWhiteSpace(user.Id)
            ? Markdown.Escape(user.Id)
            : "Someone";
    }

    private static string? DistinctKey(PmsUserRef user)
    {
        var email = ZulipUserResolver.NormalizeEmail(user.Email);

        if (email is not null)
            return $"email:{email}";

        if (!string.IsNullOrWhiteSpace(user.Id))
            return $"id:{user.Id.Trim()}";

        return string.IsNullOrWhiteSpace(user.DisplayName)
            ? null
            : $"name:{user.DisplayName.Trim()}";
    }
}

