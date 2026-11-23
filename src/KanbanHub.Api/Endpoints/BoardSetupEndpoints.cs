using System.Text.Json.Nodes;
using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Domain;
using KanbanHub.Domain.Boards;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>A board's structure: its preferences, its workflow, its colors, and deleting it.</summary>
public sealed class BoardSetupEndpoints(BoardSetup setup)
{
    /// <summary>A JSON merge patch over the board's preferences.</summary>
    [Function("EditBoard")]
    public async Task<IActionResult> Edit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "v1/boards/{boardId:guid}")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<JsonObject>(cancellationToken) is not { } patch) return Problems.BodyRequired();

        Outcome<Saved> outcome = await setup.EditPreferencesAsync(boardId, patch.MergeInto, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    [Function("DeleteBoard")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "v1/boards/{boardId:guid}")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        Outcome<Saved> outcome = await setup.DeleteAsync(boardId, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    /// <summary>The whole workflow in order; steps left out are retired and the workflow version goes up.</summary>
    [Function("SetWorkflow")]
    public async Task<IActionResult> SetWorkflow(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v1/boards/{boardId:guid}/workflow")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<SetWorkflowRequest>(cancellationToken) is not { Steps: not null } body)
            return Problems.BodyRequired();

        Outcome<Saved> outcome = await setup.ChangeWorkflowAsync(boardId, body.Steps, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    [Function("SetPalette")]
    public async Task<IActionResult> SetPalette(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v1/boards/{boardId:guid}/palette")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<SetPaletteRequest>(cancellationToken) is not { Colors: not null } body)
            return Problems.BodyRequired();

        Outcome<Saved> outcome = await setup.SetPaletteAsync(boardId, body.Colors, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }
}
