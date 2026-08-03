internal sealed record NotificationSettings(
    bool IssueCreated,
    bool Comment,
    bool Status,
    bool Assignee,
    bool Priority,
    bool Title,
    bool Date,
    bool Label,
    bool Points,
    bool Draft,
    bool Description,
    bool OtherUpdates,
    int DescriptionDebounceSeconds)
{
    public static NotificationSettings Load(ILogger logger) => new(
        IssueCreated: Read("PLANE_NOTIFY_ISSUE_CREATED", true, logger),
        Comment: Read("PLANE_NOTIFY_COMMENT", true, logger),
        Status: Read("PLANE_NOTIFY_STATUS", true, logger),
        Assignee: Read("PLANE_NOTIFY_ASSIGNEE", true, logger),
        Priority: Read("PLANE_NOTIFY_PRIORITY", true, logger),
        Title: Read("PLANE_NOTIFY_TITLE", true, logger),
        Date: Read("PLANE_NOTIFY_DATE", true, logger),
        Label: Read("PLANE_NOTIFY_LABEL", true, logger),
        Points: Read("PLANE_NOTIFY_POINTS", true, logger),
        Draft: Read("PLANE_NOTIFY_DRAFT", true, logger),
        Description: Read("PLANE_NOTIFY_DESCRIPTION", false, logger),
        OtherUpdates: Read("PLANE_NOTIFY_OTHER_UPDATES", true, logger),
        DescriptionDebounceSeconds: ReadPositiveInt(
            "PLANE_DESCRIPTION_DEBOUNCE_SECONDS",
            45,
            logger));

    public bool ShouldSendUpdate(string? field)
    {
        if (IsStatusField(field)) return Status;
        if (IsAssigneeField(field)) return Assignee;
        if (IsPriorityField(field)) return Priority;
        if (IsTitleField(field)) return Title;
        if (IsDateField(field)) return Date;
        if (IsLabelField(field)) return Label;
        if (IsPointsField(field)) return Points;
        if (IsDraftField(field)) return Draft;
        if (IsDescriptionField(field)) return Description;

        return OtherUpdates;
    }

    public static bool IsStatusField(string? field) =>
        Is(field, "state_id", "state", "status");

    public static bool IsAssigneeField(string? field) =>
        Is(field, "assignee_ids", "assignees", "assignee");

    public static bool IsPriorityField(string? field) =>
        Is(field, "priority");

    public static bool IsTitleField(string? field) =>
        Is(field, "name", "title");

    public static bool IsDateField(string? field) =>
        Is(field, "start_date", "target_date");

    public static bool IsLabelField(string? field) =>
        Is(field, "label_ids", "labels", "label");

    public static bool IsPointsField(string? field) =>
        Is(field, "point", "points", "estimate_point", "estimate_points");

    public static bool IsDraftField(string? field) =>
        Is(field, "is_draft", "draft");

    public static bool IsDescriptionField(string? field) =>
        Is(field, "description", "description_html", "description_stripped");

    private static bool Is(string? field, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(field))
            return false;

        var normalized = field.Trim();
        return names.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Read(
        string variable,
        bool defaultValue,
        ILogger logger)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return bool.TryParse(value.Trim(), out var parsedValue)
            ? parsedValue
            : Invalid(variable, value, defaultValue, logger);
    }

    private static bool Invalid(
        string variable,
        string value,
        bool defaultValue,
        ILogger logger)
    {
        logger.LogWarning(
            "Ignoring invalid {Variable} value {Value}; using {DefaultValue}",
            variable,
            value,
            defaultValue);

        return defaultValue;
    }

    private static int ReadPositiveInt(
        string variable,
        int defaultValue,
        ILogger logger)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (int.TryParse(value, out var seconds) && seconds > 0)
            return seconds;

        logger.LogWarning(
            "Ignoring invalid {Variable} value {Value}; using {DefaultValue}",
            variable,
            value,
            defaultValue);

        return defaultValue;
    }
}
