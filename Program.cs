// Load .env before creating the application so all configuration is
// available through Environment.GetEnvironmentVariable().
BridgeConfiguration.LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var zulipUrl = BridgeConfiguration.Required("ZULIP_URL").TrimEnd('/');
var zulipEmail = BridgeConfiguration.Required("ZULIP_BOT_EMAIL");
var zulipApiKey = BridgeConfiguration.Required("ZULIP_BOT_API_KEY");
var zulipChannel = BridgeConfiguration.Required("ZULIP_CHANNEL");
var webhookToken = BridgeConfiguration.Required("WEBHOOK_TOKEN");
var planeApiUrl = BridgeConfiguration.Required("PLANE_API_URL");
var planeApiKey = BridgeConfiguration.Required("PLANE_API_KEY");
var planeWorkspaceSlug = BridgeConfiguration.Required("PLANE_WORKSPACE_SLUG");
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(15)
};

var planeMentionMap = PlaneMentionMapLoader.Load(
    BridgeConfiguration.LoadJsonConfiguration(
        "PLANE_MENTION_MAP_FILE",
        "PLANE_MENTION_MAP_JSON",
        "./config/plane-mention-map.json"),
    app.Logger);

var pmsTaskUrlTemplate =
    Environment.GetEnvironmentVariable("PMS_TASK_URL_TEMPLATE")
    ?? "https://pms.hallboard.ir/team/browse/" +
       "{projectIdentifier}-{sequenceId}/";

var projects = await PlaneProjectCatalog.LoadAsync(
    http,
    planeApiUrl,
    planeApiKey,
    planeWorkspaceSlug,
    loggerFactory.CreateLogger<PlaneProjectCatalog>(),
    CancellationToken.None);
var notificationSettings = NotificationSettings.Load(app.Logger);

/*
 * Comment webhook payloads contain the issue UUID but not sequence_id.
 *
 * Whenever an issue create/update webhook is received, its UUID, sequence
 * number, title, and project are cached. A later comment webhook can then
 * use the same Zulip topic and task URL.
 *
 * This cache is in memory and is cleared when the container restarts.
 */
var issueCache = IssueCacheStore.Load(
    Environment.GetEnvironmentVariable("PMS_ISSUE_CACHE_FILE")
    ?? "./data/pms-issues.json",
    app.Logger);

var zulipMessageSender = new ZulipMessageSender(
    http,
    zulipUrl,
    zulipEmail,
    zulipApiKey,
    zulipChannel);
var descriptionDebouncer = new DescriptionNotificationDebouncer(
    zulipMessageSender,
    app.Logger,
    TimeSpan.FromSeconds(
        notificationSettings.DescriptionDebounceSeconds));

app.Lifetime.ApplicationStopping.Register(descriptionDebouncer.Dispose);

var zulipUserResolver = new ZulipUserResolver(
    http,
    zulipUrl,
    zulipEmail,
    zulipApiKey,
    loggerFactory.CreateLogger<ZulipUserResolver>());

var zulipMentionFormatter = new ZulipMentionFormatter(
    zulipUserResolver,
    loggerFactory.CreateLogger<ZulipMentionFormatter>());

var planeCommentFormatter = new PlaneCommentFormatter(
    zulipMentionFormatter,
    planeMentionMap,
    loggerFactory.CreateLogger<PlaneCommentFormatter>());

var pmsMentionExtractor = new PmsMentionExtractor();

try
{
    await zulipUserResolver.RefreshAsync(CancellationToken.None);
}
catch (Exception exception)
{
    app.Logger.LogWarning(
        exception,
        "Initial Zulip user directory refresh failed; webhooks will use plain user names until refresh succeeds");
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "pms-zulip-bridge",
    configuredProjects = projects.Count,
    cachedIssues = issueCache.Count,
    taskUrlTemplate = pmsTaskUrlTemplate,
    notifications = notificationSettings
}));

app.MapPlaneWebhook(
    webhookToken,
    projects,
    pmsTaskUrlTemplate,
    issueCache,
    notificationSettings,
    pmsMentionExtractor,
    zulipMentionFormatter,
    planeCommentFormatter,
    zulipMessageSender,
    descriptionDebouncer);

app.Run();
