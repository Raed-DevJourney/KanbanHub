using KanbanHub.Messaging;
using KanbanHub.Workers;
using Microsoft.Azure.Functions.Worker.Builder;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services
    .AddKanbanHubMessaging(builder.Configuration, builder.Environment.IsDevelopment())
    .AddKanbanHubWorkers(builder.Configuration);

builder.Build().Run();
