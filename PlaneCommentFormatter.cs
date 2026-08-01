using System.Text;
using System.Text.Json;
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
    private readonly IReadOnlyDictionary<string, string> _emailByPlaneIdentifier;
    private readonly ILogger<PlaneCommentFormatter> _logger;

    public PlaneCommentFormatter(
        ZulipMentionFormatter zulipMentionFormatter,
        IReadOnlyDictionary<string, string> emailByPlaneIdentifier,
        ILogger<PlaneCommentFormatter> logger)
    {
        _zulipMentionFormatter = zulipMentionFormatter;
        _emailByPlaneIdentifier = emailByPlaneIdentifier;
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
            return NormalizeText(StripHtml(commentHtml));

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

            if (!_emailByPlaneIdentifier.TryGetValue(
                    identifier,
                    out var email))
            {
                _logger.LogWarning(
                    "No email mapping exists for Plane mention identifier {Identifier}",
                    identifier);

                // Do not allow an unresolved mention-only comment to become empty.
                replacements.Add((
                    match,
                    Markdown.Escape("@mentioned-user")));

                continue;
            }

            var mention = await _zulipMentionFormatter.FormatUserAsync(
                new PmsUserRef(
                    Id: identifier,
                    Email: email,
                    DisplayName: email),
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

        return NormalizeText(StripHtml(result.ToString()));
    }

    private static string? GetEntityIdentifier(Match mentionMatch)
    {
        var attributes = mentionMatch.Groups["attributes"].Value;
        var identifierMatch = EntityIdentifierRegex.Match(attributes);

        return identifierMatch.Success
            ? identifierMatch.Groups["id"].Value.Trim()
            : null;
    }

    private static string NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim();

    private static string StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        // Preserve the line structure represented by common HTML elements
        // before removing the remaining tags.
        value = Regex.Replace(
            value,
            @"<br\s*/?>|</(?:p|div|li|blockquote|h[1-6])\s*>",
            "\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var withoutTags = Regex.Replace(
            value,
            "<[^>]+>",
            "",
            RegexOptions.Singleline);

        return System.Net.WebUtility.HtmlDecode(withoutTags);
    }
}
