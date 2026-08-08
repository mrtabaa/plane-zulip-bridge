using System.Collections.Concurrent;

internal sealed class NotificationDebouncer : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource>
        _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ZulipMessageSender _sender;
    private readonly ILogger _logger;

    public NotificationDebouncer(
        ZulipMessageSender sender,
        ILogger logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public void Schedule(
        string notificationType,
        string issueId,
        string topic,
        string content,
        TimeSpan delay)
    {
        var key = $"{notificationType}:{issueId}";
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? replaced = null;

        _pending.AddOrUpdate(
            key,
            cancellation,
            (_, existing) =>
            {
                replaced = existing;
                existing.Cancel();
                return cancellation;
            });

        replaced?.Dispose();

        _ = DeliverAfterDelayAsync(
            key,
            notificationType,
            issueId,
            topic,
            content,
            delay,
            cancellation);
    }

    public bool TryReschedulePending(
        string notificationType,
        string issueId,
        string topic,
        string content,
        TimeSpan delay)
    {
        var key = $"{notificationType}:{issueId}";

        while (_pending.TryGetValue(key, out var existing))
        {
            var cancellation = new CancellationTokenSource();

            if (!_pending.TryUpdate(key, cancellation, existing))
            {
                cancellation.Dispose();
                continue;
            }

            existing.Cancel();
            existing.Dispose();

            _ = DeliverAfterDelayAsync(
                key,
                notificationType,
                issueId,
                topic,
                content,
                delay,
                cancellation);

            return true;
        }

        return false;
    }

    public bool IsPending(string notificationType, string issueId) =>
        _pending.ContainsKey($"{notificationType}:{issueId}");

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
        string key,
        string notificationType,
        string issueId,
        string topic,
        string content,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            var result = await _sender.SendAsync(
                topic,
                content,
                cancellation.Token);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Delivered debounced {NotificationType} notification for issue {IssueId}",
                    notificationType,
                    issueId);
            }
            else
            {
                _logger.LogError(
                    "Could not deliver debounced {NotificationType} notification for issue {IssueId}: Status={Status}, Error={Error}, Body={Body}",
                    notificationType,
                    issueId,
                    result.StatusCode,
                    result.Error,
                    result.ResponseBody);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer update of the same type replaced this pending notification.
        }
        finally
        {
            if (_pending.TryGetValue(key, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                _pending.TryRemove(key, out _);
                cancellation.Dispose();
            }
        }
    }
}
