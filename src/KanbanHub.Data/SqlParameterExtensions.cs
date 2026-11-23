using System.Data;
using System.Text.Json;
using KanbanHub.Domain.Boards;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data;

/// <summary>Typed parameter binding: explicit SQL types and sizes keep query plans stable, and null becomes DBNull.</summary>
internal static class SqlParameterExtensions
{
    private const int Max = -1;

    public static void AddValue(this SqlParameterCollection parameters, string name, SqlDbType type, object? value, int size = 0) =>
        parameters.Add(name, type, size).Value = value ?? DBNull.Value;

    public static void AddGuid(this SqlParameterCollection parameters, string name, Guid? value) =>
        parameters.AddValue(name, SqlDbType.UniqueIdentifier, value);

    public static void AddTinyInt(this SqlParameterCollection parameters, string name, byte? value) =>
        parameters.AddValue(name, SqlDbType.TinyInt, value);

    public static void AddBigInt(this SqlParameterCollection parameters, string name, long? value) =>
        parameters.AddValue(name, SqlDbType.BigInt, value);

    public static void AddBinary(this SqlParameterCollection parameters, string name, byte[] value, int size) =>
        parameters.AddValue(name, SqlDbType.Binary, value, size);

    /// <summary>An <c>nvarchar</c> of the given size; omit the size for <c>nvarchar(max)</c>.</summary>
    public static void AddText(this SqlParameterCollection parameters, string name, string? value, int size = Max) =>
        parameters.AddValue(name, SqlDbType.NVarChar, value, size);

    public static void AddDateTime2(this SqlParameterCollection parameters, string name, DateTime value) =>
        parameters.AddValue(name, SqlDbType.DateTime2, value);

    public static void AddDecimal(
        this SqlParameterCollection parameters, string name, decimal? value, byte precision, byte scale)
    {
        SqlParameter parameter = parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = value ?? (object)DBNull.Value;
    }

    /// <summary>A list or object as camelCase JSON, for procedures that read it with <c>OPENJSON</c>.</summary>
    public static void AddJson<T>(this SqlParameterCollection parameters, string name, T value) =>
        parameters.AddText(name, JsonSerializer.Serialize(value, JsonSerializerOptions.Web));

    /// <summary>The <c>@ActorId</c> and <c>@NowUtc</c> every write procedure takes.</summary>
    public static void AddStamp(this SqlParameterCollection parameters, Stamp stamp)
    {
        parameters.AddGuid("@ActorId", stamp.ActorId);
        parameters.AddDateTime2("@NowUtc", stamp.Now.UtcDateTime);
    }
}
