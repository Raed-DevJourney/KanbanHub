using KanbanHub.Api.Composition;
using KanbanHub.Data;
using KanbanHub.DevHost;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("KanbanHub.DevHost has no real sign-in. It runs in Development only.");

string connectionString = builder.Configuration["Sql:ConnectionString"] ?? DevDatabase.DefaultConnectionString;

builder.Services.AddMvcCore();
builder.Services.AddKanbanHubApplication();
builder.Services.AddLocalEvents();
builder.Services.AddLocalGitHub(builder.Configuration);
builder.Services.AddLocalAi();
builder.Services.AddSingleton<ISqlConnectionFactory>(new ConnectionStringSqlConnectionFactory(connectionString));
builder.Services.AddSingleton<DevSeed>();
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => new Uri(origin).IsLoopback)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()   // the SignalR connection asks for it; origins are loopback only
    .WithExposedHeaders("ETag")));

WebApplication app = builder.Build();

DevDatabase.Deploy(connectionString);
await app.Services.GetRequiredService<DevSeed>().EnsureSampleBoardAsync(app.Lifetime.ApplicationStopping);
await app.RestoreGitHubAsync(app.Lifetime.ApplicationStopping);

app.UseCors();
app.UseMiddleware<DevSignIn>();
app.UseOutboxPublishing();
app.MapFunctions(typeof(KanbanHubServices).Assembly);
app.MapHub<BoardHub>(BoardHub.Path);
app.MapLocalGitHub();
app.MapLocalAi();

await app.RunAsync();
