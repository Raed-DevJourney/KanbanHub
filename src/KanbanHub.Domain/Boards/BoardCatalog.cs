using System.Security.Claims;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Boards;

/// <summary>Creates workspaces and boards. Both need the tenant's BoardCreator role (section 7.2).</summary>
public sealed class BoardCatalog(IBoardStore store, TimeProvider clock)
{
    public const int MaxNameLength = 200;

    public async Task<Outcome<Workspace>> CreateWorkspaceAsync(
        string name, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!user.MayCreateBoards()) return new TenantRoleRequired(TenantRoles.BoardCreator);
        if (FindNameProblem(name) is { } problem) return new Invalid(problem);

        var workspace = new Workspace(Guid.NewGuid(), name.Trim());
        await store.CreateWorkspaceAsync(workspace, StampFor(user), cancellationToken);
        return workspace;
    }

    public async Task<Outcome<NewBoard>> CreateBoardAsync(
        CreateBoardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!user.MayCreateBoards()) return new TenantRoleRequired(TenantRoles.BoardCreator);
        if (FindNameProblem(command.Name) is { } problem) return new Invalid(problem);
        if (BoardTemplates.Find(command.TemplateKey) is not { } template)
            return new Invalid($"Unknown board template '{command.TemplateKey}'.");

        NewBoard board = Draft(command, template, user);
        return await store.CreateBoardAsync(board, cancellationToken) ? board : new NotFound("workspace");
    }

    private NewBoard Draft(CreateBoardCommand command, BoardTemplate template, ClaimsPrincipal user) => new()
    {
        WorkspaceId = command.WorkspaceId,
        Name = command.Name.Trim(),
        TimeZoneId = command.TimeZoneId,
        Template = template,
        OwnerId = user.GetObjectId(),
        Now = clock.GetUtcNow()
    };

    private Stamp StampFor(ClaimsPrincipal user) => new(user.GetObjectId(), clock.GetUtcNow());

    private static string? FindNameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "A name is required."
        : name.Trim().Length > MaxNameLength ? $"A name can be at most {MaxNameLength} characters."
        : null;
}
