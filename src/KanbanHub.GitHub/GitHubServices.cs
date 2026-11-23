using System.Net.Http.Headers;
using Azure.Core;
using Azure.Security.KeyVault.Keys.Cryptography;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanHub.GitHub;

public static class GitHubServices
{
    private const string ApiVersion = "2022-11-28";

    /// <summary>
    /// The GitHub side for the function apps: the REST client with its resilience handler, the token provider
    /// that signs in Key Vault, the webhook verifier and parser. Needs a <see cref="TokenCredential"/> registered.
    /// </summary>
    public static IServiceCollection AddKanbanHubGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        GitHubAppOptions options = configuration.GetSection(GitHubAppOptions.Section).Get<GitHubAppOptions>() ?? new GitHubAppOptions();

        services.AddSingleton(options);
        services.AddSingleton(options.ToSyncSettings());
        services.AddSingleton<WebhookVerifier>();
        services.AddSingleton<IGitHubEventParser, GitHubWebhookParser>();

        services.AddMemoryCache();
        services.AddHttpClient(GitHubRestClient.ClientName, ConfigureClient).AddStandardResilienceHandler();
        services.AddSingleton<IAppJwtSigner>(provider =>
            new KeyVaultJwtSigner(new CryptographyClient(new Uri(options.KeyId), provider.GetRequiredService<TokenCredential>())));
        services.AddSingleton<GitHubAppTokenProvider>();
        services.AddSingleton<IGitHubClient, GitHubRestClient>();

        return services.AddUserAuthorization(options);
    }

    /// <summary>"Connect GitHub": the app's user authorization flow, and the Key Vault key that protects refresh tokens.</summary>
    private static IServiceCollection AddUserAuthorization(this IServiceCollection services, GitHubAppOptions options)
    {
        services.AddHttpClient(GitHubUserAuthorization.LoginClientName, ConfigureLoginClient).AddStandardResilienceHandler();
        services.AddSingleton<IGitHubAuthorization, GitHubUserAuthorization>();
        services.AddSingleton<ITokenProtector>(provider =>
            new KeyVaultTokenProtector(new CryptographyClient(new Uri(options.TokenKeyId), provider.GetRequiredService<TokenCredential>())));

        return services;
    }

    private static void ConfigureLoginClient(HttpClient client)
    {
        client.BaseAddress = new Uri("https://github.com/");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KanbanHub", "1.0"));
    }

    private static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KanbanHub", "1.0"));
    }
}
