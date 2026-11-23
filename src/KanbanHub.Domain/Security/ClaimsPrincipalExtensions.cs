using System.Security.Claims;

namespace KanbanHub.Domain.Security;

public static class ClaimsPrincipalExtensions
{
    // Entra emits "oid"; handlers that map inbound claims rename it to the long form.
    private static readonly string[] ObjectIdClaimTypes =
        ["oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"];

    /// <summary>The principal's Entra object id.</summary>
    /// <exception cref="InvalidOperationException">The token carries no usable object id.</exception>
    public static Guid GetObjectId(this ClaimsPrincipal user)
    {
        string? value = ObjectIdClaimTypes.Select(type => user.FindFirst(type)?.Value).FirstOrDefault(v => v is not null);
        return Guid.TryParse(value, out Guid objectId)
            ? objectId
            : throw new InvalidOperationException("The principal has no Entra object id claim.");
    }
}
