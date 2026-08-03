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
        IssueCreated: Read("PMS_NOTIFY_ISSUE_CREATED", true, logger),
        Comment: Read("PMS_NOTIFY_COMMENT", true, logger),
        Status: Read("PMS_NOTIFY_STATUS", true, logger),
        Assignee: Read("PMS_NOTIFY_ASSIGNEE", true, logger),
        Priority: Read("PMS_NOTIFY_PRIORITY", true, logger),
        Title: Read("PMS_NOTIFY_TITLE", true, logger),
        Date: Read("PMS_NOTIFY_DATE", true, logger),
        Label: Read("PMS_NOTIFY_LABEL", true, logger),
        Points: Read("PMS_NOTIFY_POINTS", true, logger),
        Draft: Read("PMS_NOTIFY_DRAFT", true, logger),
        Description: Read("PMS_NOTIFY_DESCRIPTION", false, logger),
        OtherUpdates: Read("PMS_NOTIFY_OTHER_UPDATES", true, logger),
        DescriptionDebounceSeconds: ReadPositiveInt(
            "PMS_DESCRIPTION_DEBOUNCE_SECONDS",
            45,
            logger));

    public bool ShouldSendUpdate(string? field) =>
        field?.ToLowerInvariant() switch
        {
            "state_id" => Status,
            "assignee_ids" => Assignee,
            "priority" => Priority,
            "name" => Title,
            "start_date" or "target_date" => Date,
            "label_ids" => Label,
            "point" or "estimate_point" => Points,
            "is_draft" => Draft,
            "description" or
            "description_html" or
            "description_stripped" => Description,
            _ => OtherUpdates
        };

    public static bool IsDescriptionField(string? field) =>
        field?.ToLowerInvariant() is
            "description" or
            "description_html" or
            "description_stripped";

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
