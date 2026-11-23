using KanbanHub.Data.Agents;
using KanbanHub.Data.Ai;
using KanbanHub.Data.Boards;
using KanbanHub.Data.Cards;
using KanbanHub.Data.Events;
using KanbanHub.Data.GitHub;
using KanbanHub.Data.Notifications;
using KanbanHub.Data.Security;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;
using KanbanHub.Domain.Notifications;
using KanbanHub.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanHub.Data;

public static class CoreServices
{
    /// <summary>
    /// The use cases and their SQL storage: what every host shares, whichever way requests and events reach it.
    /// The host adds what depends on where it runs: the connection factory, the authorizer it wants in front of
    /// <see cref="SqlBoardAuthorizer"/>, and the adapters for real-time, events, email and GitHub.
    /// </summary>
    public static IServiceCollection AddKanbanHubCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPrincipalResolver, ClaimsPrincipalResolver>();
        services.AddSingleton<SqlBoardAuthorizer>();

        return services.AddBoardsAndCards().AddEventsAndNotifications().AddGitHubSync().AddAgents().AddAi();
    }

    /// <summary>
    /// The AI features (section 11). The host adds the model behind them and says whether there is one:
    /// <c>AddKanbanHubAi</c>, or a stand-in; and where background jobs wait (<see cref="IAiJobInbox"/>).
    /// </summary>
    private static IServiceCollection AddAi(this IServiceCollection services)
    {
        services.AddSingleton<IAiPolicyStore, AiPolicyStore>();
        services.AddSingleton<IInsightStore, InsightStore>();
        services.AddSingleton<IFlowFacts, FlowFactQueries>();
        services.AddSingleton<IAgingStore, AgingStore>();
        services.AddSingleton<IDuplicateStore, DuplicateStore>();

        services.AddSingleton<AiRunner>();
        services.AddSingleton<AgingAlerts>();
        services.AddSingleton<DuplicateFinder>();
        services.AddSingleton<DuplicateDesk>();
        services.AddSingleton<AiStatusDesk>();
        services.AddSingleton<CardAssistant>();
        services.AddSingleton<ForecastNarrator>();
        services.AddSingleton<InsightDesk>();
        services.AddSingleton<FlowAnalyst>();
        services.AddSingleton<AiJobRunner>();
        services.AddSingleton<AiScheduler>();
        return services;
    }

    /// <summary>
    /// Agent dispatch (section 10). The host adds the adapters in front of GitHub's preview APIs and the
    /// <see cref="AgentSettings"/> feature flag: <c>AddKanbanHubAgents</c>, or stand-ins.
    /// </summary>
    private static IServiceCollection AddAgents(this IServiceCollection services)
    {
        services.AddSingleton<IAgentSessionStore, AgentSessionStore>();
        services.AddSingleton<IGitHubAccountStore, GitHubAccountStore>();

        services.AddSingleton<GitHubConnection>();
        services.AddSingleton<DispatchContexts>();
        services.AddSingleton<AgentLine>();
        services.AddSingleton<AgentDispatches>();
        services.AddSingleton<AgentTracking>();
        return services;
    }

    private static IServiceCollection AddBoardsAndCards(this IServiceCollection services)
    {
        services.AddSingleton<IBoardStore, BoardStore>();
        services.AddSingleton<ICardStore, CardStore>();
        services.AddSingleton<ICardRepository, CardRepository>();
        services.AddSingleton<IBoardSetupStore, BoardSetupStore>();
        services.AddSingleton<CommentStore>();
        services.AddSingleton<ICommentStore>(provider => provider.GetRequiredService<CommentStore>());
        services.AddSingleton<InsightQueries>();
        services.AddSingleton<BoardQueries>();
        services.AddSingleton<CardQueries>();

        services.AddSingleton<BoardCatalog>();
        services.AddSingleton<BoardSettings>();
        services.AddSingleton<BoardSetup>();
        services.AddSingleton<Comments>();
        services.AddSingleton<Backlog>();
        services.AddSingleton<CardCommands>();
        services.AddSingleton<CardBlocking>();
        services.AddSingleton<MoveCardHandler>();
        services.AddSingleton<CancelCardHandler>();
        return services;
    }

    private static IServiceCollection AddEventsAndNotifications(this IServiceCollection services)
    {
        services.AddSingleton<IOutboxStore, OutboxStore>();
        services.AddSingleton<NotificationStore>();
        services.AddSingleton<INotificationStore>(provider => provider.GetRequiredService<NotificationStore>());
        services.AddSingleton<IInboxStore>(provider => provider.GetRequiredService<NotificationStore>());

        services.AddSingleton<Inbox>();
        services.AddSingleton<NotificationSender>();
        services.AddSingleton<OutboxDispatcher>();
        return services;
    }

    private static IServiceCollection AddGitHubSync(this IServiceCollection services)
    {
        services.AddSingleton<IRepoLinkStore, RepoLinkStore>();
        services.AddSingleton<IIssueLinkStore, IssueLinkStore>();
        services.AddSingleton<ISyncLedger, SyncLedgerStore>();
        services.AddSingleton<ISyncedCards, SyncedCards>();
        services.AddSingleton<IPullRequestStore, PullRequestStore>();
        services.AddSingleton<IWebhookDeliveries, WebhookDeliveryStore>();

        services.AddSingleton<SyncJournal>();
        services.AddSingleton<GitHubEdits>();
        services.AddSingleton<GitHubMoves>();
        services.AddSingleton<OutboundSync>();
        services.AddSingleton<IssueText>();
        services.AddSingleton<LabelRequests>();
        services.AddSingleton<IssueClosure>();
        services.AddSingleton<IssueImport>();
        services.AddSingleton<InboundSync>();
        services.AddSingleton<CommandRunner>();
        services.AddSingleton<IssueComments>();
        services.AddSingleton<DoneRuleCheck>();
        services.AddSingleton<PullRequestTracker>();
        services.AddSingleton<Backfill>();
        services.AddSingleton<InboundProcessor>();
        services.AddSingleton<GitHubWriter>();
        services.AddSingleton<BackfillOnLink>();
        services.AddSingleton<RepositoryLinks>();
        services.AddSingleton<IssueLinks>();
        return services;
    }
}
