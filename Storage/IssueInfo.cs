internal sealed record IssueInfo(
    string IssueId,
    string Name,
    long? SequenceId,
    string ProjectId,
    string? CreatorId = null,
    string? CreatorEmail = null,
    string? CreatorDisplayName = null,
    DateTimeOffset CachedAt = default);
