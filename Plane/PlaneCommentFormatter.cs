using System.Text;
using System.Text.RegularExpressions;

internal sealed class PlaneCommentFormatter
{
    private static readonly Regex MentionComponentRegex = new(
        @"<mention-component\b(?<attributes>[^>]*)>" +
        @"[\s\S]*?</mention-component\s*>",
        RegexOptions.IgnoreCase |
        RegexOptions.Compiled);

    private static readonly Regex EntityIdentifierRegex = new(
        @"\bentity_identifier\s*=\s*[""'](?<id>[^""']+)[""']",
        RegexOptions.IgnoreCase |
        RegexOptions.Compiled);

    private readonly ZulipMentionFormatter _zulipMentionFormatter;
    private readonly IPlaneUserDirectory _planeUsers;
    private readonly ILogger<PlaneCommentFormatter> _logger;

    public PlaneCommentFormatter(
        ZulipMentionFormatter zulipMentionFormatter,
        IPlaneUserDirectory planeUsers,
        ILogger<PlaneCommentFormatter> logger)
    {
        _zulipMentionFormatter = zulipMentionFormatter;
        _planeUsers = planeUsers;
        _logger = logger;
    }

    public async ValueTask<string> FormatAsync(
        string? commentHtml,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commentHtml))
            return "";

        var matches = MentionComponentRegex.Matches(commentHtml);

        if (matches.Count == 0)
            return PlaneHtmlText.ToPlainText(commentHtml);

        var replacements = new List<(Match Match, string Replacement)>();

        foreach (Match match in matches)
        {
            var identifier = GetEntityIdentifier(match);

            if (identifier is null)
            {
                _logger.LogWarning(
                    "Plane user mention did not contain an entity_identifier: {Mention}",
                    match.Value);

                replacements.Add((
                    match,
                    Markdown.Escape("@mentioned-user")));

                continue;
            }

            var user = await _planeUsers.FindUserAsync(
                identifier,
                cancellationToken);

            if (user is null)
            {
                _logger.LogWarning(
                    "Plane API returned no user for mention identifier {Identifier}",
                    identifier);

                // Do not allow an unresolved mention-only comment to become empty.
                replacements.Add((
                    match,
                    Markdown.Escape("@mentioned-user")));

                continue;
            }

            var mention = await _zulipMentionFormatter.FormatUserAsync(
                user,
                cancellationToken);

            replacements.Add((match, mention));
        }

        var result = new StringBuilder(commentHtml);

        // Replace from the end so earlier match offsets remain valid.
        foreach (var replacement in replacements
                     .OrderByDescending(item => item.Match.Index))
        {
            result.Remove(
                replacement.Match.Index,
                replacement.Match.Length);

            result.Insert(
                replacement.Match.Index,
                replacement.Replacement);
        }

        return PlaneHtmlText.ToPlainText(result.ToString());
    }

    public async ValueTask<IReadOnlyList<PmsUserRef>> MentionUsersAsync(
        string? commentHtml,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commentHtml))
            return Array.Empty<PmsUserRef>();

        var users = new List<PmsUserRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MentionComponentRegex.Matches(commentHtml))
        {
            var identifier = GetEntityIdentifier(match);

            if (identifier is null)
                continue;

            var user = await _planeUsers.FindUserAsync(
                identifier,
                cancellationToken);

            var key = user?.Email ?? user?.Id;

            if (user is null || string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                continue;
            }

            users.Add(user);
        }

        return users;
    }

    private static string? GetEntityIdentifier(Match mentionMatch)
    {
        var attributes = mentionMatch.Groups["attributes"].Value;
        var identifierMatch = EntityIdentifierRegex.Match(attributes);

        return identifierMatch.Success
            ? identifierMatch.Groups["id"].Value.Trim()
            : null;
    }

}
