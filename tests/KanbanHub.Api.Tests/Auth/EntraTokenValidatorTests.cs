using System.Security.Claims;
using System.Security.Cryptography;
using KanbanHub.Api.Auth;
using KanbanHub.Domain.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace KanbanHub.Api.Tests.Auth;

/// <summary>Tokens are signed here with a throwaway key that stands in for Entra's signing keys.</summary>
public class EntraTokenValidatorTests
{
    private static readonly EntraOptions Options = new()
    {
        TenantId = "0f6c1d57-7a50-4d1e-9c5e-6a1f2b3c4d5e",
        ApiClientId = "4b0f9a52-1c2d-4e3f-8a9b-0c1d2e3f4a5b"
    };

    private static readonly RsaSecurityKey EntraKey = new(RSA.Create(2048)) { KeyId = "entra" };
    private static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "other" };
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly EntraTokenValidator validator = new(new StaticConfigurationManager<OpenIdConnectConfiguration>(Metadata()), Options);

    [Fact]
    public async Task A_user_token_with_the_api_scope_is_accepted()
    {
        ClaimsPrincipal? user = await ValidateAsync(Token());

        Assert.NotNull(user);
        Assert.Equal(UserId, user.GetObjectId());
    }

    [Fact]
    public async Task The_api_uri_form_of_the_audience_is_accepted_too() =>
        Assert.NotNull(await ValidateAsync(Token(token => token.Audience = $"api://{Options.ApiClientId}")));

    [Fact]
    public async Task App_roles_in_the_token_answer_IsInRole()
    {
        ClaimsPrincipal? user = await ValidateAsync(Token(token => token.Claims["roles"] = new[] { TenantRoles.Admin, TenantRoles.User }));

        Assert.NotNull(user);
        Assert.True(user.IsTenantAdmin());
        Assert.False(user.IsInRole(TenantRoles.Auditor));
    }

    [Fact]
    public async Task A_service_holding_the_integration_role_is_accepted_without_a_scope()
    {
        string appOnly = Token(token =>
        {
            token.Claims.Remove("scp");
            token.Claims["roles"] = new[] { TenantRoles.Integration };
        });

        Assert.NotNull(await ValidateAsync(appOnly));
    }

    [Fact]
    public async Task A_token_with_neither_the_scope_nor_the_integration_role_is_refused() =>
        Assert.Null(await ValidateAsync(Token(token => token.Claims["scp"] = "User.Read")));

    [Fact]
    public async Task A_token_for_another_api_is_refused() =>
        Assert.Null(await ValidateAsync(Token(token => token.Audience = "00000003-0000-0000-c000-000000000000")));

    [Fact]
    public async Task A_token_from_another_tenant_is_refused() =>
        Assert.Null(await ValidateAsync(Token(token => token.Issuer = "https://login.microsoftonline.com/another-tenant/v2.0")));

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        string expired = Token(token =>
        {
            token.NotBefore = DateTime.UtcNow.AddHours(-2);
            token.Expires = DateTime.UtcNow.AddHours(-1);
        });

        Assert.Null(await ValidateAsync(expired));
    }

    [Fact]
    public async Task A_token_signed_with_an_unknown_key_is_refused() =>
        Assert.Null(await ValidateAsync(Token(token => token.SigningCredentials = Signing(OtherKey))));

    [Fact]
    public async Task Text_that_is_not_a_token_is_refused() =>
        Assert.Null(await ValidateAsync("not-a-token"));

    private Task<ClaimsPrincipal?> ValidateAsync(string token) =>
        validator.ValidateAsync(token, TestContext.Current.CancellationToken);

    private static OpenIdConnectConfiguration Metadata()
    {
        var metadata = new OpenIdConnectConfiguration { Issuer = Options.Issuer };
        metadata.SigningKeys.Add(EntraKey);
        return metadata;
    }

    private static string Token(Action<SecurityTokenDescriptor>? change = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Options.Issuer,
            Audience = Options.ApiClientId,
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = Signing(EntraKey),
            Claims = new Dictionary<string, object> { ["oid"] = UserId.ToString(), ["scp"] = "access_as_user" }
        };
        change?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static SigningCredentials Signing(SecurityKey key) => new(key, SecurityAlgorithms.RsaSha256);
}
