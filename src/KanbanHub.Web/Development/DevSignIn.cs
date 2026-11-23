using System.Security.Claims;
using KanbanHub.Web.Composition;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace KanbanHub.Web.Development;

/// <summary>
/// Local development without Entra: the app signs in as one of the made-up people of
/// <c>tools/KanbanHub.DevHost</c> and says who in the <c>X-Dev-User</c> header. Compiled into Debug builds only.
/// </summary>
internal static class DevSignIn
{
    public const string Header = "X-Dev-User";

    public static readonly IReadOnlyList<(string Key, string Name)> Personas =
    [
        ("owner", "Olivia Owner"),
        ("manager", "Marco Flow Manager"),
        ("member", "Mia Member"),
        ("contributor", "Carl Contributor"),
        ("viewer", "Vera Viewer"),
        ("stranger", "Sam Stranger")
    ];

    public static void Add(IServiceCollection services, Uri apiBase)
    {
        services.AddScoped<DevPersonaState>();
        services.AddScoped<AuthenticationStateProvider, DevAuthenticationStateProvider>();
        services.AddScoped(provider => new HttpClient(new DevUserHandler(provider.GetRequiredService<DevPersonaState>()))
        {
            BaseAddress = apiBase
        });
        services.AddSingleton(new HeaderExtension(typeof(PersonaSwitcher)));
    }
}

/// <summary>Which persona is signed in: the one named in the <c>?as=</c> query the app was opened with, else the owner.</summary>
internal sealed class DevPersonaState(NavigationManager navigation)
{
    public const string QueryKey = "as";

    public string Key { get; } = FromQuery(navigation.Uri) ?? DevSignIn.Personas[0].Key;

    public string Name => DevSignIn.Personas.First(persona => persona.Key == Key).Name;

    private static string? FromQuery(string uri)
    {
        string? requested = System.Web.HttpUtility.ParseQueryString(new Uri(uri).Query)[QueryKey];
        return DevSignIn.Personas.Any(persona => persona.Key == requested) ? requested : null;
    }
}

internal sealed class DevAuthenticationStateProvider(DevPersonaState persona) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("name", persona.Name)], "dev", nameType: "name", roleType: "roles");
        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }
}
internal sealed class DevUserHandler(DevPersonaState persona) : DelegatingHandler(new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add(DevSignIn.Header, persona.Key);
        return base.SendAsync(request, cancellationToken);
    }
}
