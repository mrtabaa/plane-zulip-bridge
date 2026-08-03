using System.Collections.Concurrent;
using System.Text.Json;

internal sealed class IssueCacheStore
{
    private const int DefaultRetentionDays = 90;
    private const int DefaultMaximumEntries = 10_000;

    private readonly ConcurrentDictionary<string, IssueInfo> _issues;
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly TimeSpan _retention;
    private readonly int _maximumEntries;
    private readonly object _writeLock = new();

    private IssueCacheStore(
        ConcurrentDictionary<string, IssueInfo> issues,
        string filePath,
        ILogger logger,
        TimeSpan retention,
        int maximumEntries)
    {
        _issues = issues;
        _filePath = filePath;
        _logger = logger;
        _retention = retention;
        _maximumEntries = maximumEntries;
    }

    public int Count => _issues.Count;

    public static IssueCacheStore Load(string configuredPath, ILogger logger)
    {
        var filePath = Path.GetFullPath(configuredPath);
        var retention = TimeSpan.FromDays(ReadPositiveInt(
            "PMS_ISSUE_CACHE_RETENTION_DAYS",
            DefaultRetentionDays,
            logger));
        var maximumEntries = ReadPositiveInt(
            "PMS_ISSUE_CACHE_MAX_ENTRIES",
            DefaultMaximumEntries,
            logger);
        var issues = new ConcurrentDictionary<string, IssueInfo>(
            StringComparer.OrdinalIgnoreCase);

        if (File.Exists(filePath))
        {
            try
            {
                var serialized = File.ReadAllText(filePath);
                var savedIssues = JsonSerializer.Deserialize<List<IssueInfo>>(
                    serialized) ?? [];
                var legacyCachedAt = File.GetLastWriteTimeUtc(filePath);

                foreach (var issue in savedIssues)
                {
                    if (string.IsNullOrWhiteSpace(issue.IssueId))
                        continue;

                    issues[issue.IssueId] = issue.CachedAt == default
                        ? issue with { CachedAt = legacyCachedAt }
                        : issue;
                }

                logger.LogInformation(
                    "Loaded {Count} cached PMS issues from {Path}",
                    issues.Count,
                    filePath);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not load cached PMS issues from {Path}",
                    filePath);
            }
        }

        var store = new IssueCacheStore(
            issues,
            filePath,
            logger,
            retention,
            maximumEntries);
        if (store.Prune())
            store.Persist();

        return store;
    }

    public bool TryGet(string issueId, out IssueInfo? issue) =>
        _issues.TryGetValue(issueId, out issue);

    public void Upsert(IssueInfo issue)
    {
        _issues[issue.IssueId] = issue with { CachedAt = DateTimeOffset.UtcNow };
        Persist();
    }

    private void Persist()
    {
        lock (_writeLock)
        {
            try
            {
                Prune();

                var directory = Path.GetDirectoryName(_filePath);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var temporaryPath = _filePath + ".tmp";
                var serialized = JsonSerializer.Serialize(
                    _issues.Values.OrderBy(issue => issue.IssueId));

                File.WriteAllText(temporaryPath, serialized);
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not persist cached PMS issues to {Path}",
                    _filePath);
            }
        }
    }

    private bool Prune()
    {
        var countBefore = _issues.Count;
        var oldestAllowed = DateTimeOffset.UtcNow - _retention;

        foreach (var issue in _issues.Values)
        {
            if (issue.CachedAt < oldestAllowed)
                _issues.TryRemove(issue.IssueId, out _);
        }

        var excess = _issues.Count - _maximumEntries;

        if (excess <= 0)
            return _issues.Count != countBefore;

        foreach (var issue in _issues.Values
                     .OrderBy(issue => issue.CachedAt)
                     .Take(excess))
        {
            _issues.TryRemove(issue.IssueId, out _);
        }

        return _issues.Count != countBefore;
    }

    private static int ReadPositiveInt(
        string environmentVariable,
        int defaultValue,
        ILogger logger)
    {
        var configuredValue = Environment.GetEnvironmentVariable(
            environmentVariable);

        if (string.IsNullOrWhiteSpace(configuredValue))
            return defaultValue;

        if (int.TryParse(configuredValue, out var value) && value > 0)
            return value;

        logger.LogWarning(
            "Ignoring invalid {Variable}; using {DefaultValue}",
            environmentVariable,
            defaultValue);

        return defaultValue;
    }
}
