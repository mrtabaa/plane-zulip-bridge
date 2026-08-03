using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class PmsMentionExtractor
{
    private static readonly Regex EmailRegex = new(
        @"(?<![\w.+-])@?(?<email>[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,})(?![\w.+-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<PmsUserRef> IssueCreatedUsers(
        JsonElement data,
        PmsUserRef actor)
    {
        return Distinct(
            new[] { actor }
                .Concat(UserObjects(data, "created_by"))
                .Concat(UserObjects(data, "creator"))
                .Concat(UserObjects(data, "created_by_detail"))
                .Concat(Assignees(data)));
    }

    public static PmsUserRef? IssueCreator(JsonElement data)
    {
        // Plane commonly exposes the UUID in created_by and the usable user
        // object in created_by_detail. Prefer the detailed representations.
        var detailedCreator = UserObjects(data, "created_by_detail")
            .Concat(UserObjects(data, "creator"))
            .Concat(UserObjects(data, "created_by"))
            .FirstOrDefault(user =>
                !string.IsNullOrWhiteSpace(user.Email) ||
                !string.IsNullOrWhiteSpace(user.DisplayName));

        if (detailedCreator is not null)
            return detailedCreator;

        var creatorId = String(data, "created_by");

        if (string.IsNullOrWhiteSpace(creatorId))
            return null;

        // Some update payloads expose only the creator UUID. When the same
        // user is included as an assignee, recover their detailed user data.
        return Assignees(data).FirstOrDefault(user =>
            string.Equals(
                user.Id,
                creatorId,
                StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<PmsUserRef> IssueUpdatedUsers(
        JsonElement data,
        JsonElement activity,
        PmsUserRef actor)
    {
        var users = new List<PmsUserRef> { actor };
        users.AddRange(Assignees(data));

        if (FieldEquals(activity, "assignee_ids"))
        {
            users.AddRange(UserObjects(activity, "new_assignees"));
            users.AddRange(UserObjects(activity, "added_assignees"));
            users.AddRange(UserObjects(activity, "assignees"));
        }

        return Distinct(users);
    }

    public IReadOnlyList<PmsUserRef> CommentUsers(
        JsonElement data,
        JsonElement issueData,
        PmsUserRef actor,
        string commentText)
    {
        var users = new List<PmsUserRef> { actor };
        users.AddRange(Assignees(issueData));

        var structuredMentions = StructuredCommentMentions(data);

        users.AddRange(
            structuredMentions.Count > 0
                ? structuredMentions
                : MentionEmailsFromText(commentText));

        return Distinct(users);
    }

    public IReadOnlyList<PmsUserRef> StructuredCommentMentions(JsonElement data)
    {
        var users = new List<PmsUserRef>();

        foreach (var property in new[]
                 {
                     "mentions",
                     "mentioned_users",
                     "mention_users",
                     "user_mentions"
                 })
        {
            users.AddRange(UserObjects(data, property));
        }

        return Distinct(users);
    }

    public IReadOnlyList<PmsUserRef> MentionEmailsFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<PmsUserRef>();

        return Distinct(
            EmailRegex
                .Matches(text)
                .Select(match =>
                    new PmsUserRef(
                        Id: null,
                        Email: match.Groups["email"].Value,
                        DisplayName: match.Groups["email"].Value)));
    }

    public string ReplaceReliableTextMentions(
        string text,
        IReadOnlyDictionary<string, string> mentionsByEmail)
    {
        if (string.IsNullOrWhiteSpace(text) || mentionsByEmail.Count == 0)
            return text;

        return EmailRegex.Replace(
            text,
            match =>
            {
                var email = ZulipUserResolver.NormalizeEmail(
                    match.Groups["email"].Value);

                return email is not null &&
                       mentionsByEmail.TryGetValue(email, out var mention)
                    ? mention
                    : match.Value;
            });
    }

    public static string ReplaceTeamMention(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        // The brackets are required in the PMS comment:
        // [team]
        //
        // Zulip receives:
        // @*team*
        return Regex.Replace(
            value,
            @"\[team\]",
            "@*team*",
            RegexOptions.IgnoreCase);
    }

    public static string NeutralizeBroadcastMentions(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return Regex.Replace(
            value,
            @"@\*\*(all|everyone)\*\*",
            match => $"@\\*\\*{match.Groups[1].Value}\\*\\*",
            RegexOptions.IgnoreCase);
    }

    private static IReadOnlyList<PmsUserRef> Assignees(JsonElement data) =>
        UserObjects(data, "assignees");

    private static bool FieldEquals(JsonElement activity, string expected)
    {
        return activity.ValueKind == JsonValueKind.Object &&
               activity.TryGetProperty("field", out var field) &&
               field.ValueKind == JsonValueKind.String &&
               string.Equals(
                   field.GetString(),
                   expected,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<PmsUserRef> UserObjects(
        JsonElement element,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
        {
            return Array.Empty<PmsUserRef>();
        }

        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().SelectMany(UserObject).ToArray();

        return UserObject(value).ToArray();
    }

    private static IEnumerable<PmsUserRef> UserObject(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var nestedUser =
                Object(value, "user") ??
                Object(value, "actor") ??
                Object(value, "member");

            if (nestedUser is not null)
            {
                foreach (var user in UserObject(nestedUser.Value))
                    yield return user;
            }

            yield return new PmsUserRef(
                Id: String(value, "id") ?? String(value, "user_id"),
                Email: String(value, "email"),
                DisplayName:
                    String(value, "display_name") ??
                    String(value, "full_name") ??
                    String(value, "name") ??
                    String(value, "username"));

            yield break;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();

            if (!string.IsNullOrWhiteSpace(text) &&
                ZulipUserResolver.NormalizeEmail(text) is not null)
            {
                yield return new PmsUserRef(
                    Id: null,
                    Email: text,
                    DisplayName: text);
            }
        }
    }

    private static JsonElement? Object(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    private static string? String(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static IReadOnlyList<PmsUserRef> Distinct(
        IEnumerable<PmsUserRef> users)
    {
        var result = new List<PmsUserRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            var key =
                ZulipUserResolver.NormalizeEmail(user.Email) ??
                (!string.IsNullOrWhiteSpace(user.Id) ? $"id:{user.Id.Trim()}" : null);

            if (key is null || !seen.Add(key))
                continue;

            result.Add(user);
        }

        return result;
    }
}
