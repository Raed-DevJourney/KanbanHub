using System.Data;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Boards;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Agents;

/// <summary>Agent sessions in <c>kb.AgentDispatch</c>. The limit and the one-session-per-card rule live in the procedure.</summary>
public sealed class AgentSessionStore(ISqlConnectionFactory database) : IAgentSessionStore
{
    private const string Columns =
        "DispatchId, CardId, Agent, TargetRepo, BaseBranch, Model, [State], PullRequestNumber, DispatchedBy, DispatchedAtUtc";

    private const string OpenSql = $"SELECT {Columns} FROM kb.AgentDispatch WHERE CardId = @CardId AND [State] IN (1, 2);";

    private const string LatestSql =
        $"SELECT TOP (1) {Columns} FROM kb.AgentDispatch WHERE CardId = @CardId ORDER BY DispatchedAtUtc DESC, DispatchId;";

    private const string CapacitySql = """
        SELECT (SELECT COUNT(*) FROM kb.AgentDispatch d WHERE d.BoardId = b.BoardId AND d.[State] IN (1, 2)),
               b.AgentConcurrencyLimit
        FROM kb.Board b
        WHERE b.BoardId = @BoardId;
        """;

    private sealed record CapacityRow(int Open, byte Limit);

    public Task<AgentSession?> FindOpenAsync(Guid cardId, CancellationToken cancellationToken) =>
        FindAsync(OpenSql, cardId, cancellationToken);

    public Task<AgentSession?> FindLatestAsync(Guid cardId, CancellationToken cancellationToken) =>
        FindAsync(LatestSql, cardId, cancellationToken);

    public async Task<AgentCapacity> FindCapacityAsync(Guid boardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(CapacitySql);
        command.Parameters.AddGuid("@BoardId", boardId);

        CapacityRow? row = await database.FirstOrDefaultAsync(
            command, reader => new CapacityRow(reader.GetInt32(0), reader.GetByte(1)), cancellationToken);
        return row is null ? default : new AgentCapacity(row.Open, row.Limit);
    }

    public async Task<StartResult> StartAsync(
        AgentSession session, byte[] instructionsHash, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_StartAgentDispatch");
        command.Parameters.AddGuid("@DispatchId", session.Id);
        command.Parameters.AddGuid("@CardId", session.CardId);
        command.Parameters.AddValue("@Agent", SqlDbType.VarChar, session.Agent, size: 40);
        command.Parameters.AddText("@TargetRepo", session.TargetRepository, size: 200);
        command.Parameters.AddText("@BaseBranch", session.BaseBranch, size: 200);
        command.Parameters.AddText("@Model", session.Model, size: 100);
        command.Parameters.AddBinary("@InstructionsHash", instructionsHash, size: 32);
        command.Parameters.AddStamp(stamp);

        return (StartResult)await database.ResultCodeAsync(command, cancellationToken);
    }

    public async Task UpdateAsync(Guid sessionId, SessionProgress progress, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_UpdateAgentDispatch");
        command.Parameters.AddGuid("@DispatchId", sessionId);
        command.Parameters.AddTinyInt("@State", (byte)progress.State);
        command.Parameters.AddValue("@PullRequestNumber", SqlDbType.Int, progress.PullRequestNumber);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    private async Task<AgentSession?> FindAsync(string sql, Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(sql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.FirstOrDefaultAsync(command, Read, cancellationToken);
    }

    private static AgentSession Read(SqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        CardId = reader.GetGuid(1),
        Agent = reader.GetString(2),
        TargetRepository = reader.GetString(3),
        BaseBranch = reader.GetString(4),
        Model = reader.StringOrNull(5),
        State = (DispatchState)reader.GetByte(6),
        PullRequestNumber = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        DispatchedBy = reader.GetGuid(8),
        DispatchedAt = new DateTimeOffset(reader.UtcDateTime(9))
    };
}

/// <summary>
/// Connected GitHub accounts in <c>kb.GitHubUserLink</c> and authorizations under way in <c>kb.GitHubAuthState</c>.
/// The refresh token arrives here already encrypted; this store never sees it in the clear.
/// </summary>
public sealed class GitHubAccountStore(ISqlConnectionFactory database) : IGitHubAccountStore
{
    private sealed record Cipher(byte[] Bytes);

    public async Task<GitHubAccount?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query("SELECT UserId, GitHubUserId, [Login] FROM kb.GitHubUserLink WHERE UserId = @UserId;");
        command.Parameters.AddGuid("@UserId", userId);

        return await database.FirstOrDefaultAsync(
            command, reader => new GitHubAccount(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2)), cancellationToken);
    }

    public async Task<byte[]?> FindRefreshTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query("SELECT RefreshTokenCipher FROM kb.GitHubUserLink WHERE UserId = @UserId;");
        command.Parameters.AddGuid("@UserId", userId);

        Cipher? cipher = await database.FirstOrDefaultAsync(
            command, reader => new Cipher(reader.GetFieldValue<byte[]>(0)), cancellationToken);
        return cipher?.Bytes;
    }

    public async Task SaveAsync(
        GitHubAccount account, byte[] protectedRefreshToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SaveGitHubUserLink");
        command.Parameters.AddGuid("@UserId", account.UserId);
        command.Parameters.AddBigInt("@GitHubUserId", account.GitHubUserId);
        command.Parameters.AddText("@Login", account.Login, size: 100);
        command.Parameters.AddValue("@RefreshTokenCipher", SqlDbType.VarBinary, protectedRefreshToken, size: 1024);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task<bool> RemoveAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_RemoveGitHubUserLink");
        command.Parameters.AddGuid("@UserId", userId);

        return await database.ResultCodeAsync(command, cancellationToken) > 0;
    }

    public async Task RememberStateAsync(
        string state, Guid userId, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SaveGitHubAuthState");
        command.Parameters.AddValue("@State", SqlDbType.VarChar, state, size: 64);
        command.Parameters.AddGuid("@UserId", userId);
        command.Parameters.AddDateTime2("@ExpiresAtUtc", expiresAt.UtcDateTime);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task<bool> ConsumeStateAsync(string state, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_ConsumeGitHubAuthState");
        command.Parameters.AddValue("@State", SqlDbType.VarChar, state, size: 64);
        command.Parameters.AddGuid("@UserId", userId);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        return await database.ResultCodeAsync(command, cancellationToken) > 0;
    }
}
