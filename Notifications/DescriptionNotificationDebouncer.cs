using System.Collections.Concurrent;

internal sealed class DescriptionNotificationDebouncer : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource>
        _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ZulipMessageSender _sender;
    private readonly ILogger _logger;
    private readonly TimeSpan _delay;

    public DescriptionNotificationDebouncer(
        ZulipMessageSender sender,
        ILogger logger,
        TimeSpan delay)
    {
        _sender = sender;
        _logger = logger;
        _delay = delay;
    }

    public void Schedule(
        string issueId,
        string topic,
        string content)
    {
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? replaced = null;

        _pending.AddOrUpdate(
            issueId,
            cancellation,
            (_, existing) =>
            {
                replaced = existing;
                existing.Cancel();
                return cancellation;
            });

        replaced?.Dispose();

        _ = DeliverAfterDelayAsync(
            issueId,
            topic,
            content,
            cancellation);
    }

    public void Dispose()
    {
        foreach (var cancellation in _pending.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _pending.Clear();
    }

    private async Task DeliverAfterDelayAsync(
        string issueId,
        string topic,
        string content,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_delay, cancellation.Token);
            var result = await _sender.SendAsync(
                topic,
                content,
                cancellation.Token);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Delivered debounced description notification for issue {IssueId}",
                    issueId);
            }
            else
            {
                _logger.LogError(
                    "Could not deliver debounced description notification for issue {IssueId}: Status={Status}, Error={Error}, Body={Body}",
                    issueId,
                    result.StatusCode,
                    result.Error,
                    result.ResponseBody);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer edit replaced this pending notification.
        }
        finally
        {
            if (_pending.TryGetValue(issueId, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                _pending.TryRemove(issueId, out _);
                cancellation.Dispose();
            }
        }
    }
}
