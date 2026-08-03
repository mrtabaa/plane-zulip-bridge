using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class ZulipMentionTests
{
    [Fact]
    public void NormalizeEmail_TrimsAndLowercases()
    {
        Assert.Equal(
            "jane@example.com",
            ZulipUserResolver.NormalizeEmail("  Jane@Example.COM  "));
    }

    [Fact]
    public async Task AssigneeEmail_BecomesZulipMention()
    {
        var formatter = Formatter(
            ("jane@example.com", new ZulipUser(101, "jane@example.com", "Jane Smith")));

        var result = await formatter.FormatDistinctUsersAsync(
            new[]
            {
                new PmsUserRef("pms-1", " Jane@Example.com ", "Jane PMS")
            },
            CancellationToken.None);

        Assert.Equal("@**Jane Smith**", Assert.Single(result));
    }

    [Fact]
    public async Task ExplicitUserMap_BecomesZulipMentionWithoutDirectoryMatch()
    {
        var userMap = ZulipMentionFormatter.LoadUserMap(
            "{\"faranak@hallboard.ir\":\"Faranak - Scrum Master\"}",
            NullLogger.Instance);
        var formatter = new ZulipMentionFormatter(
            new FakeResolver(),
            NullLogger<ZulipMentionFormatter>.Instance,
            userMap);

        var result = await formatter.FormatUserAsync(
            new PmsUserRef("pms-1", "FARANAK@hallboard.ir", "faranak"),
            CancellationToken.None);

        Assert.Equal("@**Faranak - Scrum Master**", result);
    }

    [Fact]
    public async Task PlaneCommentFormatter_PreservesHtmlLineBreaks()
    {
        var formatter = new PlaneCommentFormatter(
            new ZulipMentionFormatter(
                new FakeResolver(),
                NullLogger<ZulipMentionFormatter>.Instance),
            new Dictionary<string, string>(),
            NullLogger<PlaneCommentFormatter>.Instance);

        var result = await formatter.FormatAsync(
            "<p>first line</p><br>second line</br>third line<div>fourth line</div>",
            CancellationToken.None);

        Assert.Equal(
            "first line\n\nsecond line\nthird line\nfourth line",
            result);
    }

    [Fact]
    public async Task ActorEmail_BecomesZulipMention()
    {
        var formatter = Formatter(
            ("john@example.com", new ZulipUser(102, "john@example.com", "John Smith")));

        var result = await formatter.FormatUserAsync(
            new PmsUserRef("actor-1", "john@example.com", "John Pms"),
            CancellationToken.None);

        Assert.Equal("@**John Smith**", result);
    }

    [Fact]
    public async Task CommentWithOneMentionedEmail_GeneratesRealZulipMention()
    {
        var extractor = new PmsMentionExtractor();
        var formatter = Formatter(
            ("jane@example.com", new ZulipUser(101, "jane@example.com", "Jane Doe")));

        var users = extractor.MentionEmailsFromText(
            "Please review this @jane@example.com");

        var result = await formatter.FormatDistinctUsersAsync(
            users,
            CancellationToken.None);

        Assert.Equal("@**Jane Doe**", Assert.Single(result));
    }

    [Fact]
    public async Task CommentWithMultipleMentionedEmails_GeneratesAllMentions()
    {
        var extractor = new PmsMentionExtractor();
        var formatter = Formatter(
            ("jane@example.com", new ZulipUser(101, "jane@example.com", "Jane Doe")),
            ("bob@example.com", new ZulipUser(102, "bob@example.com", "Bob Wilson")));

        var users = extractor.MentionEmailsFromText(
            "Ask jane@example.com and @bob@example.com to verify.");

        var result = await formatter.FormatDistinctUsersAsync(
            users,
            CancellationToken.None);

        Assert.Equal(
            new[] { "@**Jane Doe**", "@**Bob Wilson**" },
            result);
    }

    [Fact]
    public void StructuredMentionObjects_ArePreferredOverCommentText()
    {
        using var document = Json("""
        {
          "mentions": [
            { "email": "jane@example.com", "display_name": "Jane" }
          ]
        }
        """);

        var extractor = new PmsMentionExtractor();

        var users = extractor.CommentUsers(
            document.RootElement,
            default,
            new PmsUserRef("actor", "john@example.com", "John"),
            "Please ask @bob@example.com");

        Assert.Contains(users, user => user.Email == "jane@example.com");
        Assert.DoesNotContain(users, user => user.Email == "bob@example.com");
    }

    [Fact]
    public async Task UnknownEmail_RendersPlainTextWithoutFalseMention()
    {
        var formatter = Formatter();

        var result = await formatter.FormatUserAsync(
            new PmsUserRef("pms-1", "unknown@example.com", "Unknown User"),
            CancellationToken.None);

        Assert.Equal("Unknown User (unknown@example.com)", result);
        Assert.DoesNotContain("@**", result);
    }

    [Theory]
    [InlineData("@**all** please check")]
    [InlineData("@**everyone** please check")]
    public void BroadcastMentions_AreNeutralizedInCommentText(string comment)
    {
        var result = PmsMentionExtractor.NeutralizeBroadcastMentions(comment);

        Assert.DoesNotContain("@**all**", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@**everyone**", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@\\*\\*", result);
    }

    [Fact]
    public async Task DuplicateUsers_AreMentionedOnlyOnce()
    {
        var formatter = Formatter(
            ("jane@example.com", new ZulipUser(101, "jane@example.com", "Jane Doe")));

        var result = await formatter.FormatDistinctUsersAsync(
            new[]
            {
                new PmsUserRef("one", "jane@example.com", "Jane"),
                new PmsUserRef("two", " JANE@example.com ", "Jane Again")
            },
            CancellationToken.None);

        Assert.Equal("@**Jane Doe**", Assert.Single(result));
    }

    [Fact]
    public void MissingAssigneesActorAndMentions_DoNotCrashExtractor()
    {
        using var document = Json("""{ "name": "Task with no people" }""");
        var extractor = new PmsMentionExtractor();

        var users = extractor.CommentUsers(
            document.RootElement,
            document.RootElement,
            new PmsUserRef(null, null, null),
            "");

        Assert.Empty(users);
    }

    [Fact]
    public async Task ZulipUsersApiFailure_DoesNotCrashLookup()
    {
        var http = new HttpClient(new StaticHandler(
            HttpStatusCode.BadGateway,
            """{ "msg": "bad gateway" }"""));
        var resolver = new ZulipUserResolver(
            http,
            "https://zulip.example.com",
            "bot@example.com",
            "secret",
            NullLogger<ZulipUserResolver>.Instance);

        var user = await resolver.FindByEmailAsync(
            "jane@example.com",
            CancellationToken.None);

        Assert.Null(user);
    }

    [Fact]
    public void NonCommentEvent_DoesNotParseArbitraryAtWordsAsMentions()
    {
        using var data = Json("""{ "name": "Ping @backend about this" }""");
        using var activity = Json("""{ "field": "name" }""");
        var extractor = new PmsMentionExtractor();

        var users = extractor.IssueUpdatedUsers(
            data.RootElement,
            activity.RootElement,
            new PmsUserRef(null, null, null));

        Assert.Empty(users);
    }

    [Fact]
    public void IssueCreator_PrefersCreatedByDetail()
    {
        using var data = Json("""
        {
          "created_by": "c41d31d6-6450-46bc-8fd6-f1bb1b9050d4",
          "created_by_detail": {
            "id": "c41d31d6-6450-46bc-8fd6-f1bb1b9050d4",
            "email": "creator@example.com",
            "display_name": "Original Creator"
          }
        }
        """);

        var creator = PmsMentionExtractor.IssueCreator(data.RootElement);

        Assert.NotNull(creator);
        Assert.Equal("creator@example.com", creator.Email);
        Assert.Equal("Original Creator", creator.DisplayName);
    }

    [Fact]
    public void IssueCreator_MatchesCreatedByIdToDetailedAssignee()
    {
        using var data = Json("""
        {
          "created_by": "creator-id",
          "assignees": [
            {
              "id": "creator-id",
              "email": "creator@example.com",
              "display_name": "Original Creator"
            }
          ]
        }
        """);

        var creator = PmsMentionExtractor.IssueCreator(data.RootElement);

        Assert.NotNull(creator);
        Assert.Equal("Original Creator", creator.DisplayName);
    }

    [Fact]
    public async Task Resolver_UsesCaseInsensitiveTrimmedEmailLookup()
    {
        var http = new HttpClient(new StaticHandler(
            HttpStatusCode.OK,
            """
            {
              "members": [
                {
                  "user_id": 42,
                  "email": "Jane@Example.com",
                  "full_name": "Jane Smith",
                  "status": "active"
                }
              ]
            }
            """));
        var resolver = new ZulipUserResolver(
            http,
            "https://zulip.example.com",
            "bot@example.com",
            "secret",
            NullLogger<ZulipUserResolver>.Instance);

        var user = await resolver.FindByEmailAsync(
            " jane@example.COM ",
            CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal("Jane Smith", user.FullName);
    }

    [Fact]
    public async Task Resolver_UsesDeliveryEmailForPmsEmailMatching()
    {
        var http = new HttpClient(new StaticHandler(
            HttpStatusCode.OK,
            """
            {
              "members": [
                {
                  "user_id": 43,
                  "email": "user43@zulip-api.invalid",
                  "delivery_email": "faranak@hallboard.ir",
                  "full_name": "Faranak - Scrum Master",
                  "status": "active"
                }
              ]
            }
            """));
        var resolver = new ZulipUserResolver(
            http,
            "https://zulip.example.com",
            "bot@example.com",
            "secret",
            NullLogger<ZulipUserResolver>.Instance);

        var user = await resolver.FindByEmailAsync(
            " FARANAK@hallboard.ir ",
            CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal("Faranak - Scrum Master", user.FullName);
    }

    [Fact]
    public async Task Resolver_FallsBackToApiEmailWhenDeliveryEmailIsEmpty()
    {
        var http = new HttpClient(new StaticHandler(
            HttpStatusCode.OK,
            """
            {
              "members": [
                {
                  "user_id": 44,
                  "email": "admin@hallboard.ir",
                  "delivery_email": "",
                  "full_name": "Admin",
                  "is_active": true
                }
              ]
            }
            """));
        var resolver = new ZulipUserResolver(
            http,
            "https://zulip.example.com",
            "bot@example.com",
            "secret",
            NullLogger<ZulipUserResolver>.Instance);

        var user = await resolver.FindByEmailAsync(
            "admin@hallboard.ir",
            CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal("Admin", user.FullName);
    }

    private static ZulipMentionFormatter Formatter(
        params (string Email, ZulipUser User)[] users)
    {
        return new ZulipMentionFormatter(
            new StaticResolver(users.ToDictionary(
                user => user.Email,
                user => user.User,
                StringComparer.OrdinalIgnoreCase)),
            NullLogger<ZulipMentionFormatter>.Instance);
    }

    private static JsonDocument Json(string json) =>
        JsonDocument.Parse(json);

    private sealed class FakeResolver : IZulipUserResolver
    {
        public Task RefreshAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask<ZulipUser?> FindByEmailAsync(
            string? email,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ZulipUser?>(null);
    }

    private sealed class StaticResolver : IZulipUserResolver
    {
        private readonly IReadOnlyDictionary<string, ZulipUser> _users;

        public StaticResolver(IReadOnlyDictionary<string, ZulipUser> users)
        {
            _users = users;
        }

        public Task RefreshAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask<ZulipUser?> FindByEmailAsync(
            string? email,
            CancellationToken cancellationToken)
        {
            var normalizedEmail = ZulipUserResolver.NormalizeEmail(email);

            return ValueTask.FromResult(
                normalizedEmail is not null &&
                _users.TryGetValue(normalizedEmail, out var user)
                    ? user
                    : null);
        }
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StaticHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(
                    _body,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
