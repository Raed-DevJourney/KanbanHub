using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests;

/// <summary>The post-deployment seed must store exactly the roles the domain defines.</summary>
public class SeedTests(KanbanDatabase database)
{
    [Fact]
    public async Task The_seeded_roles_are_the_domains_built_in_roles()
    {
        List<BoardRole> seeded = await database.Connections.QueryAsync(
            "SELECT RoleId, [Name], Permissions FROM kb.RoleDefinition WHERE IsBuiltIn = 1 ORDER BY RoleId;",
            args: null,
            reader => new BoardRole(reader.GetInt16(0), reader.GetString(1), (Permission)reader.GetInt64(2)));

        Assert.Equal(BuiltInRoles.All, seeded);
    }
}
