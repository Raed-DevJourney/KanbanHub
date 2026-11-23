using Azure.Messaging.ServiceBus;
using KanbanHub.Domain.Ai;
using KanbanHub.Messaging;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Workers;

/// <summary>
/// The background AI work (section 11.1): timers decide which boards are due and queue a job for each; one
/// function runs the jobs. Only boards that opted in are queued, and a job only ever writes an insight.
/// </summary>
public sealed class AiFunctions(AiJobRunner runner, AiScheduler scheduler, AgingAlerts aging)
{
    /// <summary>
    /// Every fifteen minutes: the aging rule over every board (feature 4). It is a rule, so it needs no opt-in;
    /// only the wording of an alert comes from the model, and only on boards that use AI.
    /// </summary>
    [Function("RaiseAgingAlerts")]
    public Task RaiseAgingAlerts([TimerTrigger("0 */15 * * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        aging.RaiseAsync(cancellationToken);

    [Function("RunAiJob")]
    public Task Run(
        [ServiceBusTrigger(ServiceBusAiJobInbox.Queue, Connection = "ServiceBus")] ServiceBusReceivedMessage message,
        CancellationToken cancellationToken) =>
        ServiceBusAiJobInbox.Read(message) is { } job ? runner.RunAsync(job, cancellationToken) : Task.CompletedTask;

    /// <summary>Every five minutes: the boards whose standup is half an hour away get their brief.</summary>
    [Function("QueueStandupBriefs")]
    public Task QueueStandupBriefs([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        scheduler.QueueDueStandupBriefsAsync(cancellationToken);

    [Function("QueueBottleneckChecks")]
    public Task QueueBottleneckChecks([TimerTrigger("0 10 * * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        scheduler.QueueForEveryBoardAsync(AiFeature.Bottleneck, cancellationToken);

    /// <summary>Mondays at 06:00 UTC: a week of flow is enough to say something about the limits.</summary>
    [Function("QueueWipAdvice")]
    public Task QueueWipAdvice([TimerTrigger("0 0 6 * * 1")] TimerInfo timer, CancellationToken cancellationToken) =>
        scheduler.QueueForEveryBoardAsync(AiFeature.WipAdvice, cancellationToken);
}
