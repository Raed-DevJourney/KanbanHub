using Azure.Core;
using KanbanHub.Agents;
using KanbanHub.AI;
using KanbanHub.Data;
using KanbanHub.Data.Security;
using KanbanHub.Domain.Notifications;
using KanbanHub.Domain.Security;
using KanbanHub.GitHub;
using KanbanHub.Messaging;

namespace KanbanHub.Workers;

public static class WorkerServices
{
    /// <summary>
    /// What the workers need beyond messaging: the use cases and their storage (changes that arrive from GitHub go
    /// through the same ones as the API's), email delivery, and the GitHub adapters.
    /// </summary>
    public static IServiceCollection AddKanbanHubWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        SqlOptions sql = configuration.GetSection(SqlOptions.Section).Get<SqlOptions>() ?? new SqlOptions();
        EmailOptions email = configuration.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();

        services.AddKanbanHubCore();
        services.AddKanbanHubGitHub(configuration);
        services.AddKanbanHubAgents(configuration);   // pull requests that come back from GitHub move agent sessions on
        services.AddKanbanHubAi(configuration);
        services.AddSingleton(provider => sql.CreateFactory(provider.GetRequiredService<TokenCredential>()));
        services.AddSingleton<IBoardAuthorizer>(provider => provider.GetRequiredService<SqlBoardAuthorizer>());
        services.AddSingleton<IEmailSender>(provider => AcsEmailSender.Create(email, provider.GetRequiredService<TokenCredential>()));
        services.AddSingleton<NotificationGenerator>();

        return services;
    }
}
