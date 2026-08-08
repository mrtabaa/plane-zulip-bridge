using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class NotificationDebouncerTests
{
    [Fact]
    public async Task NewerAssigneeUpdate_ReplacesPendingAssigneeNotification()
    {
        var handler = new RecordingHandler(expectedRequests: 1);
        using var debouncer = CreateDebouncer(handler);

        debouncer.Schedule(
            "assignee",
            "issue-id",
            "topic",
            "first selection",
            TimeSpan.FromMilliseconds(100));
        debouncer.Schedule(
            "assignee",
            "issue-id",
            "topic",
            "final selection",
            TimeSpan.FromMilliseconds(20));

        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(150);

        var request = Assert.Single(handler.RequestBodies);
        Assert.Contains("final+selection", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescriptionAssigneeAndLabelUpdates_AreDebouncedIndependently()
    {
        var handler = new RecordingHandler(expectedRequests: 3);
        using var debouncer = CreateDebouncer(handler);

        debouncer.Schedule(
            "description",
            "issue-id",
            "topic",
            "description update",
            TimeSpan.FromMilliseconds(20));
        debouncer.Schedule(
            "assignee",
            "issue-id",
            "topic",
            "assignee update",
            TimeSpan.FromMilliseconds(20));
        debouncer.Schedule(
            "label",
            "issue-id",
            "topic",
            "label update",
            TimeSpan.FromMilliseconds(20));

        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task PendingIssueCreation_IsReplacedByConsolidatedSnapshot()
    {
        var handler = new RecordingHandler(expectedRequests: 1);
        using var debouncer = CreateDebouncer(handler);

        debouncer.Schedule(
            "issue-created",
            "issue-id",
            "topic",
            "initial snapshot",
            TimeSpan.FromMilliseconds(100));

        Assert.True(debouncer.TryReschedulePending(
            "issue-created",
            "issue-id",
            "topic",
            "configured snapshot",
            TimeSpan.FromMilliseconds(20)));

        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(150);

        var request = Assert.Single(handler.RequestBodies);
        Assert.Contains("configured+snapshot", request, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingIssueCreation_IsNotScheduledByAnUpdate()
    {
        using var debouncer = CreateDebouncer(
            new RecordingHandler(expectedRequests: 1));

        Assert.False(debouncer.TryReschedulePending(
            "issue-created",
            "issue-id",
            "topic",
            "update",
            TimeSpan.FromMilliseconds(20)));
    }

    private static NotificationDebouncer CreateDebouncer(
        HttpMessageHandler handler)
    {
        var sender = new ZulipMessageSender(
            new HttpClient(handler),
            "https://zulip.example.com",
            "bot@example.com",
            "secret",
            "plane");

        return new NotificationDebouncer(
            sender,
            NullLogger.Instance);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly int _expectedRequests;
        private int _requestCount;

        public RecordingHandler(int expectedRequests)
        {
            _expectedRequests = expectedRequests;
        }

        public ConcurrentQueue<string> RequestBodies { get; } = new();
        public TaskCompletionSource Completed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Enqueue(
                await request.Content!.ReadAsStringAsync(cancellationToken));

            if (Interlocked.Increment(ref _requestCount) == _expectedRequests)
                Completed.TrySetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":\"success\"}")
            };
        }
    }
}
