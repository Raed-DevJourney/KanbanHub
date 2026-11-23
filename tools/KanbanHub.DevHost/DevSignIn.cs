using System.Security.Claims;
using KanbanHub.Domain.Security;

namespace KanbanHub.DevHost;

/// <summary>One made-up person per board role, so every permission level can be tried locally.</summary>
internal sealed record DevPersona(string Key, string Name, Guid ObjectId, BoardRole? BoardRole, params string[] TenantRoles)
{
    public static readonly DevPersona Owner = new(
        "owner", "Olivia Owner", new Guid("0a000000-0000-4000-8000-000000000001"), BuiltInRoles.Owner, Domain.Security.TenantRoles.BoardCreator);

    public static IReadOnlyList<DevPersona> All { get; } =
    [
        Owner,
        new("manager", "Marco Flow Manager", new Guid("0a000000-0000-4000-8000-000000000002"), BuiltInRoles.FlowManager),
        new("member", "Mia Member", new Guid("0a000000-0000-4000-8000-000000000003"), BuiltInRoles.Member),
        new("contributor", "Carl Contributor", new Guid("0a000000-0000-4000-8000-000000000004"), BuiltInRoles.Contributor),
        new("viewer", "Vera Viewer", new Guid("0a000000-0000-4000-8000-000000000005"), BuiltInRoles.Viewer),
        new("stranger", "Sam Stranger", new Guid("0a000000-0000-4000-8000-000000000006"), BoardRole: null)
    ];

    public ClaimsPrincipal ToPrincipal()
    {
        Claim[] claims =
        [
            new("oid", ObjectId.ToString()), new("name", Name), new("preferred_username", $"{Key}@kanbanhub.test"),
            .. TenantRoles.Select(role => new Claim("roles", role))
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "dev", nameType: "name", roleType: "roles"));
    }
}

/// <summary>
/// Stands in for the Entra token check: the <c>X-Dev-User</c> header names the persona who is calling.
/// Health stays anonymous, as in the real host.
/// </summary>
internal sealed class DevSignIn(RequestDelegate next)
{
    public const string Header = "X-Dev-User";

    public async Task InvokeAsync(HttpContext http)
    {
        if (HttpMethods.IsOptions(http.Request.Method) || IsOpenTo(http.Request.Path))
        {
            await next(http);
            return;
        }

        string key = http.Request.Headers[Header].ToString();
        if (DevPersona.All.FirstOrDefault(persona => persona.Key == key) is not { } persona)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        http.User = persona.ToPrincipal();
        await next(http);
    }

    /// <summary>
    /// Health is anonymous everywhere; the hub identifies its callers itself (a WebSocket can't send headers);
    /// the GitHub webhook is authenticated by its signature; and the /dev doors are for scripts on this machine.
    /// </summary>
    private static bool IsOpenTo(PathString path) =>
        path.StartsWithSegments("/api/health") || path.StartsWithSegments(BoardHub.Path)
        || path.StartsWithSegments("/api/github/webhook") || path.StartsWithSegments("/dev");
}
