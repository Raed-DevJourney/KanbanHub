using KanbanHub.AI;
using KanbanHub.Domain.Ai;

namespace KanbanHub.DevHost;

/// <summary>
/// AI on a development machine: a stand-in model that answers from the data by simple rules, background jobs
/// that run in this process, and doors to run a job now and to see what the model was asked.
/// </summary>
internal static class LocalAi
{
    public static IServiceCollection AddLocalAi(this IServiceCollection services)
    {
        services.AddSingleton(new AiSettings(IsConfigured: true));
        services.AddSingleton<ScriptedAiModel>();
        services.AddSingleton<IAiModel>(provider => provider.GetRequiredService<ScriptedAiModel>());
        services.AddSingleton<IAiJobInbox, InlineAiJobInbox>();
        services.AddSingleton<IEmbedder, WordHashEmbedder>();
        return services.AddConsumer<DuplicateFinder>();
    }

    public static void MapLocalAi(this WebApplication app)
    {
        // Runs a background analysis now instead of waiting for its timer:
        // POST /dev/ai/StandupBrief/{boardId}, /dev/ai/Bottleneck/{boardId}, /dev/ai/WipAdvice/{boardId}
        app.MapPost("/dev/ai/{feature}/{boardId:guid}", async (string feature, Guid boardId, AiJobRunner runner, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse(feature, ignoreCase: true, out AiFeature kind)) return Results.NotFound();

            await runner.RunAsync(new AiJob(boardId, kind), cancellationToken);
            return Results.Accepted();
        });

        // Runs the aging rule now, over every board, instead of waiting fifteen minutes: POST /dev/ai/aging
        app.MapPost("/dev/ai/aging", async (AgingAlerts aging, CancellationToken cancellationToken) =>
            Results.Json(new { alerted = await aging.RaiseAsync(cancellationToken) }));

        // What the stand-in model was asked: the fixed instructions, and the data they came with.
        app.MapGet("/dev/ai", (ScriptedAiModel model) => Results.Json(
            model.Requests.Select(request => new { feature = request.Feature.ToString(), request.Instructions, data = request.Data })));
    }
}
