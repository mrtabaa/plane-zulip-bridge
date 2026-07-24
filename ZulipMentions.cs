using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal interface IZulipUserResolver
{
    Task RefreshAsync(CancellationToken cancellationToken);

    ValueTask<ZulipUser?> FindByEmailAsync(
        string? email,
        CancellationToken cancellationToken);
}

internal sealed class ZulipUserResolver : IZulipUserResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly string _zulipUrl;
    private readonly string _zulipEmail;
    private readonly string _zulipApiKey;
    private readonly ILogger<ZulipUserResolver> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, ZulipUser> _usersByEmail =
        new Dictionary<string, ZulipUser>(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public ZulipUserResolver(
        HttpClient http,
        string zulipUrl,
        string zulipEmail,
        string zulipApiKey,
        ILogger<ZulipUserResolver> logger)
    {
        _http = http;
        _zulipUrl = zulipUrl.TrimEnd('/');
        _zulipEmail = zulipEmail;
        _zulipApiKey = zulipApiKey;
        _logger = logger;
    }

    public async ValueTask<ZulipUser?> FindByEmailAsync(
        string? email,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);

        if (normalizedEmail is null)
            return null;

        if (IsCacheStale())
            await TryRefreshAsync(cancellationToken);

        if (_usersByEmail.TryGetValue(normalizedEmail, out var cachedUser))
            return cachedUser;

        await TryRefreshAsync(cancellationToken);

        return _usersByEmail.TryGetValue(normalizedEmail, out var refreshedUser)
            ? refreshedUser
            : null;
    }

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        RefreshCoreAsync(cancellationToken);

    private bool IsCacheStale() =>
        _usersByEmail.Count == 0 ||
        DateTimeOffset.UtcNow - _loadedAt > CacheLifetime;

    private async Task TryRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timed out while refreshing Zulip user directory");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not refresh Zulip user directory");
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);

        try
        {
            if (!IsCacheStale())
                return;

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_zulipUrl}/api/v1/users");

            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_zulipEmail}:{_zulipApiKey}"));

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);

            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Zulip user directory returned {Status}",
                    response.StatusCode);

                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);

            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

            var users = new Dictionary<string, ZulipUser>(
                StringComparer.OrdinalIgnoreCase);

            if (document.RootElement.TryGetProperty("members", out var members) &&
                members.ValueKind == JsonValueKind.Array)
            {
                foreach (var member in members.EnumerateArray())
                {
                    if (!IsUsableZulipUser(member))
                        continue;

                    var normalizedEmail =
                        NormalizeEmail(JsonString(member, "delivery_email")) ??
                        NormalizeEmail(JsonString(member, "email"));
                    var fullName = JsonString(member, "full_name");

                    if (normalizedEmail is null ||
                        string.IsNullOrWhiteSpace(fullName))
                    {
                        continue;
                    }

                    users[normalizedEmail] = new ZulipUser(
                        JsonLong(member, "user_id"),
                        normalizedEmail,
                        fullName.Trim());

                    var apiEmail = NormalizeEmail(JsonString(member, "email"));

                    if (apiEmail is not null)
                    {
                        users[apiEmail] = users[normalizedEmail];
                    }
                }
            }

            _usersByEmail = users;
            _loadedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Loaded {Count} Zulip users for mention resolution",
                users.Count);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static bool IsUsableZulipUser(JsonElement member)
    {
        if (member.TryGetProperty("is_active", out var isActive) &&
            isActive.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        var status = JsonString(member, "status");

        if (string.IsNullOrWhiteSpace(status))
            return true;

        return !status.Equals("inactive", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("deactivated", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string? JsonString(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static long? JsonLong(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number))
        {
            return number;
        }

        return null;
    }

    internal static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var trimmed = email.Trim();

        return trimmed.Contains('@', StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : null;
    }
}

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

internal static class Markdown
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }
}

internal sealed record PmsUserRef(
    string? Id,
    string? Email,
    string? DisplayName);

internal sealed record ZulipUser(
    long? UserId,
    string Email,
    string FullName);
