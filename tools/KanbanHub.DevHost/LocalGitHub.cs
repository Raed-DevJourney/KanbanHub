using KanbanHub.Agents;
using KanbanHub.Data;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.GitHub;
using KanbanHub.GitHub;
using Microsoft.Data.SqlClient;

namespace KanbanHub.DevHost;

/// <summary>
/// GitHub on a development machine: an in-memory stand-in instead of the REST API, webhook deliveries processed
/// in this process, and a few doors to look at and poke the stand-in. The sync itself is the real one.
/// </summary>
internal static class LocalGitHub
{
    /// <summary>The principal the pretend GitHub App acts as here; linking a repository gives it the Member role.</summary>
    private static readonly Guid AppPrincipalId = new("0a000000-0000-4000-8000-0000000000aa");

    public static IServiceCollection AddLocalGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new GitHubAppOptions
        {
            // Signed deliveries to /api/github/webhook need the secret from configuration; without one nothing validates.
            WebhookSecret = configuration["GitHub:WebhookSecret"] ?? "",
            BotLogin = "kanbanhub[bot]",
            PrincipalId = AppPrincipalId,
            WebBaseUrl = new Uri("http://localhost:5173/")
        };

        services.AddSingleton(options);
        services.AddSingleton(options.ToSyncSettings());
        services.AddSingleton<WebhookVerifier>();
        services.AddSingleton<IGitHubEventParser, GitHubWebhookParser>();
        services.AddSingleton<InMemoryGitHub>();
        services.AddSingleton<IGitHubClient>(provider => provider.GetRequiredService<InMemoryGitHub>());
        services.AddSingleton<IGitHubInbox, InlineGitHubInbox>();
        return services.AddLocalAgents(configuration, options).AddConsumer<GitHubWriter>().AddConsumer<BackfillOnLink>();
    }

    /// <summary>
    /// Agents on a development machine: "Connect GitHub" approves at once and comes straight back, and the
    /// dispatcher remembers what it was asked instead of starting anything. <c>Agents:Enabled=false</c> shows
    /// the app with the feature switched off.
    /// </summary>
    private static IServiceCollection AddLocalAgents(this IServiceCollection services, IConfiguration configuration, GitHubAppOptions options)
    {
        bool isEnabled = configuration.GetValue("Agents:Enabled", defaultValue: true);

        services.AddSingleton(new AgentSettings(isEnabled));
        services.AddSingleton<IGitHubAuthorization>(provider =>
            new InMemoryGitHubAuthorization(new Uri(options.CallbackUrl!), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITokenProtector, LocalTokenProtector>();
        services.AddSingleton<InMemoryAgentDispatcher>();
        services.AddSingleton<IAgentDispatcher>(provider => provider.GetRequiredService<InMemoryAgentDispatcher>());
        return services;
    }

    /// <summary>
    /// The stand-in starts empty while the database remembers earlier runs: give it back the repositories and
    /// issues the database links, so their ids and numbers aren't handed out a second time.
    /// </summary>
    public static async Task RestoreGitHubAsync(this WebApplication app, CancellationToken cancellationToken)
    {
        var github = app.Services.GetRequiredService<InMemoryGitHub>();
        foreach (RepoLink repository in await app.Services.GetRequiredService<IRepoLinkStore>().ListAllAsync(cancellationToken))
            github.Remember(new RepositoryInfo(repository.Repo, repository.FullName));

        await using SqlConnection connection = await app.Services.GetRequiredService<ISqlConnectionFactory>().OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT InstallationId, RepositoryId, IssueNumber FROM kb.GitHubIssueLink;", connection);
        await using SqlDataReader links = await command.ExecuteReaderAsync(cancellationToken);
        while (await links.ReadAsync(cancellationToken))
            github.Known(new IssueRef(links.GetInt64(0), links.GetInt64(1), links.GetInt32(2)));
    }

    public static void MapLocalGitHub(this WebApplication app)
    {
        // What the pretend GitHub holds: the issues with their labels and comments, the check runs, and what agents were asked.
        app.MapGet("/dev/github", (InMemoryGitHub github, InMemoryAgentDispatcher agents) => Results.Json(new
        {
            agentAssignments = agents.Sent.Select(sent => sent.Assignment),
            issues = github.Issues.Select(issue => new
            {
                repositoryId = issue.Reference.RepositoryId,
                number = issue.Reference.Number,
                issue.Title,
                issue.Body,
                issue.Labels,
                state = issue.State.ToString(),
                comments = issue.Comments
            }),
            checkRuns = github.CheckRuns
        }));

        // Delivers a webhook payload as GitHub would, without a signature: POST /dev/github/issues, /issue_comment, ...
        app.MapPost("/dev/github/{eventName}", async (string eventName, HttpRequest request, IGitHubInbox inbox, CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(request.Body);
            var delivery = new RawDelivery(Guid.NewGuid().ToString(), eventName, await reader.ReadToEndAsync(cancellationToken));
            await inbox.EnqueueAsync(delivery, cancellationToken);
            return Results.Accepted();
        });
    }
}
