using System.Data;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data;

/// <summary>Creates commands. Text is always a constant with parameters; user input never reaches the SQL string.</summary>
internal static class Sql
{
    private const int TimeoutSeconds = 10;

    public static SqlCommand Procedure(string name) =>
        new(name) { CommandType = CommandType.StoredProcedure, CommandTimeout = TimeoutSeconds };

    public static SqlCommand Query(string text) => new(text) { CommandTimeout = TimeoutSeconds };
}

/// <summary>Runs a prepared command on a fresh connection, so callers hold no connection plumbing.</summary>
internal static class SqlRunner
{
    public static async Task ExecuteAsync(this ISqlConnectionFactory database, SqlCommand command, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await database.OpenAsync(cancellationToken);
        command.Connection = connection;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Maps every row of the first result set.</summary>
    public static async Task<List<T>> ListAsync<T>(
        this ISqlConnectionFactory database, SqlCommand command, Func<SqlDataReader, T> map, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await database.OpenAsync(cancellationToken);
        command.Connection = connection;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        List<T> rows = [];
        while (await reader.ReadAsync(cancellationToken)) rows.Add(map(reader));
        return rows;
    }

    /// <summary>Maps the first row, or returns null when there is none.</summary>
    public static async Task<T?> FirstOrDefaultAsync<T>(
        this ISqlConnectionFactory database, SqlCommand command, Func<SqlDataReader, T> map, CancellationToken cancellationToken)
        where T : class =>
        (await database.ListAsync(command, map, cancellationToken)).FirstOrDefault();

    /// <summary>Maps the one row a procedure always returns.</summary>
    public static async Task<T> SingleAsync<T>(
        this ISqlConnectionFactory database, SqlCommand command, Func<SqlDataReader, T> map, CancellationToken cancellationToken) =>
        (await database.ListAsync(command, map, cancellationToken)).Single();

    /// <summary>The <c>ResultCode</c> a write procedure reports in its first column.</summary>
    public static Task<int> ResultCodeAsync(this ISqlConnectionFactory database, SqlCommand command, CancellationToken cancellationToken) =>
        database.SingleAsync(command, reader => reader.GetInt32(0), cancellationToken);
}

/// <summary>The result codes shared by the write procedures.</summary>
internal static class ResultCodes
{
    public const int Ok = 0;
    public const int Conflict = 4;
    public const int NotFound = 6;
}

internal static class SqlReaderExtensions
{
    public static Guid? GuidOrNull(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    public static string? StringOrNull(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static byte? ByteOrNull(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetByte(ordinal);

    public static short? Int16OrNull(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);

    public static DateOnly? DateOrNull(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(ordinal));

    /// <summary><c>datetime2</c> columns hold UTC; say so, or JSON would emit an unspecified time.</summary>
    public static DateTime UtcDateTime(this SqlDataReader reader, int ordinal) =>
        DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
}
