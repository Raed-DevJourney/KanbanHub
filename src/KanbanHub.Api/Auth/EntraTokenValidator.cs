using System.Security.Claims;
using KanbanHub.Domain.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace KanbanHub.Api.Auth;

/// <summary>The <c>Entra</c> configuration section: the single workforce tenant and the API's app registration.</summary>
public sealed record EntraOptions
{
    public const string Section = "Entra";

    public string TenantId { get; init; } = "";

    /// <summary>The client id of the <c>KanbanHub.Api</c> app registration: the audience of every access token.</summary>
    public string ApiClientId { get; init; } = "";

    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public string MetadataAddress => $"{Issuer}/.well-known/openid-configuration";
}

/// <summary>
/// Validates Entra access tokens in the worker itself, so it works on any hosting plan (specification section 7.3).
/// Accepts users calling with the <c>access_as_user</c> scope and services holding the Integration app role.
/// </summary>
public sealed class EntraTokenValidator(IConfigurationManager<OpenIdConnectConfiguration> oidc, EntraOptions options)
{
    public const string UserScope = "access_as_user";

    private const string ScopeClaim = "scp";
    private const string RolesClaim = "roles";

    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Returns the caller, or null when the token is not a valid KanbanHub access token.</summary>
    public async Task<ClaimsPrincipal?> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        OpenIdConnectConfiguration configuration = await oidc.GetConfigurationAsync(cancellationToken);
        TokenValidationResult result = await Handler.ValidateTokenAsync(accessToken, CreateParameters(configuration));

        return result.IsValid && IsForKanbanHub(result.ClaimsIdentity) ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
    }

    private TokenValidationParameters CreateParameters(OpenIdConnectConfiguration configuration) => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudiences = [options.ApiClientId, $"api://{options.ApiClientId}"],
        IssuerSigningKeys = configuration.SigningKeys,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2),
        RoleClaimType = RolesClaim,   // so IsInRole sees Entra app roles
        NameClaimType = "name"
    };

    private static bool IsForKanbanHub(ClaimsIdentity identity)
    {
        bool delegated = identity.FindFirst(ScopeClaim)?.Value.Split(' ').Contains(UserScope) == true;
        bool appOnly = identity.HasClaim(RolesClaim, TenantRoles.Integration);
        return delegated || appOnly;
    }
}
