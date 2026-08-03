using System.Collections.Concurrent;
using System.Text.Json;

internal sealed class PlaneProjectCatalog
{
    private const int PageSize = 100;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _workspaceSlug;
    private readonly ILogger<PlaneProjectCatalog> _logger;
    private readonly ConcurrentDictionary<string, ProjectInfo> _projects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    private PlaneProjectCatalog(
        HttpClient http,
        string apiUrl,
        string apiKey,
        string workspaceSlug,
        ILogger<PlaneProjectCatalog> logger)
    {
        _http = http;
        _apiUrl = apiUrl.TrimEnd('/');
        _apiKey = apiKey;
        _workspaceSlug = workspaceSlug.Trim();
        _logger = logger;
    }

    public int Count => _projects.Count;

    public static async Task<PlaneProjectCatalog> LoadAsync(
        HttpClient http,
        string apiUrl,
        string apiKey,
        string workspaceSlug,
        ILogger<PlaneProjectCatalog> logger,
        CancellationToken cancellationToken)
    {
        var catalog = new PlaneProjectCatalog(
            http,
            apiUrl,
            apiKey,
            workspaceSlug,
            logger);

        await catalog.RefreshAsync(cancellationToken, force: true);
        return catalog;
    }

    public async ValueTask<ProjectInfo> ResolveAsync(
        string? projectId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return new ProjectInfo("Unknown project", "");

        await RefreshAsync(cancellationToken, force: false);

        if (_projects.TryGetValue(projectId, out var project))
            return project;

        // A project may have been created since the most recent full refresh.
        // Fetch it directly instead of waiting for the refresh interval.
        project = await FetchProjectAsync(projectId, cancellationToken);
        _projects[projectId] = project;

        return project;
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

            var loaded = new Dictionary<string, ProjectInfo>(
                StringComparer.OrdinalIgnoreCase);
            string? cursor = null;

            do
            {
                var path =
                    $"/api/v1/workspaces/{Uri.EscapeDataString(_workspaceSlug)}" +
                    $"/projects/?per_page={PageSize}";

                if (!string.IsNullOrWhiteSpace(cursor))
                    path += $"&cursor={Uri.EscapeDataString(cursor)}";

                using var document = await GetJsonAsync(path, cancellationToken);
                var root = document.RootElement;
                var projects = ProjectArray(root);

                foreach (var element in projects.EnumerateArray())
                {
                    if (TryReadProject(element, out var id, out var project))
                        loaded[id] = project;
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
                    "Plane returned no projects for the configured workspace.");
            }

            _projects.Clear();

            foreach (var item in loaded)
                _projects[item.Key] = item.Value;

            _refreshedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Loaded {Count} projects from Plane API",
                _projects.Count);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<ProjectInfo> FetchProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var path =
            $"/api/v1/workspaces/{Uri.EscapeDataString(_workspaceSlug)}" +
            $"/projects/{Uri.EscapeDataString(projectId)}/";

        using var document = await GetJsonAsync(path, cancellationToken);

        if (!TryReadProject(
                document.RootElement,
                out var returnedId,
                out var project))
        {
            throw new InvalidOperationException(
                $"Plane returned invalid metadata for project {projectId}.");
        }

        if (!string.Equals(
                returnedId,
                projectId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Plane returned project {returnedId} when {projectId} was requested.");
        }

        _logger.LogInformation(
            "Loaded previously unknown project {ProjectId} ({ProjectName}) from Plane API",
            projectId,
            project.Name);

        return project;
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
                $"Plane API request failed with status {(int)response.StatusCode}: " +
                Limit(body, 1000));
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Plane API returned invalid JSON.",
                exception);
        }
    }

    private static JsonElement ProjectArray(JsonElement root)
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
            "Plane project-list response did not contain a project array.");
    }

    private static bool TryReadProject(
        JsonElement element,
        out string id,
        out ProjectInfo project)
    {
        id = JsonString(element, "id")?.Trim() ?? "";
        var name = JsonString(element, "name")?.Trim();
        var identifier = JsonString(element, "identifier")?.Trim();

        if (string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(identifier))
        {
            project = default!;
            return false;
        }

        project = new ProjectInfo(name, identifier);
        return true;
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

internal sealed record ProjectInfo(string Name, string Identifier);
