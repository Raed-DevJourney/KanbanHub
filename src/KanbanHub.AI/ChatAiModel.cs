using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.Core;
using KanbanHub.Domain.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KanbanHub.AI;

/// <summary>
/// Asks a chat model for a C# record (section 11.2). The instructions are the system message; the board's data
/// goes in the user message as JSON, so card text is never part of what the model is told to do. An answer that
/// isn't valid JSON for the record, or that the deployment's content filter stopped, is dropped and logged.
/// </summary>
public sealed partial class ChatAiModel(IChatClient chat, ILogger<ChatAiModel> logger) : IAiModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<AiAnswer<TResult>> AskAsync<TResult>(AiRequest request, CancellationToken cancellationToken)
        where TResult : class
    {
        ChatMessage[] messages =
        [
            new(ChatRole.System, request.Instructions),
            new(ChatRole.User, JsonSerializer.Serialize(request.Data, request.Data.GetType(), Json))
        ];

        try
        {
            ChatResponse<TResult> response = await chat.GetResponseAsync<TResult>(messages, Json, cancellationToken: cancellationToken);
            int tokens = (int)Math.Min(int.MaxValue, response.Usage?.TotalTokenCount ?? 0);
            if (response.TryGetResult(out TResult? result)) return new AiAnswer<TResult>(result, tokens);

            LogNotARecord(logger, request.Feature, typeof(TResult).Name);
            return new AiAnswer<TResult>(null, tokens);
        }
        catch (Exception refused) when (refused is ClientResultException or HttpRequestException or JsonException)
        {
            LogNoAnswer(logger, refused, request.Feature);
            return new AiAnswer<TResult>(null, 0);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The model's answer for {Feature} was not a valid {Record}; dropped.")]
    private static partial void LogNotARecord(ILogger logger, AiFeature feature, string record);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The model gave no answer for {Feature}; nothing is shown.")]
    private static partial void LogNoAnswer(ILogger logger, Exception exception, AiFeature feature);
}

/// <summary>The <c>Ai</c> configuration section. No key: the app's identity is given the OpenAI User role instead.</summary>
public sealed record class AiOptions
{
    public const string Section = "Ai";

    /// <summary>The Azure OpenAI endpoint of the Foundry resource, for example <c>https://name.openai.azure.com/</c>.</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>The chat deployment's name, so the model can be swapped without a code change.</summary>
    public string ChatDeployment { get; init; } = "";

    /// <summary>
    /// The embedding deployment for the duplicate finder. Its vectors must have 1536 dimensions, the size of the
    /// database column. Without it the duplicate finder stays off; everything else works.
    /// </summary>
    public string EmbeddingDeployment { get; init; } = "";

    public bool IsConfigured => Endpoint.Length > 0 && ChatDeployment.Length > 0;

    public bool CanEmbed => IsConfigured && EmbeddingDeployment.Length > 0;
}

public static class AiServices
{
    /// <summary>
    /// The model for the function apps. Without an endpoint and a deployment the AI features answer
    /// "not set up here" and nothing is ever sent anywhere. Needs a <see cref="TokenCredential"/> registered.
    /// </summary>
    public static IServiceCollection AddKanbanHubAi(this IServiceCollection services, IConfiguration configuration)
    {
        AiOptions options = configuration.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();

        services.AddSingleton(new AiSettings(options.IsConfigured));
        services.AddEmbedder(options);
        if (!options.IsConfigured) return services.AddSingleton<IAiModel, NoAiModel>();

        services.AddSingleton(provider => CreateChatClient(options, provider.GetRequiredService<TokenCredential>()));
        return services.AddSingleton<IAiModel, ChatAiModel>();
    }

    private static void AddEmbedder(this IServiceCollection services, AiOptions options)
    {
        if (!options.CanEmbed)
        {
            services.AddSingleton<IEmbedder, NoEmbedder>();
            return;
        }

        services.AddSingleton<IEmbedder>(provider => new EmbeddingModel(
            CreateEmbeddingGenerator(options, provider.GetRequiredService<TokenCredential>()),
            options.EmbeddingDeployment,
            provider.GetRequiredService<ILogger<EmbeddingModel>>()));
    }

    /// <summary>The embedding generator for a deployment; <paramref name="clientOptions"/> as for the chat client.</summary>
    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        AiOptions options, TokenCredential credential, AzureOpenAIClientOptions? clientOptions = null) =>
        new AzureOpenAIClient(new Uri(options.Endpoint), credential, clientOptions ?? new AzureOpenAIClientOptions())
            .GetEmbeddingClient(options.EmbeddingDeployment)
            .AsIEmbeddingGenerator();

    /// <summary>The chat client for a deployment. <paramref name="clientOptions"/> lets a test put a stub where Azure is.</summary>
    public static IChatClient CreateChatClient(AiOptions options, TokenCredential credential, AzureOpenAIClientOptions? clientOptions = null) =>
        new AzureOpenAIClient(new Uri(options.Endpoint), credential, clientOptions ?? new AzureOpenAIClientOptions())
            .GetChatClient(options.ChatDeployment)
            .AsIChatClient();
}

/// <summary>Stands where a model would be when none is configured. The runner refuses before it is ever asked.</summary>
public sealed class NoAiModel : IAiModel
{
    public Task<AiAnswer<TResult>> AskAsync<TResult>(AiRequest request, CancellationToken cancellationToken)
        where TResult : class => Task.FromResult(new AiAnswer<TResult>(null, 0));
}
