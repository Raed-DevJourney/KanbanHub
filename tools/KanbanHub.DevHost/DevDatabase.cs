using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace KanbanHub.DevHost;

/// <summary>Brings the local development database up to the current schema on every start.</summary>
internal static class DevDatabase
{
    public const string DefaultConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=kanbanhub_dev;Integrated Security=true;TrustServerCertificate=true";

    public static void Deploy(string connectionString)
    {
        string database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        string dacpac = Path.Combine(AppContext.BaseDirectory, "KanbanHub.Database.dacpac");
        using DacPackage package = DacPackage.Load(dacpac);

        // The project targets Azure SQL; the local server is its on-premises sibling.
        var options = new DacDeployOptions { AllowIncompatiblePlatform = true, BlockOnPossibleDataLoss = false };
        new DacServices(connectionString).Deploy(package, database, upgradeExisting: true, options);
    }
}
