using System.Text.Json.Serialization;
using Azure.Core;
using KanbanHub.Agents;
using KanbanHub.AI;
using KanbanHub.Api.Auth;
using KanbanHub.Contracts;
using KanbanHub.Data;
using KanbanHub.Data.Security;
using KanbanHub.Domain.Security;
using KanbanHub.GitHub;
using KanbanHub.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace KanbanHub.Api.Composition;

/// <summary>The composition root: the only place that knows which implementation stands behind each port.</summary>
public static class KanbanHubServices
{
    /// <summary>What depends on where the app runs: Entra sign-in, credentials, the database and the Azure and GitHub adapters.</summary>
    public static IServiceCollection AddKanbanHubHost(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        EntraOptions entra = configuration.GetSection(EntraOptions.Section).Get<EntraOptions>() ?? new EntraOptions();
        SqlOptions sql = configuration.GetSection(SqlOptions.Section).Get<SqlOptions>() ?? new SqlOptions();

        services.AddSingleton(entra);
        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(
            new ConfigurationManager<OpenIdConnectConfiguration>(entra.MetadataAddress, new OpenIdConnectConfigurationRetriever()));
        services.AddSingleton<EntraTokenValidator>();

        services.AddKanbanHubMessaging(configuration, isDevelopment);
        services.AddKanbanHubGitHub(configuration);
        services.AddKanbanHubAgents(configuration);
        services.AddKanbanHubAi(configuration);
        services.AddSingleton(provider => sql.CreateFactory(provider.GetRequiredService<TokenCredential>()));

        return services;
    }

    /// <summary>What is the same everywhere, tests included: the use cases and storage, cached authorization and JSON.</summary>
    public static IServiceCollection AddKanbanHubApplication(this IServiceCollection services)
    {
        services.AddKanbanHubCore();
        services.AddMemoryCache();
        services.AddSingleton<IBoardAuthorizer>(provider => new CachedBoardAuthorizer(
            provider.GetRequiredService<SqlBoardAuthorizer>(), provider.GetRequiredService<IMemoryCache>()));

        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
        {
            // Enums travel by name everywhere, including in problem extensions the JSON context doesn't know.
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;   // keeps the board snapshot small
            options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, KanbanHubJsonContext.Default);
        });

        return services;
    }
}
