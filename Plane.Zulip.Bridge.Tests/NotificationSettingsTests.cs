using Xunit;

public sealed class NotificationSettingsTests
{
    public static TheoryData<string, string> UpdateFields => new()
    {
        { "state_id", "status" },
        { "state", "status" },
        { "status", "status" },
        { "assignee_ids", "assignee" },
        { "assignees", "assignee" },
        { "assignee", "assignee" },
        { "priority", "priority" },
        { "name", "title" },
        { "title", "title" },
        { "start_date", "date" },
        { "target_date", "date" },
        { "label_ids", "label" },
        { "labels", "label" },
        { "label", "label" },
        { "point", "points" },
        { "points", "points" },
        { "estimate_point", "points" },
        { "estimate_points", "points" },
        { "is_draft", "draft" },
        { "draft", "draft" },
        { "description", "description" },
        { "description_html", "description" },
        { "description_stripped", "description" }
    };

    [Theory]
    [MemberData(nameof(UpdateFields))]
    public void KnownUpdateField_UsesItsDedicatedFlag(
        string field,
        string enabledCategory)
    {
        Assert.True(SettingsWithOnly(enabledCategory).ShouldSendUpdate(field));
        Assert.False(AllUpdateFlagsDisabled().ShouldSendUpdate(field));
    }

    [Fact]
    public void UnknownUpdateField_UsesOtherUpdatesFlag()
    {
        Assert.True(AllUpdateFlagsDisabled(otherUpdates: true)
            .ShouldSendUpdate("unknown_field"));
        Assert.False(AllUpdateFlagsDisabled()
            .ShouldSendUpdate("unknown_field"));
    }

    private static NotificationSettings SettingsWithOnly(string category) => new(
        IssueCreated: false,
        Comment: false,
        Status: category == "status",
        Assignee: category == "assignee",
        Priority: category == "priority",
        Title: category == "title",
        Date: category == "date",
        Label: category == "label",
        Points: category == "points",
        Draft: category == "draft",
        Description: category == "description",
        OtherUpdates: false,
        DescriptionDebounceSeconds: 45);

    private static NotificationSettings AllUpdateFlagsDisabled(
        bool otherUpdates = false) => new(
        IssueCreated: false,
        Comment: false,
        Status: false,
        Assignee: false,
        Priority: false,
        Title: false,
        Date: false,
        Label: false,
        Points: false,
        Draft: false,
        Description: false,
        OtherUpdates: otherUpdates,
        DescriptionDebounceSeconds: 45);
}
