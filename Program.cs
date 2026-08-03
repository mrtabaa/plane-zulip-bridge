// Load .env before creating the application so all configuration is
// available through Environment.GetEnvironmentVariable().
BridgeConfiguration.LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Plane", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
});
builder.Services.AddHttpClient("Zulip", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
});

var zulipUrl = BridgeConfiguration.Required("ZULIP_URL").TrimEnd('/');
var zulipEmail = BridgeConfiguration.Required("ZULIP_BOT_EMAIL");
var zulipApiKey = BridgeConfiguration.Required("ZULIP_BOT_API_KEY");
var zulipChannel = BridgeConfiguration.Required("ZULIP_CHANNEL");
var webhookToken = BridgeConfiguration.Required("WEBHOOK_TOKEN");
var planeApiUrl = BridgeConfiguration.Required("PLANE_API_URL");
var planeApiKey = BridgeConfiguration.Required("PLANE_API_KEY");
var planeWorkspaceSlug = BridgeConfiguration.Required("PLANE_WORKSPACE_SLUG");
var planeTaskUrlTemplate = BridgeConfiguration.Required(
    "PLANE_TASK_URL_TEMPLATE");

var app = builder.Build();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
var planeHttp = httpClientFactory.CreateClient("Plane");
var zulipHttp = httpClientFactory.CreateClient("Zulip");

var projects = await PlaneProjectCatalog.LoadAsync(
    planeHttp,
    planeApiUrl,
    planeApiKey,
    planeWorkspaceSlug,
    loggerFactory.CreateLogger<PlaneProjectCatalog>(),
    CancellationToken.None);
var planeUsers = await PlaneUserDirectory.LoadAsync(
    planeHttp,
    planeApiUrl,
    planeApiKey,
    planeWorkspaceSlug,
    loggerFactory.CreateLogger<PlaneUserDirectory>(),
    CancellationToken.None);
var planeWorkItems = new PlaneWorkItemClient(
    planeHttp,
    planeApiUrl,
    planeApiKey,
    planeWorkspaceSlug);
var notificationSettings = NotificationSettings.Load(app.Logger);

var zulipMessageSender = new ZulipMessageSender(
    zulipHttp,
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
    zulipHttp,
    zulipUrl,
    zulipEmail,
    zulipApiKey,
    loggerFactory.CreateLogger<ZulipUserResolver>());

var zulipMentionFormatter = new ZulipMentionFormatter(
    zulipUserResolver,
    loggerFactory.CreateLogger<ZulipMentionFormatter>());

var planeCommentFormatter = new PlaneCommentFormatter(
    zulipMentionFormatter,
    planeUsers,
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
    configuredPlaneUsers = planeUsers.Count,
    taskUrlTemplate = planeTaskUrlTemplate,
    notifications = notificationSettings
}));

app.MapPlaneWebhook(
    webhookToken,
    projects,
    planeUsers,
    planeWorkItems,
    planeTaskUrlTemplate,
    notificationSettings,
    pmsMentionExtractor,
    zulipMentionFormatter,
    planeCommentFormatter,
    zulipMessageSender,
    descriptionDebouncer);

app.Run();
