using System.Net;
using System.Text;
using Xunit;

public sealed class PlaneWorkItemClientTests
{
    [Fact]
    public async Task GetsLiveWorkItemAndCommentAttachmentMetadata()
    {
        var handler = new RouteHandler(new Dictionary<string, string>
        {
            ["/api/v1/workspaces/team/projects/project-id/work-items/issue-id/"] = """
            {
              "id": "issue-id",
              "name": "Live title",
              "sequence_id": 527,
              "project": "project-id",
              "created_by": "creator-id"
            }
            """,
            ["/api/v1/workspaces/team/projects/project-id/work-items/issue-id/attachments/"] = """
            [
              {
                "comment": "comment-id",
                "attributes": { "name": "design.png" }
              },
              {
                "comment": "another-comment",
                "attributes": { "name": "unrelated.pdf" }
              }
            ]
            """
        });
        var client = Client(handler);

        var item = await client.GetAsync(
            "project-id",
            "issue-id",
            CancellationToken.None);
        var attachments = await client.GetAttachmentNamesAsync(
            "project-id",
            "issue-id",
            "comment-id",
            CancellationToken.None);

        Assert.Equal("Live title", item.Name);
        Assert.Equal(527, item.SequenceId);
        Assert.Equal("creator-id", item.CreatorId);
        Assert.Equal(new[] { "design.png" }, attachments);
    }

    [Fact]
    public async Task ResolvesStateAndLabelIdsFromPlaneApi()
    {
        var handler = new RouteHandler(new Dictionary<string, string>
        {
            ["/api/v1/workspaces/team/projects/project-id/states/"] =
                """[{ "id": "state-id", "name": "In Progress" }]""",
            ["/api/v1/workspaces/team/projects/project-id/labels/"] =
                """[{ "id": "label-id", "name": "Backend" }]"""
        });
        var client = Client(handler);

        var state = await client.FindStateNameAsync(
            "project-id",
            "state-id",
            CancellationToken.None);
        var labels = await client.FindLabelNamesAsync(
            "project-id",
            new[] { "label-id" },
            CancellationToken.None);

        Assert.Equal("In Progress", state);
        Assert.Equal(new[] { "Backend" }, labels);
    }

    private static PlaneWorkItemClient Client(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            "http://plane-api:8000",
            "plane_api_secret",
            "team");

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _responses;

        public RouteHandler(IReadOnlyDictionary<string, string> responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (!_responses.TryGetValue(path, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found")
                });
            }

            Assert.Equal("plane_api_secret", request.Headers.GetValues("X-API-Key").Single());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
