using KanbanHub.Domain.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanHub.Agents;

/// <summary>The <c>Agents</c> configuration section.</summary>
public sealed record class AgentOptions
{
    public const string Section = "Agents";

    /// <summary>
    /// The feature flag (section 10): GitHub's agent APIs are in public preview, so dispatch stays off until an
    /// environment switches it on, and can be switched off again without a deployment.
    /// </summary>
    public bool Enabled { get; init; }
}

public static class AgentServices
{
    /// <summary>
    /// The agent adapter for the function apps. Needs the <c>github</c> HTTP client that
    /// <c>AddKanbanHubGitHub</c> registers.
    /// </summary>
    public static IServiceCollection AddKanbanHubAgents(this IServiceCollection services, IConfiguration configuration)
    {
        AgentOptions options = configuration.GetSection(AgentOptions.Section).Get<AgentOptions>() ?? new AgentOptions();

        services.AddSingleton(new AgentSettings(options.Enabled));
        services.AddSingleton<IAgentDispatcher, CopilotDispatcher>();
        return services;
    }
}
