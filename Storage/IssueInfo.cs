internal sealed record IssueInfo(
    string IssueId,
    string Name,
    long? SequenceId,
    string ProjectId,
    string ProjectName,
    string ProjectIdentifier,
    DateTimeOffset CachedAt = default);

