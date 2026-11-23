using KanbanHub.Api.Auth;
using KanbanHub.Api.Composition;
using KanbanHub.Api.Http;
using Microsoft.Azure.Functions.Worker.Builder;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Order matters: failures anywhere below become problem details; nothing runs before the caller is known.
builder.UseMiddleware<UnhandledExceptionMiddleware>();
builder.UseMiddleware<EntraJwtMiddleware>();
builder.UseMiddleware<OutboxPublishMiddleware>();

builder.Services
    .AddKanbanHubHost(builder.Configuration, builder.Environment.IsDevelopment())
    .AddKanbanHubApplication();

builder.Build().Run();
