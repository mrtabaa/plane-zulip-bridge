using System.Collections.Concurrent;
using System.Text.Json;

internal sealed class PlaneWorkItemClient
{
    private static readonly TimeSpan MetadataLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _workspaceSlug;
    private readonly ConcurrentDictionary<string, MetadataEntry> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MetadataEntry> _labels =
        new(StringComparer.OrdinalIgnoreCase);

    public PlaneWorkItemClient(
        HttpClient http,
        string apiUrl,
        string apiKey,
        string workspaceSlug)
    {
        _http = http;
        _apiUrl = apiUrl.TrimEnd('/');
        _apiKey = apiKey;
        _workspaceSlug = workspaceSlug.Trim();
    }

    public async Task<PlaneWorkItem> GetAsync(
        string projectId,
        string workItemId,
        CancellationToken cancellationToken)
    {
        ValidateId(projectId, nameof(projectId));
        ValidateId(workItemId, nameof(workItemId));

        var path = ProjectPath(projectId) +
            $"/work-items/{Uri.EscapeDataString(workItemId)}/" +
            "?expand=assignees,state,labels";

        using var document = await GetJsonAsync(path, cancellationToken);
        var data = document.RootElement.Clone();
        var returnedId = JsonString(data, "id");
        var name = JsonString(data, "name");

        if (!string.Equals(returnedId, workItemId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                $"Plane returned invalid metadata for work item {workItemId}.");
        }

        return new PlaneWorkItem(
            returnedId!,
            name.Trim(),
            JsonLong(data, "sequence_id"),
            JsonString(data, "created_by"));
    }

    public async Task<IReadOnlyList<string>> GetAttachmentNamesAsync(
        string projectId,
        string workItemId,
        string? commentId,
        CancellationToken cancellationToken)
    {
        var path = ProjectPath(projectId) +
            $"/work-items/{Uri.EscapeDataString(workItemId)}/attachments/";
        var attachments = await GetAllAsync(path, cancellationToken);

        return attachments
            .Where(attachment =>
                string.IsNullOrWhiteSpace(commentId) ||
                string.Equals(
                    JsonString(attachment, "comment"),
                    commentId,
                    StringComparison.OrdinalIgnoreCase))
            .Select(AttachmentName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async ValueTask<string?> FindStateNameAsync(
        string projectId,
        string? stateId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stateId))
            return null;

        var states = await MetadataAsync(
            _states,
            projectId,
            "states",
            cancellationToken);

        return states.TryGetValue(stateId, out var name) ? name : null;
    }

    public async ValueTask<IReadOnlyList<string>> FindLabelNamesAsync(
        string projectId,
        IEnumerable<string> labelIds,
        CancellationToken cancellationToken)
    {
        var ids = labelIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();

        if (ids.Length == 0)
            return Array.Empty<string>();

        var labels = await MetadataAsync(
            _labels,
            projectId,
            "labels",
            cancellationToken);

        return ids
            .Where(labels.ContainsKey)
            .Select(id => labels[id])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<string, string>> MetadataAsync(
        ConcurrentDictionary<string, MetadataEntry> cache,
        string projectId,
        string resource,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(projectId, out var cached) &&
            DateTimeOffset.UtcNow - cached.LoadedAt < MetadataLifetime)
        {
            return cached.Names;
        }

        var path = ProjectPath(projectId) + $"/{resource}/";
        var elements = await GetAllAsync(path, cancellationToken);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements)
        {
            var id = JsonString(element, "id");
            var name = JsonString(element, "name");

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                names[id] = name.Trim();
        }

        cache[projectId] = new MetadataEntry(names, DateTimeOffset.UtcNow);
        return names;
    }

    private async Task<IReadOnlyList<JsonElement>> GetAllAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var elements = new List<JsonElement>();
        string? cursor = null;

        do
        {
            var separator = path.Contains('?') ? '&' : '?';
            var pagePath = $"{path}{separator}per_page=100";

            if (!string.IsNullOrWhiteSpace(cursor))
                pagePath += $"&cursor={Uri.EscapeDataString(cursor)}";

            using var document = await GetJsonAsync(pagePath, cancellationToken);
            var root = document.RootElement;
            elements.AddRange(Elements(root).Select(element => element.Clone()));

            cursor = root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("next_page_results", out var hasNext) &&
                     hasNext.ValueKind == JsonValueKind.True
                ? JsonString(root, "next_cursor")
                : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return elements;
    }

    private async Task<JsonDocument> GetJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _apiUrl + path);
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
            throw new InvalidOperationException("Plane API returned invalid JSON.", exception);
        }
    }

    private string ProjectPath(string projectId)
    {
        ValidateId(projectId, nameof(projectId));

        return $"/api/v1/workspaces/{Uri.EscapeDataString(_workspaceSlug)}" +
            $"/projects/{Uri.EscapeDataString(projectId)}";
    }

    private static IEnumerable<JsonElement> Elements(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToArray();

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            return results.EnumerateArray().ToArray();
        }

        throw new InvalidOperationException("Plane API response did not contain an array.");
    }

    private static string? AttachmentName(JsonElement attachment)
    {
        var direct =
            JsonString(attachment, "name") ??
            JsonString(attachment, "asset_name") ??
            JsonString(attachment, "file_name");

        if (!string.IsNullOrWhiteSpace(direct))
            return direct.Trim();

        if (attachment.ValueKind == JsonValueKind.Object &&
            attachment.TryGetProperty("attributes", out var attributes))
        {
            return JsonString(attributes, "name")?.Trim();
        }

        return null;
    }

    private static string? JsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? JsonLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result)
            ? result
            : null;

    private static void ValidateId(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A Plane identifier is required.", parameter);
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

    private sealed record MetadataEntry(
        IReadOnlyDictionary<string, string> Names,
        DateTimeOffset LoadedAt);
}

internal sealed record PlaneWorkItem(
    string Id,
    string Name,
    long? SequenceId,
    string? CreatorId);
