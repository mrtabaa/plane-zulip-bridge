internal sealed class ZulipMentionFormatter
{
    private readonly IZulipUserResolver _resolver;
    private readonly ILogger<ZulipMentionFormatter> _logger;

    public ZulipMentionFormatter(
        IZulipUserResolver resolver,
        ILogger<ZulipMentionFormatter> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public async ValueTask<string> FormatUserAsync(
        PmsUserRef user,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = ZulipUserResolver.NormalizeEmail(user.Email);

        if (normalizedEmail is not null)
        {
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
