using Xunit;
using KanbanHub.Data;
using Microsoft.Data.SqlClient;

namespace KanbanHub.TestSupport;

/// <summary>
/// Minimal parameterized SQL for arranging and inspecting test data.
/// Each property of <c>args</c> becomes a parameter of the same name.
/// </summary>
public static class TestSql
{
    public static async Task ExecuteAsync(this ISqlConnectionFactory database, string sql, object? args = null)
    {
        await using SqlConnection connection = await database.OpenAsync(Cancellation);
        await using SqlCommand command = CreateCommand(connection, sql, args);
        await command.ExecuteNonQueryAsync(Cancellation);
    }

    public static async Task<List<T>> QueryAsync<T>(
        this ISqlConnectionFactory database, string sql, object? args, Func<SqlDataReader, T> map)
    {
        await using SqlConnection connection = await database.OpenAsync(Cancellation);
        await using SqlCommand command = CreateCommand(connection, sql, args);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(Cancellation);

        List<T> rows = [];
        while (await reader.ReadAsync(Cancellation)) rows.Add(map(reader));
        return rows;
    }

    public static async Task<T> QuerySingleAsync<T>(
        this ISqlConnectionFactory database, string sql, object? args, Func<SqlDataReader, T> map) =>
        (await database.QueryAsync(sql, args, map)).Single();

    public static Guid? GetNullableGuid(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    public static string? GetNullableString(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static DateTime? GetNullableDateTime(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, object? args)
    {
        var command = new SqlCommand(sql, connection);
        foreach (var property in args?.GetType().GetProperties() ?? [])
            command.Parameters.AddWithValue("@" + property.Name, property.GetValue(args) ?? DBNull.Value);
        return command;
    }
}
