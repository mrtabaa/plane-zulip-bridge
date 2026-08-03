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
        IReadOnlyDictionary<string, ProjectInfo> projects,
        string pmsTaskUrlTemplate,
        IssueCacheStore issueCache,
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
                    "Rejected PMS webhook with an invalid token from {RemoteIp}",
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
                    "Received invalid PMS webhook JSON");
        
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
        
                var workspaceId =
                    String(root, "workspace_id")
                    ?? String(data, "workspace");
        
                var actorName = PersonName(actor);
                var actorEmail = String(actor, "email");
                var actorId = String(actor, "id");
                var actorUser = new PmsUserRef(
                    actorId,
                    actorEmail,
                    actorName);
        
                var projectId = String(data, "project") ?? "";
                var project = BridgeConfiguration.ResolveProject(projectId, projects);
        
                app.Logger.LogInformation(
                    "Received PMS webhook: Event={Event}, Action={Action}, " +
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
        
                    /*
                     * Save issue information for later comment webhooks.
                     */
                    if (!string.IsNullOrWhiteSpace(issueId))
                    {
                        issueCache.Upsert(new IssueInfo(
                            IssueId: issueId,
                            Name: issueName,
                            SequenceId: sequenceId,
                            ProjectId: projectId,
                            ProjectName: project.Name,
                            ProjectIdentifier: project.Identifier));
                    }
        
                    taskUrl = BuildTaskUrl(
                        pmsTaskUrlTemplate,
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
                            actorName,
                            actorEmail,
                            actorId,
                            project,
                            projectId,
                            issueReference,
                            webhookId,
                            workspaceId,
                            taskUrl,
                            pmsMentionExtractor,
                            zulipMentionFormatter,
                            actorUser,
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
                            actorName,
                            actorEmail,
                            actorId,
                            project,
                            projectId,
                            issueReference,
                            webhookId,
                            workspaceId,
                            taskUrl,
                            pmsMentionExtractor,
                            zulipMentionFormatter,
                            actorUser,
                            cancellationToken);
        
                        if (NotificationSettings.IsDescriptionField(changedField))
                            debouncedDescriptionIssueId = issueId;
                    }
                    else
                    {
                        app.Logger.LogInformation(
                            "Ignored unsupported PMS issue action: {Action}",
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
        
                    app.Logger.LogInformation(
                        "Raw PMS comment webhook payload: {Payload}",
                        Limit(root.GetRawText(), 30000));
        
                    app.Logger.LogInformation(
                        "Raw issue-comment data: {CommentData}",
                        Limit(data.GetRawText(), 20000));
        
                    foreach (var property in new[]
                    {
                        "comment_html",
                        "comment_stripped",
                        "comment",
                        "body",
                        "content",
                        "mentions",
                        "mentioned_users",
                        "mention_users",
                        "user_mentions"
                    })
                    {
                        LogJsonProperty(
                            app.Logger,
                            data,
                            property);
                    }
        
                    var issueId = String(data, "issue") ?? "";
        
                    /*
                     * A comment payload does not contain sequence_id or task name.
                     * Try to retrieve them from the in-memory cache.
                     */
                    issueCache.TryGet(issueId, out var cachedIssue);
        
                    if (cachedIssue is not null)
                    {
                        var cachedReference = cachedIssue.SequenceId is not null
                            ? $"#{cachedIssue.SequenceId}"
                            : ShortId(issueId);
        
                        topic = BuildTopic(
                            cachedIssue.ProjectName,
                            $"{cachedReference}: {cachedIssue.Name}");
        
                        taskUrl = BuildTaskUrl(
                            pmsTaskUrlTemplate,
                            cachedIssue.ProjectIdentifier,
                            cachedIssue.SequenceId);
                    }
                    else
                    {
                        /*
                         * The cache can be empty after a container restart.
                         * The comment will still be sent, but the payload alone
                         * cannot provide PERSKHAB-285 because it has no sequence_id.
                         */
                        topic = BuildTopic(
                            project.Name,
                            $"Task {ShortId(issueId)}");
                    }
        
                    content = await BuildCommentMessage(
                        data,
                        actorName,
                        actorEmail,
                        actorId,
                        project,
                        projectId,
                        cachedIssue,
                        webhookId,
                        workspaceId,
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
                        "Ignored unsupported PMS webhook: Event={Event}, Action={Action}",
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
                        "Delivered PMS webhook to Zulip. " +
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

    static void LogJsonProperty(
        ILogger logger,
        JsonElement element,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
        {
            logger.LogInformation(
                "Comment property {Property}: NOT PRESENT",
                property);
    
            return;
        }
    
        logger.LogInformation(
            "Comment property {Property}: Kind={Kind}, Value={Value}",
            property,
            value.ValueKind,
            Limit(value.GetRawText(), 10000));
    }
    
    static async Task<string> BuildCreatedIssueMessage(
        JsonElement data,
        string actorName,
        string? actorEmail,
        string? actorId,
        ProjectInfo project,
        string projectId,
        string issueReference,
        string? webhookId,
        string? workspaceId,
        string? taskUrl,
        PmsMentionExtractor mentionExtractor,
        ZulipMentionFormatter mentionFormatter,
        PmsUserRef actorUser,
        CancellationToken cancellationToken)
    {
        var issueName = String(data, "name") ?? "Unnamed task";
        // var description = Description(data);
    
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
            ProjectDisplay(project, projectId));
    
        await AppendInvolvedUsersAsync(
            message,
            mentionFormatter,
            mentionExtractor.IssueCreatedUsers(data, actorUser),
            cancellationToken);
    
        await AppendCurrentIssueDetails(
            message,
            data,
            mentionFormatter,
            cancellationToken);
    
        // if (!string.IsNullOrWhiteSpace(description))
        // {
        //     message.AppendLine();
        //     message.AppendLine("#### Description");
        //     message.AppendLine(Quote(description));
        // }
    
        // AppendTechnicalDetails(
        //     message,
        //     webhookId,
        //     workspaceId,
        //     projectId,
        //     String(data, "id"));
    
        AppendTaskLink(message, taskUrl);
    
        return message.ToString().Trim();
    }
    
    static async Task<string> BuildUpdatedIssueMessage(
        JsonElement data,
        JsonElement activity,
        string actorName,
        string? actorEmail,
        string? actorId,
        ProjectInfo project,
        string projectId,
        string issueReference,
        string? webhookId,
        string? workspaceId,
        string? taskUrl,
        PmsMentionExtractor mentionExtractor,
        ZulipMentionFormatter mentionFormatter,
        PmsUserRef actorUser,
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
            ProjectDisplay(project, projectId));
    
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
            cancellationToken);
    
        message.AppendLine();
        message.AppendLine("#### Current task details");
    
        await AppendCurrentIssueDetails(
            message,
            data,
            mentionFormatter,
            cancellationToken,
            includeHeading: false);
    
        // var description = Description(data);
    
        // if (!string.IsNullOrWhiteSpace(description) &&
        //     !EqualsIgnoreCase(field, "description_html"))
        // {
        //     message.AppendLine();
        //     message.AppendLine("#### Current description");
        //     message.AppendLine(Quote(description));
        // }
    
        // AppendTechnicalDetails(
        //     message,
        //     webhookId,
        //     workspaceId,
        //     projectId,
        //     String(data, "id"));
    
        AppendTaskLink(message, taskUrl);
    
        return message.ToString().Trim();
    }
    
    static async Task<string> BuildCommentMessage(
        JsonElement data,
        string actorName,
        string? actorEmail,
        string? actorId,
        ProjectInfo project,
        string projectId,
        IssueInfo? cachedIssue,
        string? webhookId,
        string? workspaceId,
        string? taskUrl,
        PmsMentionExtractor mentionExtractor,
        ZulipMentionFormatter mentionFormatter,
        PlaneCommentFormatter planeCommentFormatter,
        PmsUserRef actorUser,
        CancellationToken cancellationToken)
    {
        var issueId = String(data, "issue") ?? "";
        var commentId = String(data, "id");
        var rawCommentHtml = String(data, "comment_html");
        var comment = !string.IsNullOrWhiteSpace(rawCommentHtml)
            ? await planeCommentFormatter.FormatAsync(
                rawCommentHtml,
                cancellationToken)
            : ExtractCommentText(data);
        var planeMentionedUsers = planeCommentFormatter.MentionUsers(
            rawCommentHtml);
    
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
    
        var title = cachedIssue is not null
            ? cachedIssue.SequenceId is not null
                ? $"#{cachedIssue.SequenceId} {cachedIssue.Name}"
                : $"{ShortId(issueId)} {cachedIssue.Name}"
            : ShortId(issueId);
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
            ProjectDisplay(project, projectId));
    
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
    
        var attachments = Attachments(data);
    
        AddBullet(
            message,
            "Attachments",
            attachments);
    
        message.AppendLine();
        message.AppendLine("#### Comment");
        message.AppendLine();
        message.AppendLine($"**By:** {authorMention}");
        message.AppendLine();
    
        message.AppendLine(
            string.IsNullOrWhiteSpace(comment)
                ? "_Empty comment_"
                : comment);
    
        // AppendTechnicalDetails(
        //     message,
        //     webhookId,
        //     workspaceId,
        //     projectId,
        //     issueId,
        //     commentId);
    
        AppendTaskLink(message, taskUrl);
    
        if (string.IsNullOrWhiteSpace(taskUrl))
        {
            message.AppendLine();
            message.AppendLine(
                "_A direct task link was unavailable because the comment " +
                "webhook did not contain the task sequence number and the " +
                "task was not present in the bridge's in-memory cache._");
        }
    
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
    
    static async Task AppendChangeDetails(
        StringBuilder message,
        JsonElement data,
        JsonElement activity,
        string? field,
        ZulipMentionFormatter mentionFormatter,
        CancellationToken cancellationToken)
    {
        var oldValue = Property(activity, "old_value");
        var newValue = Property(activity, "new_value");
    
        if (EqualsIgnoreCase(field, "state_id"))
        {
            var currentState = Object(data, "state");
    
            var oldStateName = GetStateDisplayName(
                activity,
                "old");
    
            var newStateName =
                String(currentState, "name")
                ?? GetStateDisplayName(activity, "new");
    
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
    
        if (EqualsIgnoreCase(field, "assignee_ids"))
        {
            await AppendAssigneeChanges(
                message,
                data,
                oldValue,
                newValue,
                mentionFormatter,
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
    
        if (EqualsIgnoreCase(field, "priority"))
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
    
        if (EqualsIgnoreCase(field, "name"))
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
    
        if (EqualsIgnoreCase(field, "start_date") ||
            EqualsIgnoreCase(field, "target_date"))
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
    
        if (EqualsIgnoreCase(field, "label_ids"))
        {
            AddBullet(
                message,
                "Previous label IDs",
                JsonValueDisplay(oldValue));
    
            AddBullet(
                message,
                "New label IDs",
                JsonValueDisplay(newValue));
    
            AddBullet(
                message,
                "Current labels",
                Labels(data));
    
            return;
        }
    
        if (EqualsIgnoreCase(field, "point") ||
            EqualsIgnoreCase(field, "estimate_point"))
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
    
        if (EqualsIgnoreCase(field, "is_draft"))
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
         * Different PMS/Plane versions may use different properties
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
        CancellationToken cancellationToken)
    {
        var oldIds = StringArray(oldValue);
        var newIds = StringArray(newValue);
        var currentAssignees = await AssigneeDictionary(
            data,
            mentionFormatter,
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
    
        var addedNames = addedIds
            .Where(currentAssignees.ContainsKey)
            .Select(id => currentAssignees[id])
            .ToArray();
    
        if (addedIds.Length > 0)
        {
            AddBullet(
                message,
                "Added assignees",
                addedNames.Length > 0
                    ? string.Join(", ", addedNames)
                    : CountDisplay(
                        addedIds.Length,
                        "assignee added",
                        "assignees added"));
        }
    
        /*
         * The current issue data generally does not include information
         * about removed assignees. Show the count instead of their UUIDs.
         */
        if (removedIds.Length > 0)
        {
            AddBullet(
                message,
                "Removed assignees",
                CountDisplay(
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
        CancellationToken cancellationToken,
        bool includeHeading = true)
    {
        if (includeHeading)
        {
            message.AppendLine();
            message.AppendLine("#### Task details");
        }
    
        var state = Object(data, "state");
    
        var stateName = String(state, "name");
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
                cancellationToken));
    
        AddBullet(
            message,
            "Labels",
            Labels(data));
    
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
    
    // static void AppendTechnicalDetails(
    //     StringBuilder message,
    //     string? webhookId,
    //     string? workspaceId,
    //     string? projectId,
    //     string? issueId,
    //     string? commentId = null)
    // {
    //     message.AppendLine();BuildCommentMessage
    //     message.AppendLine("#### Technical details");
    
    //     AddBullet(message, "Task ID", Code(issueId));
    //     AddBullet(message, "Comment ID", Code(commentId));
    //     AddBullet(message, "Project ID", Code(projectId));
    //     AddBullet(message, "Workspace ID", Code(workspaceId));
    //     AddBullet(message, "Webhook ID", Code(webhookId));
    // }
    
    static void AppendTaskLink(
        StringBuilder message,
        string? taskUrl)
    {
        if (string.IsNullOrWhiteSpace(taskUrl))
            return;
    
        message.AppendLine();
        message.AppendLine("---");
        message.AppendLine(
            $"[**Open this task in PMS →**]({taskUrl})");
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
    
    static string ProjectDisplay(
        ProjectInfo project,
        string? projectId)
    {
        // Project UUID and project identifier are not shown in the body.
        // The project UUID remains under Technical details.
        return EscapeMarkdown(project.Name);
    }
    
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
        CancellationToken cancellationToken)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("assignees", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return "Unassigned";
        }
    
        var assigneeUsers = array
            .EnumerateArray()
            .Select(assignee => new PmsUserRef(
                String(assignee, "id"),
                String(assignee, "email"),
                PersonName(assignee)))
            .ToArray();
    
        if (assigneeUsers.Length == 0)
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
            var id = String(assignee, "id");
    
            if (string.IsNullOrWhiteSpace(id))
                continue;
    
            var display = await mentionFormatter.FormatUserAsync(
                new PmsUserRef(
                    id,
                    String(assignee, "email"),
                    PersonName(assignee)),
                cancellationToken);
    
            result[id] = display;
        }
    
        return result;
    }
    
    static string Labels(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("labels", out var labels) ||
            labels.ValueKind != JsonValueKind.Array)
        {
            return "None";
        }
    
        var values = labels
            .EnumerateArray()
            .Select(label =>
            {
                if (label.ValueKind == JsonValueKind.String)
                {
                    var value = label.GetString();
    
                    // Do not show label UUIDs.
                    return IsReadableName(value)
                        ? EscapeMarkdown(value)
                        : null;
                }
    
                if (label.ValueKind != JsonValueKind.Object)
                    return null;
    
                var name = String(label, "name");
    
                return string.IsNullOrWhiteSpace(name)
                    ? null
                    : EscapeMarkdown(name);
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    
        return values.Length == 0
            ? "None"
            : string.Join(", ", values!);
    }
    
    static string Attachments(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("attachments", out var attachments) ||
            attachments.ValueKind != JsonValueKind.Array)
        {
            return "None";
        }
    
        var names = attachments
            .EnumerateArray()
            .Select(attachment =>
            {
                if (attachment.ValueKind != JsonValueKind.Object)
                    return null;
    
                return
                    String(attachment, "name")
                    ?? String(attachment, "asset_name")
                    ?? String(attachment, "file_name");
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => EscapeMarkdown(name))
            .ToArray();
    
        if (names.Length > 0)
            return string.Join(", ", names);
    
        // Report attachment presence without exposing UUIDs.
        return attachments.GetArrayLength() > 0
            ? CountDisplay(
                attachments.GetArrayLength(),
                "attachment",
                "attachments")
            : "None";
    }
    
    // static string Description(JsonElement data)
    // {
    //     var stripped = String(data, "description_stripped");
    
    //     if (!string.IsNullOrWhiteSpace(stripped))
    //         return stripped.Trim();
    
    //     return StripHtml(String(data, "description_html"));
    // }
    
    static string FriendlyFieldName(string? field)
    {
        return field?.ToLowerInvariant() switch
        {
            "state_id" => "Status",
            "assignee_ids" => "Assignees",
            // "description_html" => "Description",
            "priority" => "Priority",
            "name" => "Title",
            "start_date" => "Start date",
            "target_date" => "Target date",
            "label_ids" => "Labels",
            "point" => "Points",
            "estimate_point" => "Estimate points",
            "parent_id" => "Parent task",
            "is_draft" => "Draft status",
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
