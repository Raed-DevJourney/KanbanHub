using Xunit;
using Testcontainers.MsSql;

namespace KanbanHub.TestSupport;

/// <summary>A SQL Server 2025 instance the tests can create a database on.</summary>
internal interface ISqlServerHost : IAsyncDisposable
{
    /// <summary>Starts the server if needed and returns a connection string to it.</summary>
    Task<string> StartAsync();
}

/// <summary>A server that is already running: LocalDB, a CI service container, a shared dev instance.</summary>
internal sealed class ExternalSqlServer(string connectionString) : ISqlServerHost
{
    public Task<string> StartAsync() => Task.FromResult(connectionString);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A throwaway SQL Server container (needs Docker).</summary>
internal sealed class ContainerSqlServer : ISqlServerHost
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();

    public async Task<string> StartAsync()
    {
        await container.StartAsync(TestContext.Current.CancellationToken);
        return container.GetConnectionString();
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}

internal static class SqlServerHosts
{
    /// <summary>Set this to a connection string to run the tests against a server of your choice.</summary>
    public const string OverrideVariable = "KANBANHUB_TEST_SQL";

    private const string LocalDb = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true";

    /// <summary>The override if set; otherwise LocalDB on Windows and a container everywhere else.</summary>
    public static ISqlServerHost Choose()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } connectionString)
            return new ExternalSqlServer(connectionString);

        return OperatingSystem.IsWindows() ? new ExternalSqlServer(LocalDb) : new ContainerSqlServer();
    }
}
