using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class PlaneProjectCatalogTests
{
    [Fact]
    public async Task LoadAsync_UsesPlaneApiProjects()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """
            [
              {
                "id": "project-id",
                "name": "Persian-Khab",
                "identifier": "PERSKHAB"
              }
            ]
            """);
        var catalog = await PlaneProjectCatalog.LoadAsync(
            new HttpClient(handler),
            "http://plane-api:8000/",
            "plane_api_secret",
            "team",
            NullLogger<PlaneProjectCatalog>.Instance,
            CancellationToken.None);

        var project = await catalog.ResolveAsync(
            "project-id",
            CancellationToken.None);

        Assert.Equal(1, catalog.Count);
        Assert.Equal("Persian-Khab", project.Name);
        Assert.Equal("PERSKHAB", project.Identifier);
        Assert.Equal("plane_api_secret", handler.ApiKey);
        Assert.Equal(
            "http://plane-api:8000/api/v1/workspaces/team/projects/?per_page=100",
            handler.RequestUris.Single());
    }

    [Fact]
    public async Task ResolveAsync_FetchesProjectCreatedAfterStartup()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.OK, """
            {
              "results": [
                { "id": "first", "name": "First", "identifier": "FIRST" }
              ],
              "next_page_results": false
            }
            """),
            (HttpStatusCode.OK, """
            {
              "id": "new-project",
              "name": "New Project",
              "identifier": "NEW"
            }
            """));
        var catalog = await PlaneProjectCatalog.LoadAsync(
            new HttpClient(handler),
            "http://plane-api:8000",
            "plane_api_secret",
            "team",
            NullLogger<PlaneProjectCatalog>.Instance,
            CancellationToken.None);

        var project = await catalog.ResolveAsync(
            "new-project",
            CancellationToken.None);

        Assert.Equal("New Project", project.Name);
        Assert.Equal("NEW", project.Identifier);
        Assert.EndsWith(
            "/api/v1/workspaces/team/projects/new-project/",
            handler.RequestUris.Last());
    }

    [Fact]
    public async Task LoadAsync_FailsWhenPlaneIsUnavailable()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PlaneProjectCatalog.LoadAsync(
                new HttpClient(new RecordingHandler(
                    HttpStatusCode.ServiceUnavailable,
                    "Plane unavailable")),
                "http://plane-api:8000",
                "plane_api_secret",
                "team",
                NullLogger<PlaneProjectCatalog>.Instance,
                CancellationToken.None));

        Assert.Contains("503", exception.Message);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public RecordingHandler(HttpStatusCode status, string body)
            : this((status, body))
        {
        }

        public RecordingHandler(
            params (HttpStatusCode Status, string Body)[] responses)
        {
            _responses = new Queue<(HttpStatusCode Status, string Body)>(responses);
        }

        public List<string> RequestUris { get; } = [];
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.AbsoluteUri);
            ApiKey = request.Headers.GetValues("X-API-Key").Single();

            var response = _responses.Dequeue();

            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(
                    response.Body,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
