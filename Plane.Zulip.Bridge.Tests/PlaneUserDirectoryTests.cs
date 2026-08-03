using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class PlaneUserDirectoryTests
{
    [Fact]
    public async Task LoadAsync_MapsPlaneUserIdsToEmails()
    {
        var handler = new RecordingHandler("""
        [
          {
            "id": "plane-user-id",
            "email": "USER@Example.com",
            "display_name": "User"
          }
        ]
        """);
        var directory = await PlaneUserDirectory.LoadAsync(
            new HttpClient(handler),
            "http://plane-api:8000/",
            "plane_api_secret",
            "team",
            NullLogger<PlaneUserDirectory>.Instance,
            CancellationToken.None);

        var user = await directory.FindUserAsync(
            "plane-user-id",
            CancellationToken.None);

        Assert.Equal("user@example.com", user?.Email);
        Assert.Equal("User", user?.DisplayName);
        Assert.Equal(1, directory.Count);
        Assert.Equal("plane_api_secret", handler.ApiKey);
        Assert.Equal(
            "http://plane-api:8000/api/v1/workspaces/team/members/?per_page=100",
            handler.RequestUri);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public RecordingHandler(string body)
        {
            _body = body;
        }

        public string? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.AbsoluteUri;
            ApiKey = request.Headers.GetValues("X-API-Key").Single();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _body,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
