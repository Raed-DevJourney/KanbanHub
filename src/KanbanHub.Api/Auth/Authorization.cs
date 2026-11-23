using System.Security.Claims;
using KanbanHub.Domain.Security;
using Microsoft.Extensions.Caching.Memory;

namespace KanbanHub.Api.Auth;

/// <summary>
/// Remembers effective permissions for a minute, so a board full of requests asks the database once.
/// A revoked role therefore stops working within 60 seconds (specification section 7.2).
/// </summary>
public sealed class CachedBoardAuthorizer(IBoardAuthorizer inner, IMemoryCache cache) : IBoardAuthorizer
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public async Task<Permission> GetEffectiveAsync(ClaimsPrincipal user, Guid boardId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync((user.GetObjectId(), boardId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Lifetime;
            return inner.GetEffectiveAsync(user, boardId, cancellationToken);
        });
}
