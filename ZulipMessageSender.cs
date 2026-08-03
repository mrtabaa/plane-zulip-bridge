using System.Net.Http.Headers;
using System.Text;

internal sealed class ZulipMessageSender
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _email;
    private readonly string _apiKey;
    private readonly string _channel;

    public ZulipMessageSender(
        HttpClient http,
        string url,
        string email,
        string apiKey,
        string channel)
    {
        _http = http;
        _url = url;
        _email = email;
        _apiKey = apiKey;
        _channel = channel;
    }

    public async Task<ZulipDeliveryResult> SendAsync(
        string topic,
        string content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_url}/api/v1/messages");
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_email}:{_apiKey}"));

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);
        request.Content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["type"] = "stream",
                ["to"] = _channel,
                ["topic"] = topic,
                ["content"] = content
            });

        try
        {
            using var response = await _http.SendAsync(
                request,
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(
                cancellationToken);

            return new ZulipDeliveryResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                body,
                null);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new ZulipDeliveryResult(false, null, null, "timeout");
        }
        catch (Exception exception)
        {
            return new ZulipDeliveryResult(
                false,
                null,
                null,
                exception.Message);
        }
    }
}

internal sealed record ZulipDeliveryResult(
    bool Success,
    int? StatusCode,
    string? ResponseBody,
    string? Error);
