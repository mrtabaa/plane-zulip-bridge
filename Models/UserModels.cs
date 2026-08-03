internal sealed record PmsUserRef(
    string? Id,
    string? Email,
    string? DisplayName);

internal sealed record ZulipUser(
    long? UserId,
    string Email,
    string FullName);

