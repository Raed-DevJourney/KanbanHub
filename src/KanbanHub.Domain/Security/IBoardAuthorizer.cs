using System.Security.Claims;

namespace KanbanHub.Domain.Security;

/// <summary>Resolves what a signed-in principal may do on a board.</summary>
public interface IBoardAuthorizer
{
    /// <summary>The union of every role the principal and their groups hold on the board and its workspace.</summary>
    Task<Permission> GetEffectiveAsync(ClaimsPrincipal user, Guid boardId, CancellationToken cancellationToken);
}
