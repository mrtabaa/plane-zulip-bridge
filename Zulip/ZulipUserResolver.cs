using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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

