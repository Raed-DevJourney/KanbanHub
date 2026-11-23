using Azure.Core;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data;

/// <summary>
/// Connects with an Entra access token from <see cref="TokenCredential"/> (a managed identity in Azure,
/// the Azure CLI locally), so the connection string holds no credentials (specification section 13.4).
/// </summary>
public sealed class EntraSqlConnectionFactory(string connectionString, TokenCredential credential) : ISqlConnectionFactory
{
    private static readonly string[] Scopes = ["https://database.windows.net/.default"];

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString) { AccessTokenCallback = GetTokenAsync };
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    // Azure.Identity caches tokens, so asking per connection is cheap.
    private async Task<SqlAuthenticationToken> GetTokenAsync(
        SqlAuthenticationParameters parameters, CancellationToken cancellationToken)
    {
        AccessToken token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
    }
}

/// <summary>
/// Connects with whatever the connection string says. For a local SQL Server or LocalDB
/// with integrated security; never for Azure SQL, which is Entra-only.
/// </summary>
public sealed class ConnectionStringSqlConnectionFactory(string connectionString) : ISqlConnectionFactory
{
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

/// <summary>The <c>Sql</c> configuration section, shared by every host.</summary>
public sealed record SqlOptions
{
    public const string Section = "Sql";

    /// <summary>Server and database only. With Entra authentication it carries no credentials.</summary>
    public string ConnectionString { get; init; } = "";

    /// <summary>"Entra" (the default, required for Azure SQL) or "ConnectionString" for a local SQL Server.</summary>
    public string Authentication { get; init; } = "Entra";

    public ISqlConnectionFactory CreateFactory(TokenCredential credential) =>
        string.Equals(Authentication, "ConnectionString", StringComparison.OrdinalIgnoreCase)
            ? new ConnectionStringSqlConnectionFactory(ConnectionString)
            : new EntraSqlConnectionFactory(ConnectionString, credential);
}
