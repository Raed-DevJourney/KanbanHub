using System.Security.Claims;

namespace KanbanHub.Domain.Security;

/// <summary>
/// Reads the caller's groups from the token's <c>groups</c> claims
/// (<c>groupMembershipClaims = SecurityGroup</c> on the API's app registration).
/// </summary>
public sealed class ClaimsPrincipalResolver : IPrincipalResolver
{
    private const string GroupsClaim = "groups";

    public Task<IReadOnlyList<Guid>> GetPrincipalIdsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        IEnumerable<Guid> groups = user.FindAll(GroupsClaim)
            .Select(claim => Guid.TryParse(claim.Value, out Guid id) ? id : (Guid?)null)
            .OfType<Guid>();

        return Task.FromResult<IReadOnlyList<Guid>>([user.GetObjectId(), .. groups]);
    }
}
