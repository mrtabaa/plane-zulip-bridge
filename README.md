# Plane.Zulip.Bridge

Plane.Zulip.Bridge is a small ASP.NET Core service that turns Plane issue and
comment webhooks into formatted messages in a Zulip stream. It enriches webhook
payloads with data from the Plane API, resolves Plane users to Zulip users by
email, preserves useful HTML formatting, and creates links back to each Plane
work item.

The bridge is intentionally stateless and handles one Plane workspace per
deployment. It does not use a database, persistent caches, or JSON mapping
files.

## Features

- Sends notifications for new issues, comments, and issue updates.
- Supports status, assignee, priority, title, date, label, points, draft,
  description, and other update notifications independently.
- Resolves creators, assignees, states, labels, work items, and attachment names
  through the Plane API.
- Converts Plane user mentions to Zulip mentions when the users have matching
  email addresses.
- Preserves comment and description line breaks, including Plane's nonstandard
  `</br>` HTML.
- Debounces description and assignee updates independently so rapid edits do not
  flood Zulip. The newest pending update of each type wins for an issue.
- Exposes a health endpoint with the loaded project/user counts and active
  notification settings.
- Uses constant-time webhook-token comparison and avoids logging webhook bodies,
  descriptions, and comments.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/) for local builds, or Docker for a
  container deployment.
- A Plane API key that can read the configured workspace, its projects, members,
  work items, states, labels, and attachments.
- A Zulip bot with permission to read the user directory and post to the target
  stream.
- A public HTTPS URL that Plane can reach for webhook delivery.

## Configuration

The application reads configuration from environment variables. For local use,
it also loads a `.env` file from the working directory or application directory.
Existing environment variables take precedence over values in `.env`.

Create `.env` without committing real credentials:

```env
PLANE_API_URL=http://plane-api:8000
PLANE_API_KEY=plane_api_REPLACE_ME
PLANE_WORKSPACE_SLUG=team
PLANE_TASK_URL_TEMPLATE=https://pms.example.com/{PLANE_WORKSPACE_SLUG}/browse/{projectIdentifier}-{sequenceId}/

ZULIP_URL=https://zulip.example.com
ZULIP_BOT_EMAIL=plane-bot@zulip.example.com
ZULIP_BOT_API_KEY=REPLACE_ME
ZULIP_CHANNEL=0-pms

WEBHOOK_TOKEN=REPLACE_WITH_A_LONG_RANDOM_VALUE
```

All variables above are required. `PLANE_TASK_URL_TEMPLATE` should contain the
`{projectIdentifier}` and `{sequenceId}` placeholders used to build work-item
links. It may also contain `{PLANE_WORKSPACE_SLUG}`, which is replaced with the
configured workspace slug. `PLANE_API_URL` should point to the Plane API origin;
`ZULIP_CHANNEL` is the destination stream name.

### Notification settings

Notification flags accept `true` or `false`. Invalid values are ignored and the
documented default is used.

| Variable | Default | Controls |
| --- | ---: | --- |
| `PLANE_NOTIFY_ISSUE_CREATED` | `true` | New issues |
| `PLANE_NOTIFY_COMMENT` | `true` | New issue comments |
| `PLANE_NOTIFY_STATUS` | `true` | Status changes |
| `PLANE_NOTIFY_ASSIGNEE` | `true` | Assignee changes |
| `PLANE_NOTIFY_PRIORITY` | `true` | Priority changes |
| `PLANE_NOTIFY_TITLE` | `true` | Title changes |
| `PLANE_NOTIFY_DATE` | `true` | Start- and target-date changes |
| `PLANE_NOTIFY_LABEL` | `true` | Label changes |
| `PLANE_NOTIFY_POINTS` | `true` | Estimate/point changes |
| `PLANE_NOTIFY_DRAFT` | `true` | Draft-state changes |
| `PLANE_NOTIFY_DESCRIPTION` | `false` | Description changes |
| `PLANE_NOTIFY_OTHER_UPDATES` | `true` | Update fields not listed above |
| `PLANE_DESCRIPTION_DEBOUNCE_SECONDS` | `45` | Description quiet period |
| `PLANE_ASSIGNEE_DEBOUNCE_SECONDS` | description delay | Assignee quiet period |

