using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static PmsPayload;

internal static class PlaneWebhookEndpoints
{
    public static void MapPlaneWebhook(
        this WebApplication app,
        string webhookToken,
        PlaneProjectCatalog projects,
        IPlaneUserDirectory planeUsers,
        PlaneWorkItemClient planeWorkItems,
        string planeTaskUrlTemplate,
        NotificationSettings notificationSettings,
        PmsMentionExtractor pmsMentionExtractor,
        ZulipMentionFormatter zulipMentionFormatter,
        PlaneCommentFormatter planeCommentFormatter,
        ZulipMessageSender zulipMessageSender,
        DescriptionNotificationDebouncer descriptionDebouncer)
    {
        // Keep this route unchanged for compatibility with the existing webhook.
        app.MapPost("/plane/{token}", async (
            string token,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!TokenMatches(token, webhookToken))
            {
                app.Logger.LogWarning(
                    "Rejected Plane webhook with an invalid token from {RemoteIp}",
                    request.HttpContext.Connection.RemoteIpAddress);
        
                return Results.Unauthorized();
            }
        
            JsonDocument document;
        
            try
            {
                document = await JsonDocument.ParseAsync(
                    request.Body,
                    cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                app.Logger.LogWarning(
                    exception,
                    "Received invalid Plane webhook JSON");
        
                return Results.BadRequest(new
                {
                    error = "Invalid JSON payload"
                });
            }
        
            using (document)
            {
                var root = document.RootElement;
        
                var eventName = String(root, "event");
                var action = String(root, "action");
                var webhookId = String(root, "webhook_id");
        
                var data = Object(root, "data");
                var activity = Object(root, "activity");
                var actor = Object(activity, "actor");
        
                var actorName = PersonName(actor);
                var actorEmail = String(actor, "email");
                var actorId =
                    String(actor, "id") ??
                    String(data, "actor") ??
                    String(data, "created_by");
                var actorUser = await HydratePlaneUserAsync(
                    planeUsers,
                    new PmsUserRef(actorId, actorEmail, actorName),
                    cancellationToken);
        
                var projectId = String(data, "project") ?? "";
                var project = await projects.ResolveAsync(
                    projectId,
                    cancellationToken);
        
                app.Logger.LogInformation(
                    "Received Plane webhook: Event={Event}, Action={Action}, " +
                    "Project={Project}, ProjectId={ProjectId}, WebhookId={WebhookId}",
                    eventName,
                    action,
                    project.Name,
                    projectId,
                    webhookId);
        
                string topic;
                string content;
                string? taskUrl = null;
                string? debouncedDescriptionIssueId = null;
        
                if (EqualsIgnoreCase(eventName, "issue"))
                {
                    var issueId = String(data, "id") ?? "";
                    var issueName = String(data, "name") ?? "Unnamed task";
                    var sequenceId = Number(data, "sequence_id");
        
                    var issueReference = sequenceId is not null
                        ? $"#{sequenceId}"
                        : ShortId(issueId);

                    var issueCreator = await ResolveIssueCreatorAsync(
                        data,
                        EqualsIgnoreCase(action, "created") ? actorUser : null,
                        planeUsers,
                        planeWorkItems,
                        projectId,
                        issueId,
                        cancellationToken);
        
                    taskUrl = BuildTaskUrl(
                            planeTaskUrlTemplate,
                        project.Identifier,
                        sequenceId);
        
                    topic = BuildTopic(
                        project.Name,
                        $"{issueReference}: {issueName}");
        
                    if (EqualsIgnoreCase(action, "created"))
                    {
                        if (!notificationSettings.IssueCreated)
                        {
                            return Results.Ok(new
                            {
                                ignored = true,
                                reason = "Issue-created notifications are disabled"
                            });
                        }
        
                        content = await BuildCreatedIssueMessage(
                            data,
                            project,
                            projectId,
                            issueReference,
                            taskUrl,
                            pmsMentionExtractor,
                            zulipMentionFormatter,
                            planeUsers,
                            planeWorkItems,
                            actorUser,
                            issueCreator,
                            cancellationToken);
                    }
                    else if (EqualsIgnoreCase(action, "updated"))
                    {
                        var changedField = String(activity, "field");
        
                        if (!notificationSettings.ShouldSendUpdate(changedField))
                        {
                            app.Logger.LogInformation(
                                "Ignored disabled {Field} update notification for issue {IssueId}",
                                changedField,
                                String(data, "id"));
        
                            return Results.Ok(new
                            {
                                ignored = true,
                                reason = $"{changedField ?? "Other"} notifications are disabled"
                            });
                        }
        
                        content = await BuildUpdatedIssueMessage(
                            data,
                            activity,
                            project,
                            projectId,
                            issueReference,
                            taskUrl,
                            pmsMentionExtractor,
                            zulipMentionFormatter,
                            planeUsers,
                            planeWorkItems,
                            actorUser,
                            issueCreator,
                            cancellationToken);
        
                        if (NotificationSettings.IsDescriptionField(changedField))
                            debouncedDescriptionIssueId = issueId;
                    }
                    else
                    {
                        app.Logger.LogInformation(
                            "Ignored unsupported Plane issue action: {Action}",
                            action);
        
                        return Results.Ok(new
                        {
                            ignored = true,
                            reason = $"Unsupported issue action: {action}"
                        });
                    }
                }
                else if (EqualsIgnoreCase(eventName, "issue_comment") &&
                         EqualsIgnoreCase(action, "created"))
                {
                    if (!notificationSettings.Comment)
                    {
                        return Results.Ok(new
                        {
                            ignored = true,
                            reason = "Comment notifications are disabled"
                        });
                    }
        
                    var issueId = String(data, "issue") ?? "";
        
                    var workItem = await planeWorkItems.GetAsync(
                        projectId,
                        issueId,
                        cancellationToken);
                    var issueReference = workItem.SequenceId is not null
                        ? $"#{workItem.SequenceId}"
                        : ShortId(issueId);

                    topic = BuildTopic(
                        project.Name,
                        $"{issueReference}: {workItem.Name}");

                    taskUrl = BuildTaskUrl(
                        planeTaskUrlTemplate,
                        project.Identifier,
                        workItem.SequenceId);

                    var attachmentNames = await planeWorkItems.GetAttachmentNamesAsync(
                        projectId,
                        issueId,
                        String(data, "id"),
                        cancellationToken);
        
                    content = await BuildCommentMessage(
                        data,
                        project,
                        projectId,
                        workItem,
                        attachmentNames,
                        taskUrl,
                        pmsMentionExtractor,
                        zulipMentionFormatter,
                        planeCommentFormatter,
                        actorUser,
                        cancellationToken);
                }
                else
                {
                    app.Logger.LogInformation(
                        "Ignored unsupported Plane webhook: Event={Event}, Action={Action}",
                        eventName,
                        action);
        
                    return Results.Ok(new
                    {
                        ignored = true,
                        reason = $"Unsupported event/action: {eventName}/{action}"
                    });
                }
        
                if (debouncedDescriptionIssueId is not null)
                {
                    descriptionDebouncer.Schedule(
                        debouncedDescriptionIssueId,
                        topic,
                        content);
        
                    return Results.Ok(new
                    {
                        ok = true,
                        scheduled = true,
                        debounceSeconds = notificationSettings.DescriptionDebounceSeconds,
                        project = project.Name,
                        topic,
                        taskUrl
                    });
                }
        
                var delivery = await zulipMessageSender.SendAsync(
                    topic,
                    content,
                    cancellationToken);
        
                if (delivery.Success)
                {
                    app.Logger.LogInformation(
                        "Delivered Plane webhook to Zulip. " +
                        "Event={Event}, Action={Action}, Project={Project}, " +
                        "Topic={Topic}, TaskUrl={TaskUrl}",
                        eventName,
                        action,
                        project.Name,
                        topic,
                        taskUrl);
        
                    return Results.Ok(new
                    {
                        ok = true,
                        project = project.Name,
                        topic,
                        taskUrl
                    });
                }
        
                app.Logger.LogError(
                    "Zulip delivery failed: Status={Status}, Error={Error}, Body={Body}",
                    delivery.StatusCode,
                    delivery.Error,
                    delivery.ResponseBody);
        
                if (delivery.Error == "timeout")
                {
                    return Results.Json(
                        new
                        {
                            error = "Zulip request timed out"
                        },
                        statusCode: StatusCodes.Status502BadGateway);
                }
        
                return Results.Json(
                    new
                    {
                        error = delivery.Error ?? "Zulip delivery failed",
                        zulipStatus = delivery.StatusCode,
                        zulipResponse = delivery.ResponseBody
                    },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    static async Task<string> BuildCreatedIssueMessage(
        JsonElement data,
        ProjectInfo project,
        string projectId,
        string issueReference,
        string? taskUrl,
        PmsMentionExtractor mentionExtractor,
        ZulipMentionFormatter mentionFormatter,
        IPlaneUserDirectory planeUsers,
        PlaneWorkItemClient planeWorkItems,
        PmsUserRef actorUser,
        PmsUserRef? issueCreator,
        CancellationToken cancellationToken)
    {
        var issueName = String(data, "name") ?? "Unnamed task";
    
        var message = new StringBuilder();
    
        message.AppendLine(
            $"### Created: **{EscapeMarkdown(issueReference)} " +
            $"{EscapeMarkdown(issueName)}**");
    
        message.AppendLine();
        message.AppendLine("#### Action");
    
        AddBullet(message, "Action", "Created");
    
        AddBullet(
            message,
            "Created by",
            await mentionFormatter.FormatUserAsync(
                actorUser,
                cancellationToken));
    
        AddBullet(
            message,
            "Project",
            ProjectDisplay(project));
    
        await AppendInvolvedUsersAsync(
            message,
            mentionFormatter,
            mentionExtractor.IssueCreatedUsers(data, actorUser),
            cancellationToken);
    
        await AppendCurrentIssueDetails(
            message,
            data,
            mentionFormatter,
            planeUsers,
            planeWorkItems,
            projectId,
            cancellationToken,
            issueCreator);
    
        AppendTaskLink(message, taskUrl);
    
        return message.ToString().Trim();
    }
    
    static async Task<string> BuildUpdatedIssueMessage(
        JsonElement data,
        JsonElement activity,
        ProjectInfo project,
        string projectId,
        string issueReference,
        string? taskUrl,
        PmsMentionExtractor mentionExtractor,
        ZulipMentionFormatter mentionFormatter,
        IPlaneUserDirectory planeUsers,
        PlaneWorkItemClient planeWorkItems,
        PmsUserRef actorUser,
        PmsUserRef? issueCreator,
        CancellationToken cancellationToken)
    {
        var issueName = String(data, "name") ?? "Unnamed task";
        var field = String(activity, "field");
    
        var message = new StringBuilder();
    
        message.AppendLine(
            $"### Updated: **{EscapeMarkdown(issueReference)} " +
            $"{EscapeMarkdown(issueName)}**");
    
        message.AppendLine();
        message.AppendLine("#### Action");
    
        AddBullet(message, "Action", "Updated");
    
        AddBullet(
            message,
            "Updated by",
            await mentionFormatter.FormatUserAsync(
                actorUser,
                cancellationToken));
    
        AddBullet(
            message,
            "Project",
            ProjectDisplay(project));
    
        AddBullet(
            message,
            "Changed field",
            FriendlyFieldName(field));
    
        await AppendInvolvedUsersAsync(
            message,
            mentionFormatter,
            mentionExtractor.IssueUpdatedUsers(data, activity, actorUser),
            cancellationToken);
    
        message.AppendLine();
        message.AppendLine("#### Change");
    
        await AppendChangeDetails(
            message,
            data,
            activity,
            field,
            mentionFormatter,
            planeUsers,
            planeWorkItems,
            projectId,
            cancellationToken);
    
        message.AppendLine();
        message.AppendLine("#### Current task details");
    
        await AppendCurrentIssueDetails(
            message,
            data,
            mentionFormatter,
            planeUsers,
            planeWorkItems,
            projectId,
            cancellationToken,
            issueCreator,
            includeHeading: false);
    
        AppendTaskLink(message, taskUrl);
    
        return message.ToString().Trim();
    }
    
    static async Task<string> BuildCommentMessage(
        JsonElement data,
        ProjectInfo project,
        string projectId,
        PlaneWorkItem workItem,
        IReadOnlyList<string> attachmentNames,
        string? taskUrl,
        PmsMentionExtractor mentionExtractor,
        ZulipMentionFormatter mentionFormatter,
        PlaneCommentFormatter planeCommentFormatter,
        PmsUserRef actorUser,
        CancellationToken cancellationToken)
    {
        var issueId = String(data, "issue") ?? "";
        var rawCommentHtml = String(data, "comment_html");
        var comment = !string.IsNullOrWhiteSpace(rawCommentHtml)
            ? await planeCommentFormatter.FormatAsync(
                rawCommentHtml,
                cancellationToken)
            : ExtractCommentText(data);
        var planeMentionedUsers = await planeCommentFormatter.MentionUsersAsync(
            rawCommentHtml,
            cancellationToken);
    
        comment = PmsMentionExtractor.NeutralizeBroadcastMentions(comment);
        comment = PmsMentionExtractor.ReplaceTeamMention(comment);
    
        var originalCommentForExtraction = comment;
        var structuredMentions = mentionExtractor.StructuredCommentMentions(data);
        var mentionedUsers = structuredMentions.Count > 0
            ? structuredMentions
            : planeMentionedUsers.Count > 0
                ? planeMentionedUsers
                : mentionExtractor.MentionEmailsFromText(comment);
        var mentionedUserDisplays =
            await mentionFormatter.FormatDistinctUsersAsync(
                mentionedUsers,
                cancellationToken);
    
        // Some mention-only comments have no readable text because the mention is
        // represented only in the structured mention metadata.
        if (string.IsNullOrWhiteSpace(comment) &&
            mentionedUserDisplays.Count > 0)
        {
            comment = string.Join(" ", mentionedUserDisplays);
        }
    
        var mentionsByEmail = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
    
        for (var index = 0; index < mentionedUsers.Count; index++)
        {
            var normalizedEmail = ZulipUserResolver.NormalizeEmail(
                mentionedUsers[index].Email);
    
            if (normalizedEmail is not null &&
                index < mentionedUserDisplays.Count &&
                mentionedUserDisplays[index].StartsWith(
                    "@**",
                    StringComparison.Ordinal))
            {
                mentionsByEmail[normalizedEmail] = mentionedUserDisplays[index];
            }
        }
    
        if (structuredMentions.Count == 0)
        {
            comment = mentionExtractor.ReplaceReliableTextMentions(
                comment,
                mentionsByEmail);
        }
    
        var title = workItem.SequenceId is not null
            ? $"#{workItem.SequenceId} {workItem.Name}"
            : $"{ShortId(issueId)} {workItem.Name}";
        var authorMention = await mentionFormatter.FormatUserAsync(
            actorUser,
            cancellationToken);
    
        var message = new StringBuilder();
    
        message.AppendLine(
            $"### Comment on **{EscapeMarkdown(title)}**");
    
        message.AppendLine();
        message.AppendLine("#### Comment information");
    
        AddBullet(
            message,
            "Author",
            authorMention);
    
        AddBullet(
            message,
            "Project",
            ProjectDisplay(project));
    
        AddBullet(
            message,
            "Mentioned users",
            mentionedUserDisplays.Count > 0
                ? string.Join(", ", mentionedUserDisplays)
                : "None");
    
        var commentInvolvedUsers = mentionExtractor.CommentUsers(
            data,
            data,
            actorUser,
            originalCommentForExtraction)
            .Concat(planeMentionedUsers)
            .ToArray();
    
        await AppendInvolvedUsersAsync(
            message,
            mentionFormatter,
            commentInvolvedUsers,
            cancellationToken);
    
        AddBullet(
            message,
            "Access",
            FormatValue(String(data, "access")));
    
        AddBullet(
            message,
            "Created",
            FormatDate(String(data, "created_at")));
    
        AddBullet(
            message,
            "Updated",
            FormatDate(String(data, "updated_at")));
    
        var editedAt = String(data, "edited_at");
    
        if (!string.IsNullOrWhiteSpace(editedAt))
        {
            AddBullet(
                message,
                "Edited",
                FormatDate(editedAt));
        }
    
        AddBullet(
            message,
            "Attachments",
            attachmentNames.Count == 0
                ? "None"
                : string.Join(", ", attachmentNames.Select(EscapeMarkdown)));
    
        message.AppendLine();
        message.AppendLine("#### Comment");
        message.AppendLine();
        message.AppendLine($"**By:** {authorMention}");
        message.AppendLine();
    
        message.AppendLine(
            string.IsNullOrWhiteSpace(comment)
                ? "_Empty comment_"
                : comment);
    
        AppendTaskLink(message, taskUrl);
    
        return message.ToString().Trim();
    }
    
    static string ExtractCommentText(JsonElement data)
    {
        // Plane normally provides comment_stripped as the readable text.
        var plainTextProperties = new[]
        {
            "comment_stripped",
            "comment",
            "body",
            "content"
        };
    
        foreach (var property in plainTextProperties)
        {
            var value = String(data, property);
    
            if (!string.IsNullOrWhiteSpace(value))
                return NormalizeText(StripHtml(value));
        }
    
        // Only use HTML after trying the plain-text properties.
        var commentHtml = String(data, "comment_html");
    
        if (!string.IsNullOrWhiteSpace(commentHtml))
        {
            var stripped = StripHtml(commentHtml);
    
            if (!string.IsNullOrWhiteSpace(stripped))
                return NormalizeText(stripped);
        }
    
        return "";
    }
    
    static async Task AppendInvolvedUsersAsync(
        StringBuilder message,
        ZulipMentionFormatter mentionFormatter,
        IReadOnlyList<PmsUserRef> users,
        CancellationToken cancellationToken)
    {
        var displays = await mentionFormatter.FormatDistinctUsersAsync(
            users,
            cancellationToken);
    
        if (displays.Count == 0)
            return;
    
        AddBullet(
            message,
            "Involved users",
            string.Join(", ", displays));
    }

    static async ValueTask<PmsUserRef> HydratePlaneUserAsync(
        IPlaneUserDirectory planeUsers,
        PmsUserRef user,
        CancellationToken cancellationToken)
    {
        var apiUser = await planeUsers.FindUserAsync(
            user.Id,
            cancellationToken);

        if (apiUser is null)
            return user;

        var displayName = user.DisplayName;

        if (string.IsNullOrWhiteSpace(displayName) ||
            displayName.Equals("Someone", StringComparison.OrdinalIgnoreCase))
        {
            displayName = apiUser.DisplayName;
        }

        return new PmsUserRef(
            user.Id ?? apiUser.Id,
            user.Email ?? apiUser.Email,
            displayName);
    }

    static async ValueTask<PmsUserRef?> ResolveIssueCreatorAsync(
        JsonElement data,
        PmsUserRef? fallback,
        IPlaneUserDirectory planeUsers,
        PlaneWorkItemClient planeWorkItems,
        string projectId,
        string issueId,
        CancellationToken cancellationToken)
    {
        var creator = PmsMentionExtractor.IssueCreator(data);
        var creatorId = creator?.Id ?? String(data, "created_by");

        if (string.IsNullOrWhiteSpace(creatorId) && fallback is null)
        {
            creatorId = (await planeWorkItems.GetAsync(
                projectId,
                issueId,
                cancellationToken)).CreatorId;
        }

        var apiCreator = await planeUsers.FindUserAsync(
            creatorId,
            cancellationToken);

        if (creator is null)
            return apiCreator ?? fallback;

        return new PmsUserRef(
            creator.Id ?? apiCreator?.Id,
            creator.Email ?? apiCreator?.Email,
            creator.DisplayName ?? apiCreator?.DisplayName);
    }

    static string NameList(IReadOnlyList<string> names) =>
        names.Count == 0
            ? "None"
            : string.Join(", ", names.Select(EscapeMarkdown));
    
    static async Task AppendChangeDetails(
        StringBuilder message,
        JsonElement data,
        JsonElement activity,
        string? field,
        ZulipMentionFormatter mentionFormatter,
        IPlaneUserDirectory planeUsers,
        PlaneWorkItemClient planeWorkItems,
        string projectId,
        CancellationToken cancellationToken)
    {
        var oldValue = Property(activity, "old_value");
        var newValue = Property(activity, "new_value");
    
        if (NotificationSettings.IsStatusField(field))
        {
            var currentState = Object(data, "state");
    
            var oldStateName = GetStateDisplayName(
                    activity,
                    "old") ??
                await planeWorkItems.FindStateNameAsync(
                    projectId,
                    JsonIdentifier(oldValue),
                    cancellationToken);
    
            var newStateName =
                String(currentState, "name")
                ?? GetStateDisplayName(activity, "new")
                ?? await planeWorkItems.FindStateNameAsync(
                    projectId,
                    String(currentState, "id") ?? JsonIdentifier(newValue),
                    cancellationToken);
    
            // Only show the old state when its readable name is included
            // in the webhook. Never show its UUID as a fallback.
            if (!string.IsNullOrWhiteSpace(oldStateName))
            {
                AddBullet(
                    message,
                    "Old state",
                    FormatValue(oldStateName));
            }
    
            AddBullet(
                message,
                "New state",
                FormatValue(newStateName));
    
            return;
        }
    
        if (NotificationSettings.IsAssigneeField(field))
        {
            await AppendAssigneeChanges(
                message,
                data,
                oldValue,
                newValue,
                mentionFormatter,
                planeUsers,
                cancellationToken);
    
            return;
        }
    
        if (NotificationSettings.IsDescriptionField(field))
        {
            var description =
                String(data, "description_stripped") ??
                StripHtml(String(data, "description_html"));
    
            if (string.IsNullOrWhiteSpace(description))
                description = StripHtml(ValueAsString(newValue));
    
            description = PmsMentionExtractor.ReplaceTeamMention(
                PmsMentionExtractor.NeutralizeBroadcastMentions(description));
    
            message.AppendLine("* **Current description:**");
            message.AppendLine();
            message.AppendLine(
                string.IsNullOrWhiteSpace(description)
                    ? "_Empty_"
                    : description);
    
            return;
        }
    
        if (NotificationSettings.IsPriorityField(field))
        {
            AddBullet(
                message,
                "Previous priority",
                FormatPriority(ValueAsString(oldValue)));
    
            AddBullet(
                message,
                "New priority",
                FormatPriority(ValueAsString(newValue)));
    
            return;
        }
    
        if (NotificationSettings.IsTitleField(field))
        {
            AddBullet(
                message,
                "Previous title",
                FormatValue(ValueAsString(oldValue)));
    
            AddBullet(
                message,
                "New title",
                FormatValue(ValueAsString(newValue)));
    
            return;
        }
    
        if (NotificationSettings.IsDateField(field))
        {
            AddBullet(
                message,
                "Previous value",
                FormatDate(ValueAsString(oldValue)));
    
            AddBullet(
                message,
                "New value",
                FormatDate(ValueAsString(newValue)));
    
            return;
        }
    
        if (NotificationSettings.IsLabelField(field))
        {
            var oldLabels = await planeWorkItems.FindLabelNamesAsync(
                projectId,
                StringArray(oldValue),
                cancellationToken);
            var newLabels = await planeWorkItems.FindLabelNamesAsync(
                projectId,
                StringArray(newValue),
                cancellationToken);

            AddBullet(
                message,
                "Previous labels",
                NameList(oldLabels));
    
            AddBullet(
                message,
                "New labels",
                NameList(newLabels));
    
            AddBullet(
                message,
                "Current labels",
                await LabelsAsync(
                    data,
                    planeWorkItems,
                    projectId,
                    cancellationToken));
    
            return;
        }
    
        if (NotificationSettings.IsPointsField(field))
        {
            AddBullet(
                message,
                "Previous points",
                JsonValueDisplay(oldValue));
    
            AddBullet(
                message,
                "New points",
                JsonValueDisplay(newValue));
    
            return;
        }
    
        if (NotificationSettings.IsDraftField(field))
        {
            AddBullet(
                message,
                "Previous draft status",
                JsonValueDisplay(oldValue));
    
            AddBullet(
                message,
                "New draft status",
                JsonValueDisplay(newValue));
    
            return;
        }
    
        AddBullet(
            message,
            "Previous value",
            JsonValueDisplay(oldValue));
    
        AddBullet(
            message,
            "New value",
            JsonValueDisplay(newValue));
    }
    
    static string? GetStateDisplayName(
        JsonElement activity,
        string prefix)
    {
        /*
         * Different Plane versions may use different properties
         * for readable old/new state values. Check the known formats.
         */
    
        var stateObject = Object(
            activity,
            $"{prefix}_state");
    
        var name = String(stateObject, "name");
    
        if (IsReadableName(name))
            return name!.Trim();
    
        var value = Property(
            activity,
            $"{prefix}_value");
    
        if (value.ValueKind == JsonValueKind.Object)
        {
            name =
                String(value, "name")
                ?? String(value, "title")
                ?? String(value, "label");
    
            if (IsReadableName(name))
                return name!.Trim();
        }
    
        var possibleProperties = new[]
        {
            $"{prefix}_state_name",
            $"{prefix}_value_name",
            $"{prefix}_identifier",
            $"{prefix}_display_value"
        };
    
        foreach (var property in possibleProperties)
        {
            name = String(activity, property);
    
            if (IsReadableName(name))
                return name!.Trim();
        }
    
        return null;
    }
    
    static bool IsReadableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
    
        // Do not accidentally display a UUID as the state name.
        return !Guid.TryParse(value, out _);
    }
    
    static async Task AppendAssigneeChanges(
        StringBuilder message,
        JsonElement data,
        JsonElement oldValue,
        JsonElement newValue,
        ZulipMentionFormatter mentionFormatter,
        IPlaneUserDirectory planeUsers,
        CancellationToken cancellationToken)
    {
        var oldIds = StringArray(oldValue);
        var newIds = StringArray(newValue);
        var currentAssignees = await AssigneeDictionary(
            data,
            mentionFormatter,
            planeUsers,
            cancellationToken);
    
        var addedIds = newIds
            .Except(
                oldIds,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    
        var removedIds = oldIds
            .Except(
                newIds,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    
        var addedNames = await ResolvePlaneUserDisplaysAsync(
            addedIds,
            currentAssignees,
            mentionFormatter,
            planeUsers,
            cancellationToken);
        var removedNames = await ResolvePlaneUserDisplaysAsync(
            removedIds,
            currentAssignees,
            mentionFormatter,
            planeUsers,
            cancellationToken);
    
        if (addedIds.Length > 0)
        {
            AddBullet(
                message,
                "Added assignees",
            addedNames.Count > 0
                ? string.Join(", ", addedNames)
                    : CountDisplay(
                        addedIds.Length,
                        "assignee added",
                        "assignees added"));
        }
    
        if (removedIds.Length > 0)
        {
            AddBullet(
                message,
                "Removed assignees",
            removedNames.Count > 0
                ? string.Join(", ", removedNames)
                : CountDisplay(
                    removedIds.Length,
                    "assignee removed",
                    "assignees removed"));
        }
    
        AddBullet(
            message,
            "Current assignees",
            await Assignees(
                data,
                mentionFormatter,
                planeUsers,
                cancellationToken));
    }
    
    static string CountDisplay(
        int count,
        string singular,
        string plural)
    {
        return count == 1
            ? $"1 {singular}"
            : $"{count} {plural}";
    }

    static async Task AppendCurrentIssueDetails(
        StringBuilder message,
        JsonElement data,
        ZulipMentionFormatter mentionFormatter,
        IPlaneUserDirectory planeUsers,
        PlaneWorkItemClient planeWorkItems,
        string projectId,
        CancellationToken cancellationToken,
        PmsUserRef? issueCreator = null,
        bool includeHeading = true)
    {
        if (includeHeading)
        {
            message.AppendLine();
            message.AppendLine("#### Task details");
        }
    
        var state = Object(data, "state");
    
        var stateName = String(state, "name")
            ?? await planeWorkItems.FindStateNameAsync(
                projectId,
                String(state, "id") ?? String(data, "state_id"),
                cancellationToken);
        var stateGroup = String(state, "group");
        var stateColor = String(state, "color");
    
        var stateDisplay = stateName;
    
        if (!string.IsNullOrWhiteSpace(stateGroup))
        {
            stateDisplay =
                $"{stateName ?? "Unknown"} ({stateGroup})";
        }
    
        AddBullet(
            message,
            "Title",
            FormatValue(String(data, "name")));
    
        AddBullet(
            message,
            "Sequence",
            SequenceDisplay(data));
    
        AddBullet(
            message,
            "State",
            FormatValue(stateDisplay));
    
        AddBullet(
            message,
            "State color",
            FormatValue(stateColor));
    
        AddBullet(
            message,
            "Priority",
            FormatPriority(String(data, "priority")));
    
        AddBullet(
            message,
            "Assignees",
            await Assignees(
                data,
                mentionFormatter,
                planeUsers,
                cancellationToken));
    
        AddBullet(
            message,
            "Labels",
            await LabelsAsync(
                data,
                planeWorkItems,
                projectId,
                cancellationToken));
    
        var points =
            Number(data, "point")
            ?? Number(data, "estimate_point");
    
        AddBullet(
            message,
            "Estimate points",
            points?.ToString() ?? "Not set");
    
        AddBullet(
            message,
            "Start date",
            FormatDate(String(data, "start_date")));
    
        AddBullet(
            message,
            "Target date",
            FormatDate(String(data, "target_date")));

        var creator = PmsMentionExtractor.IssueCreator(data) ?? issueCreator;

        AddBullet(
            message,
            "Created by",
            creator is null
                ? "Not available"
                : await mentionFormatter.FormatUserAsync(
                    creator,
                    cancellationToken));
    
        AddBullet(
            message,
            "Created",
            FormatDate(String(data, "created_at")));
    
        AddBullet(
            message,
            "Last updated",
            FormatDate(String(data, "updated_at")));
    
        AddBullet(
            message,
            "Completed",
            FormatDate(String(data, "completed_at")));
    
        AddBullet(
            message,
            "Archived",
            FormatDate(String(data, "archived_at")));
    
        AddBullet(
            message,
            "Draft",
            BooleanDisplay(data, "is_draft"));
    
        var type = String(data, "type");
    
        if (!string.IsNullOrWhiteSpace(type))
        {
            AddBullet(
                message,
                "Task type",
                FormatValue(type));
        }
    
        var externalSource = String(data, "external_source");
    
        if (!string.IsNullOrWhiteSpace(externalSource))
        {
            AddBullet(
                message,
                "External source",
                FormatValue(externalSource));
        }
    }
    
    static void AppendTaskLink(
        StringBuilder message,
        string? taskUrl)
    {
        if (string.IsNullOrWhiteSpace(taskUrl))
            return;
    
        message.AppendLine();
        message.AppendLine("---");
        message.AppendLine(
            $"[**Open this task in Plane →**]({taskUrl})");
    }
    
    static string? BuildTaskUrl(
        string? template,
        string? projectIdentifier,
        long? sequenceId)
    {
        if (string.IsNullOrWhiteSpace(template) ||
            string.IsNullOrWhiteSpace(projectIdentifier) ||
            sequenceId is null)
        {
            return null;
        }
    
        var url = template
            .Replace(
                "{projectIdentifier}",
                Uri.EscapeDataString(projectIdentifier),
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{sequenceId}",
                sequenceId.Value.ToString(),
                StringComparison.OrdinalIgnoreCase);
    
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri
            : null;
    }
    
    static string ProjectDisplay(ProjectInfo project) =>
        EscapeMarkdown(project.Name);
    
    static string BuildTopic(
        string? projectName,
        params string?[] titleParts)
    {
        var projectSlug = ProjectTopicSlug(projectName);
    
        var title = string.Join(
            " ",
            titleParts
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => NormalizeTopicTitle(part!)));
    
        var topic = string.IsNullOrWhiteSpace(title)
            ? $"[{projectSlug}]"
            : $"[{projectSlug}] {title}";
    
        // Keep the conservative Zulip topic-length limit.
        return topic[..Math.Min(60, topic.Length)];
    }
    
    static string ProjectTopicSlug(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return "unknown-project";
    
        var slug = projectName
            .Trim()
            .ToLowerInvariant();
    
        // Convert spaces and underscores to hyphens.
        slug = Regex.Replace(slug, @"[\s_]+", "-");
    
        // Remove characters that are unsuitable for the bracketed project slug.
        // Unicode letters and numbers are retained.
        slug = Regex.Replace(slug, @"[^\p{L}\p{N}-]+", "-");
    
        // Collapse repeated hyphens.
        slug = Regex.Replace(slug, @"-+", "-");
    
        slug = slug.Trim('-');
    
        return string.IsNullOrWhiteSpace(slug)
            ? "unknown-project"
            : slug;
    }
    
    static string NormalizeTopicTitle(string value)
    {
        return Regex.Replace(value, @"\s+", " ")
            .Replace("|", "-")
            .Trim();
    }
    
    static async Task<string> Assignees(
        JsonElement data,
        ZulipMentionFormatter mentionFormatter,
        IPlaneUserDirectory planeUsers,
        CancellationToken cancellationToken)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("assignees", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return "Unassigned";
        }
    
        var assigneeUsers = new List<PmsUserRef>();

        foreach (var assignee in array.EnumerateArray())
        {
            var id = assignee.ValueKind == JsonValueKind.String
                ? assignee.GetString()
                : String(assignee, "id");
            var apiUser = await planeUsers.FindUserAsync(id, cancellationToken);

            if (assignee.ValueKind == JsonValueKind.Object)
            {
                assigneeUsers.Add(new PmsUserRef(
                    id ?? apiUser?.Id,
                    String(assignee, "email") ?? apiUser?.Email,
                    PersonName(assignee) ?? apiUser?.DisplayName));
            }
            else if (apiUser is not null)
            {
                assigneeUsers.Add(apiUser);
            }
        }
    
        if (assigneeUsers.Count == 0)
            return "Unassigned";
    
        var assignees = await mentionFormatter.FormatDistinctUsersAsync(
            assigneeUsers,
            cancellationToken);
    
        return assignees.Count == 0
            ? "Unassigned"
            : string.Join(", ", assignees);
    }
    
    static async Task<Dictionary<string, string>> AssigneeDictionary(
        JsonElement data,
        ZulipMentionFormatter mentionFormatter,
        IPlaneUserDirectory planeUsers,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
    
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("assignees", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
    
        foreach (var assignee in array.EnumerateArray())
        {
            var id = assignee.ValueKind == JsonValueKind.String
                ? assignee.GetString()
                : String(assignee, "id");
    
            if (string.IsNullOrWhiteSpace(id))
                continue;
    
            var apiUser = await planeUsers.FindUserAsync(id, cancellationToken);
            var user = assignee.ValueKind == JsonValueKind.Object
                ? new PmsUserRef(
                    id,
                    String(assignee, "email") ?? apiUser?.Email,
                    PersonName(assignee) ?? apiUser?.DisplayName)
                : apiUser ?? new PmsUserRef(id, null, null);
            var display = await mentionFormatter.FormatUserAsync(user, cancellationToken);
    
            result[id] = display;
        }
    
        return result;
    }

    static async Task<IReadOnlyList<string>> ResolvePlaneUserDisplaysAsync(
        IEnumerable<string> userIds,
        IReadOnlyDictionary<string, string> currentAssignees,
        ZulipMentionFormatter mentionFormatter,
        IPlaneUserDirectory planeUsers,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();

        foreach (var userId in userIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (currentAssignees.TryGetValue(userId, out var currentDisplay))
            {
                values.Add(currentDisplay);
                continue;
            }

            var user = await planeUsers.FindUserAsync(userId, cancellationToken);
            if (user is not null)
            {
                values.Add(await mentionFormatter.FormatUserAsync(user, cancellationToken));
            }
        }

        return values;
    }
    
    static async Task<string> LabelsAsync(
        JsonElement data,
        PlaneWorkItemClient planeWorkItems,
        string projectId,
        CancellationToken cancellationToken)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("labels", out var labels) ||
            labels.ValueKind != JsonValueKind.Array)
        {
            return "None";
        }
    
        var values = new List<string>();
        var unresolvedIds = new List<string>();

        foreach (var label in labels.EnumerateArray())
        {
            if (label.ValueKind == JsonValueKind.String)
            {
                var value = label.GetString();
                if (IsReadableName(value))
                    values.Add(value!);
                else if (!string.IsNullOrWhiteSpace(value))
                    unresolvedIds.Add(value);
            }
            else if (label.ValueKind == JsonValueKind.Object)
            {
                var name = String(label, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    values.Add(name);
                else
                {
                    var id = String(label, "id");
                    if (!string.IsNullOrWhiteSpace(id))
                        unresolvedIds.Add(id);
                }
            }
        }

        values.AddRange(await planeWorkItems.FindLabelNamesAsync(
            projectId,
            unresolvedIds,
            cancellationToken));
    
        return values.Count == 0
            ? "None"
            : string.Join(", ", values.Distinct(StringComparer.OrdinalIgnoreCase).Select(EscapeMarkdown));
    }
    
    static string FriendlyFieldName(string? field)
    {
        return field?.Trim().ToLowerInvariant() switch
        {
            "state_id" or "state" or "status" => "Status",
            "assignee_ids" or "assignees" or "assignee" => "Assignees",
            "description" or "description_html" or "description_stripped" => "Description",
            "priority" => "Priority",
            "name" or "title" => "Title",
            "start_date" => "Start date",
            "target_date" => "Target date",
            "label_ids" or "labels" or "label" => "Labels",
            "point" or "points" => "Points",
            "estimate_point" or "estimate_points" => "Estimate points",
            "parent_id" => "Parent task",
            "is_draft" or "draft" => "Draft status",
            null or "" => "Unspecified",
            _ => field.Replace('_', ' ')
        };
    }
    
    static string SequenceDisplay(JsonElement data)
    {
        var sequence = Number(data, "sequence_id");
    
        return sequence is null
            ? "Not available"
            : $"#{sequence}";
    }
    
    static string FormatPriority(string? priority)
    {
        return priority?.ToLowerInvariant() switch
        {
            "urgent" => "**Urgent**",
            "high" => "**High**",
            "medium" => "Medium",
            "low" => "Low",
            "none" => "None",
            null or "" => "Not set",
            _ => EscapeMarkdown(priority)
        };
    }
    
    static string FormatDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Not set";
    
        if (DateTimeOffset.TryParse(value, out var date))
        {
            return $"{date.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC";
        }
    
        return EscapeMarkdown(value);
    }
    
    static string BooleanDisplay(
        JsonElement element,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
        {
            return "Unknown";
        }
    
        return value.ValueKind switch
        {
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            _ => "Unknown"
        };
    }
    
    static IReadOnlyList<string> StringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
    
        return element
            .EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }
    
    static string JsonValueDisplay(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Undefined => "Not provided",
            JsonValueKind.Null => "None",
            JsonValueKind.String => FormatValue(value.GetString()),
            JsonValueKind.Number => Code(value.GetRawText()),
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
    
            JsonValueKind.Array => value.GetArrayLength() == 0
                ? "None"
                : string.Join(
                    ", ",
                    value.EnumerateArray().Select(JsonValueDisplay)),
    
            JsonValueKind.Object => Code(
                Limit(value.GetRawText(), 1000)),
    
            _ => Code(Limit(value.GetRawText(), 1000))
        };
    }
    
    static string ValueAsString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null => "",
            JsonValueKind.Undefined => "",
            _ => value.GetRawText()
        };
    }

    static string? JsonIdentifier(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => String(value, "id"),
            _ => null
        };
    }
    
    static string FormatValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "Not set"
            : EscapeMarkdown(value);
    }
    
    static void AddBullet(
        StringBuilder message,
        string label,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
    
        message.AppendLine(
            $"* **{EscapeMarkdown(label)}:** {value}");
    }
    
    static string NormalizeText(string value)
    {
        return value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();
    }
    
    static string EscapeMarkdown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
    
        return value
            .Replace("\\", "\\\\")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("`", "\\`")
            .Replace("[", "\\[")
            .Replace("]", "\\]");
    }
    
    static string EscapeCode(string value)
    {
        return value.Replace("`", "ˋ");
    }
    
    static string Code(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
    
        return $"`{EscapeCode(value)}`";
    }
    
    static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";
    
        return value[..Math.Min(8, value.Length)];
    }
    
    static string Limit(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
            return value;
    
        return value[..maximumLength] + "…";
    }
    
    static bool EqualsIgnoreCase(
        string? left,
        string? right)
    {
        return string.Equals(
            left,
            right,
            StringComparison.OrdinalIgnoreCase);
    }
    
    static bool TokenMatches(
        string suppliedToken,
        string expectedToken)
    {
        var suppliedHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(suppliedToken));
    
        var expectedHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(expectedToken));
    
        return CryptographicOperations.FixedTimeEquals(
            suppliedHash,
            expectedHash);
    }
    
    static string StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
    
        value = Regex.Replace(
            value,
            @"</?br\s*/?>",
            "\n",
            RegexOptions.IgnoreCase);
    
        value = Regex.Replace(
            value,
            @"</?(?:p|div|blockquote|h[1-6])\b[^>]*>",
            "\n",
            RegexOptions.IgnoreCase);
    
        value = Regex.Replace(
            value,
            @"<li[^>]*>",
            "• ",
            RegexOptions.IgnoreCase);
    
        value = Regex.Replace(
            value,
            @"</li\s*>",
            "\n",
            RegexOptions.IgnoreCase);
    
        value = Regex.Replace(
            value,
            @"<[^>]+>",
            "");
    
        value = WebUtility.HtmlDecode(value);
    
        value = Regex.Replace(
            value,
            @"[ \t]+\n",
            "\n");
    
        value = Regex.Replace(
            value,
            @"\n{3,}",
            "\n\n");
    
        return value.Trim();
    }
}
