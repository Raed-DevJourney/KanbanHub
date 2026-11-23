using System.Security.Claims;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Boards;

/// <summary>Storage port for the structure of a board: its preferences, workflow and palette.</summary>
public interface IBoardSetupStore
{
    Task<BoardPreferences?> FindPreferencesAsync(Guid boardId, CancellationToken cancellationToken);

    /// <summary>Stores the preferences; <paramref name="archived"/> soft-deletes the board along the way.</summary>
    Task SavePreferencesAsync(
        Guid boardId, BoardPreferences preferences, bool archived, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Replaces the steps and bumps the workflow version, unless a step being removed still holds cards.</summary>
    Task<Outcome<Saved>> SetWorkflowAsync(
        Guid boardId, IReadOnlyList<StepDraft> steps, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Replaces the board's own colors; tokens left out fall back to their defaults.</summary>
    Task SetPaletteAsync(Guid boardId, IReadOnlyList<PaletteColor> colors, Stamp stamp, CancellationToken cancellationToken);
}

/// <summary>Reshapes a board: its preferences, its workflow, its colors, and its removal.</summary>
public sealed class BoardSetup(IBoardSetupStore store, IBoardAuthorizer authorizer, TimeProvider clock)
{
    private static readonly NotFound NoSuchBoard = new("board");

    /// <summary><paramref name="change"/> turns the current preferences into the wanted ones (null when it can't).</summary>
    public async Task<Outcome<Saved>> EditPreferencesAsync(
        Guid boardId, Func<BoardPreferences, BoardPreferences?> change, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.BoardSettingsEdit, cancellationToken) is { } denied)
            return denied;
        if (await store.FindPreferencesAsync(boardId, cancellationToken) is not { } current) return NoSuchBoard;
        if (change(current) is not { } wanted) return new Invalid("The changes don't describe board settings.");
        if (wanted.FindProblems() is { Count: > 0 } problems) return new Invalid(problems);

        await store.SavePreferencesAsync(boardId, wanted, archived: false, StampFor(user), cancellationToken);
        return Saved.Instance;
    }

    /// <summary>A soft delete: the board disappears, its history stays.</summary>
    public async Task<Outcome<Saved>> DeleteAsync(Guid boardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.BoardDelete, cancellationToken) is { } denied)
            return denied;
        if (await store.FindPreferencesAsync(boardId, cancellationToken) is not { } current) return NoSuchBoard;

        await store.SavePreferencesAsync(boardId, current, archived: true, StampFor(user), cancellationToken);
        return Saved.Instance;
    }

    public async Task<Outcome<Saved>> ChangeWorkflowAsync(
        Guid boardId, IReadOnlyList<StepDraft> steps, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.WorkflowEdit, cancellationToken) is { } denied)
            return denied;
        if (WorkflowRules.FindProblems(steps) is { Count: > 0 } problems) return new Invalid(problems);

        return await store.SetWorkflowAsync(boardId, steps, StampFor(user), cancellationToken);
    }

    public async Task<Outcome<Saved>> SetPaletteAsync(
        Guid boardId, IReadOnlyList<PaletteColor> overrides, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.PaletteEdit, cancellationToken) is { } denied)
            return denied;
        if (Palette.FindProblems(overrides) is { Count: > 0 } problems) return new Invalid(problems);

        await store.SetPaletteAsync(boardId, overrides, StampFor(user), cancellationToken);
        return Saved.Instance;
    }

    private Stamp StampFor(ClaimsPrincipal user) => new(user.GetObjectId(), clock.GetUtcNow());
}
