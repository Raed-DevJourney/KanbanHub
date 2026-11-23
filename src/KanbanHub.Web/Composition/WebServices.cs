using KanbanHub.Web.Api;
using KanbanHub.Web.Realtime;
using KanbanHub.Web.State;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace KanbanHub.Web.Composition;

/// <summary>A component the layout shows next to the signed-in user (the dev persona switcher, when present).</summary>
public sealed record HeaderExtension(Type Component);

/// <summary>The composition root of the web app.</summary>
public static class WebServices
{
    public static void AddKanbanHubWeb(this WebAssemblyHostBuilder builder)
    {
        var apiBase = new Uri(new Uri(builder.HostEnvironment.BaseAddress), builder.Configuration["Api:BaseUrl"] ?? "/api/v1/");

        builder.Services.AddAuthorizationCore();
        builder.Services.AddCascadingAuthenticationState();
        if (!TryAddDevSignIn(builder, apiBase)) AddEntraSignIn(builder, apiBase);

        builder.Services.AddScoped<ApiTransport>();
        builder.Services.AddScoped<KanbanApi>();
        builder.Services.AddScoped<Session>();
        builder.Services.AddScoped<Notices>();
        builder.Services.AddScoped<BoardStore>();
        builder.Services.AddScoped<DragState>();
        builder.Services.AddScoped<KeyboardMove>();
        builder.Services.AddScoped<BoardMoves>();
        builder.Services.AddScoped<BoardFilter>();
        builder.Services.AddScoped<SettingsSaver>();
        builder.Services.AddScoped<InboxState>();
        builder.Services.AddScoped<LiveUpdates>();
        builder.Services.AddSingleton<ILiveConnectionFactory, SignalRConnectionFactory>();
        builder.Services.AddSingleton(TimeProvider.System);
    }

    /// <summary>Sign-in with Microsoft Entra ID; every API call carries an access token for the API's scope.</summary>
    private static void AddEntraSignIn(WebAssemblyHostBuilder builder, Uri apiBase)
    {
        string scope = builder.Configuration["Api:Scope"] ?? "";

        builder.Services.AddMsalAuthentication(options =>
        {
            builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
            options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
        });

        builder.Services.AddScoped(provider =>
        {
            AuthorizationMessageHandler handler = provider.GetRequiredService<AuthorizationMessageHandler>()
                .ConfigureHandler(authorizedUrls: [apiBase.ToString()], scopes: [scope]);
            handler.InnerHandler = new HttpClientHandler();
            return new HttpClient(handler) { BaseAddress = apiBase };
        });
    }

    /// <summary>Debug builds configured with <c>Auth:Mode = Dev</c> sign in as a persona of the local dev host instead.</summary>
    private static bool TryAddDevSignIn(WebAssemblyHostBuilder builder, Uri apiBase)
    {
#if DEBUG
        if (builder.Configuration["Auth:Mode"] == "Dev")
        {
            Development.DevSignIn.Add(builder.Services, apiBase);
            return true;
        }
#endif
        return false;
    }
}
