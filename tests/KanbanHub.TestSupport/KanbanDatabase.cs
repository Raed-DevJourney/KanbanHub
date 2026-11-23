using Xunit;
using KanbanHub.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace KanbanHub.TestSupport;

/// <summary>
/// One freshly deployed database per test run, built from the same dacpac that ships.
/// Tests stay independent by each creating their own board.
/// </summary>
public sealed class KanbanDatabase : IAsyncLifetime
{
    private const string DropSql = """
        DECLARE @Quoted nvarchar(300) = QUOTENAME(@Name);
        EXEC (N'ALTER DATABASE ' + @Quoted + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + @Quoted + N';');
        """;

    private readonly ISqlServerHost server = SqlServerHosts.Choose();
    private readonly string name = $"KanbanHub_Test_{Guid.NewGuid():N}";
    private string serverConnectionString = "";

    public ISqlConnectionFactory Connections { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        serverConnectionString = await server.StartAsync();
        DeploySchema();
        Connections = new ConnectionStringSqlConnectionFactory(ConnectionStringTo(name));
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await new ConnectionStringSqlConnectionFactory(ConnectionStringTo("master")).ExecuteAsync(DropSql, new { Name = name });
        await server.DisposeAsync();
    }

    private void DeploySchema()
    {
        string dacpac = Path.Combine(AppContext.BaseDirectory, "KanbanHub.Database.dacpac");
        using DacPackage package = DacPackage.Load(dacpac);

        // The project targets Azure SQL; the test server is its on-premises sibling.
        var options = new DacDeployOptions { AllowIncompatiblePlatform = true };
        new DacServices(ConnectionStringTo("master")).Deploy(package, name, upgradeExisting: false, options);
    }

    private string ConnectionStringTo(string database) =>
        new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = database,
            TrustServerCertificate = true,
            MaxPoolSize = 200   // the race test opens a hundred connections at once
        }.ConnectionString;
}