Debounce values must be positive integers. Pending debounce state is held only
in memory and is lost when the process stops.

## Run locally

```bash
dotnet restore
dotnet run
```

The development launch profile listens on the URLs defined in
`Properties/launchSettings.json`. In a container, the service listens on port
`8080`.

Check the service:

```bash
curl http://localhost:5243/health
```

The application loads Plane projects and workspace members during startup. A
failure to access that metadata prevents startup, while an initial Zulip user
directory failure is non-fatal and mentions fall back to plain names until a
later refresh succeeds.

## Configure the Plane webhook

Create a webhook for the configured workspace and use this URL:

```text
https://<bridge-host>/plane/<WEBHOOK_TOKEN>
```

Enable issue and issue-comment events in Plane. The bridge handles:

- `issue` / `created`
- `issue` / `updated`
- `issue_comment` / `created`

Unsupported event/action pairs return a successful response marked as ignored.
An invalid token returns `401`, invalid JSON returns `400`, and immediate Zulip
delivery failures return `502`.

Keep `WEBHOOK_TOKEN` secret: it is part of the webhook URL and is the endpoint's
authentication credential.

## Docker

Version `5.8.0` is published in the
[Hallboard container registry](https://git.hallboard.ir/team/-/packages/container/prod_plane-zulip-bridge/5.8.0):

```bash
docker pull git.hallboard.ir/team/prod_plane-zulip-bridge:5.8.0
```

Run the published image with the environment file:

```bash
docker run --rm \
  --env-file .env \
  -p 8080:8080 \
  git.hallboard.ir/team/prod_plane-zulip-bridge:5.8.0
```

Alternatively, build the image locally:

```bash
docker build -t plane-zulip-bridge:local .
```

Run it with the environment file:

```bash
docker run --rm \
  --env-file .env \
  -p 8080:8080 \
  plane-zulip-bridge:local
```

The included `Dockerfile` currently uses Hallboard's mirrored .NET SDK and
ASP.NET runtime images. Replace those `FROM` values with the corresponding
public Microsoft images if that registry is unavailable in your environment.
See [build.md](build.md) for the repository's tagged build-and-push example.

## How it works

1. The service validates the token in `POST /plane/{token}` and parses the Plane
   webhook.
2. It enriches the event with project, member, work-item, state, label, and
   attachment metadata from Plane.
3. It matches Plane and Zulip identities by normalized email and formats the
   message and topic.
4. It sends the message to the configured Zulip stream, immediately or after
   the applicable in-memory debounce period.

Project, member, Zulip-user, state, and label metadata is refreshed in memory as
needed. The Plane and Zulip HTTP clients use a 15-second timeout and a five-minute
pooled connection lifetime.

## Development and verification

Run the test suite and release build after changing the code:

```bash
dotnet test Plane.Zulip.Bridge.Tests/Plane.Zulip.Bridge.Tests.csproj --no-restore
dotnet publish -c Release -o /tmp/plane-zulip-bridge-publish --no-restore
git diff --check
```

In restricted environments, the .NET test runner may need permission to open a
local IPC socket.

The main components are:

- `Program.cs` — configuration, HTTP clients, startup loading, and routes.
- `Endpoints/PlaneWebhookEndpoints.cs` — webhook authentication, event handling,
  message construction, and delivery responses.
- `Plane/` — Plane API metadata clients and Plane content formatting.
- `Zulip/` — user resolution, mention formatting, and stream delivery.
- `Notifications/NotificationDebouncer.cs` — independent in-memory description
  and assignee debounce queues.
- `Plane.Zulip.Bridge.Tests/` — unit tests.
