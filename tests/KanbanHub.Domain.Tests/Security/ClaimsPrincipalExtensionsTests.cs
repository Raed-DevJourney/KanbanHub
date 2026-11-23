using System.Security.Claims;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Tests.Security;

public class ClaimsPrincipalExtensionsTests
{
    private static readonly Guid ObjectId = Guid.NewGuid();

    [Theory]
    [InlineData("oid")]
    [InlineData("http://schemas.microsoft.com/identity/claims/objectidentifier")]
    public void The_object_id_is_read_from_either_claim_name(string claimType) =>
        Assert.Equal(ObjectId, UserWith(new Claim(claimType, ObjectId.ToString())).GetObjectId());

    [Fact]
    public void A_principal_without_an_object_id_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => UserWith(new Claim("sub", "someone")).GetObjectId());

    [Fact]
    public void An_object_id_that_is_not_a_guid_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => UserWith(new Claim("oid", "not-a-guid")).GetObjectId());

    private static ClaimsPrincipal UserWith(Claim claim) => new(new ClaimsIdentity([claim]));
}
