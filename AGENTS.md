# Plane.Zulip.Bridge project agent

Keep this file synchronized whenever the application architecture, configuration,
deployment contract, webhook routes, or verification procedure changes.

## Purpose

This ASP.NET Core service receives Plane webhooks, enriches their data through
the Plane API, formats notifications, resolves Plane users to Zulip users by
email, and sends messages to a Zulip stream.

## Current architecture

- `Program.cs` loads environment configuration, creates named Plane and Zulip
  HTTP clients, initializes the configured Plane workspace, and maps endpoints.
- `Endpoints/PlaneWebhookEndpoints.cs` authenticates and handles Plane webhooks.
- `Plane/PlaneProjectCatalog.cs`, `Plane/PlaneUserDirectory.cs`, and
  `Plane/PlaneWorkItemClient.cs` use the Plane API as the only metadata source.
- `Plane/PlaneCommentFormatter.cs` converts Plane comment HTML and mentions.
- `Zulip/ZulipUserResolver.cs` loads Zulip users from the Zulip API.
- `Zulip/ZulipMessageSender.cs` delivers stream messages.
- `Notifications/NotificationDebouncer.cs` consolidates initial issue setup and
  independently debounces later description and assignee updates in memory.

There are no JSON mapping files or persistent project, user, mention, or issue
caches. Do not reintroduce them unless the user explicitly changes this design.

## Runtime configuration

Required variables:

```env
PLANE_API_URL=http://plane-api:8000
PLANE_API_KEY=plane_api_REPLACE_ME
PLANE_WORKSPACE_SLUG=team
PLANE_TASK_URL_TEMPLATE=https://pms.example.com/{PLANE_WORKSPACE_SLUG}/browse/{projectIdentifier}-{sequenceId}/

ZULIP_URL=https://zulip.example.com
ZULIP_BOT_EMAIL=plane-bot@zulip.example.com
ZULIP_BOT_API_KEY=REPLACE_ME
ZULIP_CHANNEL=0-pms

WEBHOOK_TOKEN=REPLACE_ME
```

Notification variables use the `PLANE_NOTIFY_*` prefix. New issue setup is
consolidated with `PLANE_ISSUE_CREATION_DEBOUNCE_SECONDS` (default 120 seconds).
Description and assignee debounce periods are configured with
`PLANE_DESCRIPTION_DEBOUNCE_SECONDS` and `PLANE_ASSIGNEE_DEBOUNCE_SECONDS`. The
assignee delay defaults to the configured description delay when omitted.

Do not restore legacy `PMS_*`, mapping-file, or cache-file variables.

## Workspace behavior

- One bridge instance handles one workspace selected by `PLANE_WORKSPACE_SLUG`.
- Configure Plane with `https://<bridge-host>/plane/<WEBHOOK_TOKEN>`.
- Task links replace `{PLANE_WORKSPACE_SLUG}`, `{projectIdentifier}`, and
  `{sequenceId}`.

## Security and logging

- Compare webhook tokens in constant time.
- Never log complete webhook bodies, comments, descriptions, or raw payload
  properties. Operational logs may include event/action, workspace, project,
  webhook ID, delivery status, and errors with bounded response bodies.
- Never commit real Plane or Zulip API keys or webhook tokens.

## Verification

After code changes, run:

```bash
dotnet test Plane.Zulip.Bridge.Tests/Plane.Zulip.Bridge.Tests.csproj --no-restore
dotnet publish -c Release -o /tmp/plane-zulip-bridge-publish --no-restore
git diff --check
```

The .NET test runner needs permission to open a local IPC socket in restricted
environments.

## Current state

- Plane project, user, creator, assignee, state, label, work-item, and attachment
  metadata comes from the Plane API.
- Zulip identity resolution comes from the Zulip users API.
- Comment and description HTML line breaks, including Plane's nonstandard
  `</br>` form, are preserved. Description formatting must prefer
  `description_html` over the flattened `description_stripped` value.
- Raw webhook/comment payload diagnostics have been removed.
- Plane and Zulip use separate dependency-injected HTTP clients with a 15-second
  timeout and five-minute pooled connection lifetime.
- The bridge intentionally supports one Plane workspace per deployment.
- Notification timestamps are displayed in the `Asia/Tehran` timezone. When
  Plane supplies a date without a time, the current Tehran time is used.
- Known Plane activity field aliases are mapped to their dedicated
  `PLANE_NOTIFY_*` flags and must not fall through to `PLANE_NOTIFY_OTHER_UPDATES`.
- Description and assignee updates are independently delivered after their
  configured quiet periods; a newer update of the same type replaces the pending
  notification for that issue.
- New issue notifications are delivered after a configurable quiet period; any
  issue update during that period refreshes the pending `Created` notification
  and restarts the timer so initial setup produces one Zulip message.
