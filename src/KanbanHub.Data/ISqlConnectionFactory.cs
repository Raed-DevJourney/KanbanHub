using Microsoft.Data.SqlClient;

namespace KanbanHub.Data;

/// <summary>Opens connections to the KanbanHub database; how they authenticate is the implementation's business.</summary>
public interface ISqlConnectionFactory
{
    /// <summary>Returns an open connection that the caller disposes.</summary>
    Task<SqlConnection> OpenAsync(CancellationToken cancellationToken);
}
