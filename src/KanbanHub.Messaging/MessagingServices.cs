using Azure.Core;
using Azure.Messaging.ServiceBus;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanHub.Messaging;

public static class MessagingServices
{
    /// <summary>
    /// The Azure side of events, the same for every function app: the app's credential, the real-time gateway
    /// and a publisher that tells watchers and then feeds the topic. Nothing here holds a secret.
    /// </summary>
    public static IServiceCollection AddKanbanHubMessaging(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        services.AddSingleton(AzureCredentials.Create(isDevelopment, configuration["AZURE_CLIENT_ID"]));
        services.AddSingleton<IRealtimeGateway>(provider => SignalRServiceGateway.Create(
            new Uri(Required(configuration, "SignalR:serviceUri")), provider.GetRequiredService<TokenCredential>()));
        services.AddSingleton(provider => new ServiceBusClient(
            Required(configuration, "ServiceBus:fullyQualifiedNamespace"), provider.GetRequiredService<TokenCredential>()));

        services.AddSingleton<BoardBroadcaster>();
        services.AddSingleton<IBoardEventPublisher>(provider => new BroadcastingPublisher(
            provider.GetRequiredService<BoardBroadcaster>(),
            new ServiceBusEventPublisher(provider.GetRequiredService<ServiceBusClient>().CreateSender(ServiceBusEventPublisher.Topic))));

        services.AddSingleton<IGitHubInbox>(provider => new ServiceBusGitHubInbox(
            provider.GetRequiredService<ServiceBusClient>().CreateSender(ServiceBusGitHubInbox.Queue),
            provider.GetRequiredService<IGitHubEventParser>()));
        services.AddSingleton<ScheduledRetry>();
        services.AddSingleton<IAiJobInbox>(provider => new ServiceBusAiJobInbox(
            provider.GetRequiredService<ServiceBusClient>().CreateSender(ServiceBusAiJobInbox.Queue)));

        return services;
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The setting '{key}' is missing.");
}
