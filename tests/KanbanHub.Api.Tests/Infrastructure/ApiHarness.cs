using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using KanbanHub.Agents;
using KanbanHub.AI;
using KanbanHub.Api.Composition;
using KanbanHub.Api.Http;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.GitHub;
using KanbanHub.Domain.Notifications;
using KanbanHub.GitHub;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanHub.Api.Tests.Infrastructure;

/// <summary>
/// The API as the Functions host composes it, minus the host: the same service registrations,
/// pointed at the test database. Endpoint classes are created by the container and called directly.
/// </summary>
internal sealed class ApiHarness(KanbanDatabase database, bool agentsEnabled = true)
{
    private readonly ServiceProvider services = new ServiceCollection()
        .AddLogging()
        .AddKanbanHubApplication()
        .AddSingleton(database.Connections)
        .AddSingleton<RecordingGateway>()
        .AddSingleton<IRealtimeGateway>(provider => provider.GetRequiredService<RecordingGateway>())
        .AddSingleton<RecordingEmail>()
        .AddSingleton<IEmailSender>(provider => provider.GetRequiredService<RecordingEmail>())
        .AddSingleton<IBoardEventConsumer, BoardBroadcaster>()
        .AddSingleton<IBoardEventConsumer, NotificationGenerator>()
        .AddSingleton<IBoardEventPublisher, InProcessPublisher>()
        .AddSingleton(new GitHubAppOptions
        {
            WebhookSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            BotLogin = "kanbanhub[bot]",
            PrincipalId = Guid.NewGuid(),
            WebBaseUrl = new Uri("https://kanbanhub.test/")
        })
        .AddSingleton(provider => provider.GetRequiredService<GitHubAppOptions>().ToSyncSettings())
        .AddSingleton<WebhookVerifier>()
        .AddSingleton<IGitHubEventParser, GitHubWebhookParser>()
        .AddSingleton<InMemoryGitHub>()
        .AddSingleton<IGitHubClient>(provider => provider.GetRequiredService<InMemoryGitHub>())
        .AddSingleton<IGitHubInbox, InlineGitHubInbox>()
        .AddSingleton<IBoardEventConsumer>(provider => provider.GetRequiredService<GitHubWriter>())
        .AddSingleton<IBoardEventConsumer>(provider => provider.GetRequiredService<BackfillOnLink>())
        .AddSingleton(new AgentSettings(agentsEnabled))
        .AddSingleton(provider => new InMemoryGitHubAuthorization(
            new Uri(provider.GetRequiredService<GitHubAppOptions>().CallbackUrl!), provider.GetRequiredService<TimeProvider>()))
        .AddSingleton<IGitHubAuthorization>(provider => provider.GetRequiredService<InMemoryGitHubAuthorization>())
        .AddSingleton<ITokenProtector, LocalTokenProtector>()
        .AddSingleton<InMemoryAgentDispatcher>()
        .AddSingleton<IAgentDispatcher>(provider => provider.GetRequiredService<InMemoryAgentDispatcher>())
        .AddSingleton(new AiSettings(IsConfigured: true))
        .AddSingleton<ScriptedAiModel>()
        .AddSingleton<IAiModel>(provider => provider.GetRequiredService<ScriptedAiModel>())
        .AddSingleton<IAiJobInbox, InlineAiJobInbox>()
        .AddSingleton<WordHashEmbedder>()
        .AddSingleton<IEmbedder>(provider => provider.GetRequiredService<WordHashEmbedder>())
        .AddSingleton<IBoardEventConsumer>(provider => provider.GetRequiredService<DuplicateFinder>())
        .BuildServiceProvider();

    public T Endpoints<T>() => ActivatorUtilities.GetServiceOrCreateInstance<T>(services);

    public T Service<T>()
        where T : notnull => services.GetRequiredService<T>();
}

/// <summary>Stands in for SignalR Service: hands out a made-up connection and remembers what it was asked to push.</summary>
internal sealed class RecordingGateway : IRealtimeGateway
{
    public const string Url = "https://hub.test/board";

    public List<(string ConnectionId, Guid BoardId)> Joins { get; } = [];

    public List<BoardChanged> BoardChanges { get; } = [];

    public List<Guid> InboxPushes { get; } = [];

    public Task<RealtimeConnection> NegotiateAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(new RealtimeConnection(Url, AccessToken: userId.ToString()));

    public Task JoinBoardAsync(string connectionId, Guid boardId, CancellationToken cancellationToken)
    {
        Joins.Add((connectionId, boardId));
        return Task.CompletedTask;
    }

    public Task BoardChangedAsync(BoardChanged change, CancellationToken cancellationToken)
    {
        BoardChanges.Add(change);
        return Task.CompletedTask;
    }

    public Task InboxChangedAsync(Guid userId, CancellationToken cancellationToken)
    {
        InboxPushes.Add(userId);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingEmail : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Signed-in callers, as the auth middleware would hand them to a function.</summary>
internal static class TestUsers
{
    public static ClaimsPrincipal WithTenantRoles(params string[] tenantRoles) =>
        With([.. tenantRoles.Select(role => new Claim("roles", role))]);

    /// <summary>A user whose token names the address they sign in with.</summary>
    public static ClaimsPrincipal WithEmail(string email) => With(new Claim("preferred_username", email));

    private static ClaimsPrincipal With(params Claim[] extra)
    {
        Claim[] claims = [new("oid", Guid.NewGuid().ToString()), new("name", "Test user"), .. extra];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", nameType: "name", roleType: "roles"));
    }
}

/// <summary>Builds the HTTP request a function receives.</summary>
internal static class Requests
{
    public static HttpRequest From(ClaimsPrincipal user, object? body = null, string? ifMatch = null, Guid? idempotencyKey = null)
    {
        var http = new DefaultHttpContext { User = user };
        return Fill(http.Request, body, ifMatch, idempotencyKey);
    }

    /// <summary>A GET with a query string, for example <c>?take=2</c>.</summary>
    public static HttpRequest WithQuery(ClaimsPrincipal user, string query)
    {
        var http = new DefaultHttpContext { User = user };
        http.Request.QueryString = new QueryString(query);
        return http.Request;
    }

    private static HttpRequest Fill(HttpRequest request, object? body, string? ifMatch, Guid? idempotencyKey)
    {
        HttpContext http = request.HttpContext;
        if (body is not null)
            http.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body, JsonSerializerOptions.Web));
        if (ifMatch is not null)
            http.Request.Headers.IfMatch = $"\"{ifMatch}\"";
        if (idempotencyKey is not null)
            http.Request.Headers["Idempotency-Key"] = idempotencyKey.ToString();
        return http.Request;
    }
}

/// <summary>Reads what a function returned.</summary>
internal static class Results
{
    public static int StatusOf(this IActionResult result) => result switch
    {
        ObjectResult withBody => withBody.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult withoutBody => withoutBody.StatusCode,
        _ => throw new InvalidOperationException($"Unexpected result type {result.GetType().Name}.")
    };

    public static T ValueOf<T>(this IActionResult result) =>
        Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(result).Value, exactMatch: false);

    /// <summary>Asserts the result is a problem of the given status and type, and returns it.</summary>
    public static ProblemDetails ProblemOf(this IActionResult result, int status, string type)
    {
        var problem = result.ValueOf<ProblemDetails>();
        Assert.Equal(status, result.StatusOf());
        Assert.Equal($"https://kanbanhub.example/problems/{type}", problem.Type);
        Assert.Contains(Problems.ContentType, Assert.IsType<ObjectResult>(result).ContentTypes);
        return problem;
    }
}
