using System.Collections.Concurrent;
using System.Text.Json;

internal interface IPlaneUserDirectory
{
    ValueTask<string?> FindEmailAsync(
        string? userId,
        CancellationToken cancellationToken);
}

internal sealed class PlaneUserDirectory : IPlaneUserDirectory
{
    private const int PageSize = 100;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _workspaceSlug;
    private readonly ILogger<PlaneUserDirectory> _logger;
    private readonly ConcurrentDictionary<string, string> _emailByUserId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    private PlaneUserDirectory(
        HttpClient http,
        string apiUrl,
        string apiKey,
        string workspaceSlug,
        ILogger<PlaneUserDirectory> logger)
    {
        _http = http;
        _apiUrl = apiUrl.TrimEnd('/');
        _apiKey = apiKey;
        _workspaceSlug = workspaceSlug.Trim();
        _logger = logger;
    }

    public int Count => _emailByUserId.Count;

    public static async Task<PlaneUserDirectory> LoadAsync(
        HttpClient http,
        string apiUrl,
        string apiKey,
        string workspaceSlug,
        ILogger<PlaneUserDirectory> logger,
        CancellationToken cancellationToken)
    {
        var directory = new PlaneUserDirectory(
            http,
            apiUrl,
            apiKey,
            workspaceSlug,
            logger);

        await directory.RefreshAsync(cancellationToken, force: true);
        return directory;
    }

    public async ValueTask<string?> FindEmailAsync(
        string? userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        await RefreshAsync(cancellationToken, force: false);

        if (_emailByUserId.TryGetValue(userId, out var email))
            return email;

        // Refresh immediately for users added since the last directory load.
        await RefreshAsync(cancellationToken, force: true);

        return _emailByUserId.TryGetValue(userId, out email)
            ? email
            : null;
    }

    private async Task RefreshAsync(
        CancellationToken cancellationToken,
        bool force)
    {
        if (!force && DateTimeOffset.UtcNow - _refreshedAt < RefreshInterval)
            return;

        await _refreshLock.WaitAsync(cancellationToken);

        try
        {
            if (!force && DateTimeOffset.UtcNow - _refreshedAt < RefreshInterval)
                return;

            var loaded = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            string? cursor = null;

            do
            {
                var path =
                    $"/api/v1/workspaces/{Uri.EscapeDataString(_workspaceSlug)}" +
                    $"/members/?per_page={PageSize}";

                if (!string.IsNullOrWhiteSpace(cursor))
                    path += $"&cursor={Uri.EscapeDataString(cursor)}";

                using var document = await GetJsonAsync(path, cancellationToken);
                var root = document.RootElement;
                var members = MemberArray(root);

                foreach (var member in members.EnumerateArray())
                {
                    var id = JsonString(member, "id")?.Trim();
                    var email = ZulipUserResolver.NormalizeEmail(
                        JsonString(member, "email"));

                    if (!string.IsNullOrWhiteSpace(id) && email is not null)
                        loaded[id] = email;
                }

                cursor = root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("next_page_results", out var hasNext) &&
                         hasNext.ValueKind == JsonValueKind.True
                    ? JsonString(root, "next_cursor")
                    : null;
            }
            while (!string.IsNullOrWhiteSpace(cursor));

            if (loaded.Count == 0)
            {
                throw new InvalidOperationException(
                    "Plane returned no workspace members with email addresses.");
            }

            _emailByUserId.Clear();

            foreach (var item in loaded)
                _emailByUserId[item.Key] = item.Value;

            _refreshedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Loaded {Count} users from Plane API for mention resolution",
                _emailByUserId.Count);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<JsonDocument> GetJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            _apiUrl + path);
        request.Headers.Add("X-API-Key", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Plane member API request failed with status " +
                $"{(int)response.StatusCode}: {Limit(body, 1000)}");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Plane member API returned invalid JSON.",
                exception);
        }
    }

    private static JsonElement MemberArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            return results;
        }

        throw new InvalidOperationException(
            "Plane workspace-members response did not contain a member array.");
    }

    private static string? JsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength
            ? value
            : value[..maximumLength] + "…";
}
