using System.Data;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.GitHub;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.GitHub;

/// <summary>Which repository feeds which board, in <c>kb.GitHubRepoLink</c>.</summary>
public sealed class RepoLinkStore(ISqlConnectionFactory database) : IRepoLinkStore
{
    private const string Columns = "BoardId, InstallationId, RepositoryId, FullName, SyncMode, LabelFilter";

    public async Task<IReadOnlyList<RepoLink>> ListAsync(Guid boardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query($"SELECT {Columns} FROM kb.GitHubRepoLink WHERE BoardId = @BoardId ORDER BY FullName;");
        command.Parameters.AddGuid("@BoardId", boardId);

        return await database.ListAsync(command, Read, cancellationToken);
    }

    public async Task<IReadOnlyList<RepoLink>> ListAllAsync(CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query($"SELECT {Columns} FROM kb.GitHubRepoLink ORDER BY RepositoryId;");
        return await database.ListAsync(command, Read, cancellationToken);
    }

    public async Task<RepoLink?> FindAsync(long repositoryId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query($"SELECT {Columns} FROM kb.GitHubRepoLink WHERE RepositoryId = @RepositoryId;");
        command.Parameters.AddBigInt("@RepositoryId", repositoryId);

        return await database.FirstOrDefaultAsync(command, Read, cancellationToken);
    }

    public async Task<bool> SaveAsync(RepoLink link, Guid syncPrincipalId, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_LinkRepository");
        command.Parameters.AddGuid("@BoardId", link.BoardId);
        command.Parameters.AddBigInt("@RepositoryId", link.Repo.RepositoryId);
        command.Parameters.AddBigInt("@InstallationId", link.Repo.InstallationId);
        command.Parameters.AddText("@FullName", link.FullName, size: 200);
        command.Parameters.AddTinyInt("@SyncMode", (byte)link.Mode);
        command.Parameters.AddText("@LabelFilter", link.LabelFilter, size: 100);
        command.Parameters.AddGuid("@SyncPrincipalId", syncPrincipalId);
        command.Parameters.AddStamp(stamp);

        return await database.ResultCodeAsync(command, cancellationToken) == ResultCodes.Ok;
    }

    public async Task<bool> RemoveAsync(Guid boardId, long repositoryId, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_UnlinkRepository");
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddBigInt("@RepositoryId", repositoryId);
        command.Parameters.AddStamp(stamp);

        return await database.ResultCodeAsync(command, cancellationToken) > 0;
    }

    public async Task RenameAsync(long repositoryId, string fullName, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_RenameRepository");
        command.Parameters.AddBigInt("@RepositoryId", repositoryId);
        command.Parameters.AddText("@FullName", fullName, size: 200);

        await database.ExecuteAsync(command, cancellationToken);
    }

    private static RepoLink Read(SqlDataReader reader) => new(
        reader.GetGuid(0), new RepoRef(reader.GetInt64(1), reader.GetInt64(2)), reader.GetString(3),
        (SyncMode)reader.GetByte(4), reader.StringOrNull(5));
}

/// <summary>Which card is which issue, in <c>kb.GitHubIssueLink</c>.</summary>
public sealed class IssueLinkStore(ISqlConnectionFactory database) : IIssueLinkStore
{
    private const string Select = """
        SELECT CardId, InstallationId, RepositoryId, IssueNumber, StatusCommentId, SyncProblem, ClosedInGitHub
        FROM kb.GitHubIssueLink
        """;

    public async Task<IssueLink?> FindByCardAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query($"{Select} WHERE CardId = @CardId;");
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.FirstOrDefaultAsync(command, Read, cancellationToken);
    }

    public async Task<IssueLink?> FindByIssueAsync(long repositoryId, int issueNumber, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query($"{Select} WHERE RepositoryId = @RepositoryId AND IssueNumber = @IssueNumber;");
        command.Parameters.AddBigInt("@RepositoryId", repositoryId);
        command.Parameters.AddValue("@IssueNumber", SqlDbType.Int, issueNumber);

        return await database.FirstOrDefaultAsync(command, Read, cancellationToken);
    }

    public async Task<bool> LinkAsync(IssueLink link, string issueNodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_LinkIssue");
        command.Parameters.AddGuid("@CardId", link.CardId);
        command.Parameters.AddBigInt("@InstallationId", link.Issue.InstallationId);
        command.Parameters.AddBigInt("@RepositoryId", link.Issue.RepositoryId);
        command.Parameters.AddValue("@IssueNumber", SqlDbType.Int, link.Issue.Number);
        command.Parameters.AddValue("@IssueNodeId", SqlDbType.VarChar, issueNodeId, size: 100);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        return await database.ResultCodeAsync(command, cancellationToken) == ResultCodes.Ok;
    }

    public async Task<bool> UnlinkAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_UnlinkIssue");
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.ResultCodeAsync(command, cancellationToken) > 0;
    }

    public async Task SaveStateAsync(IssueLink link, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SaveIssueLinkState");
        command.Parameters.AddGuid("@CardId", link.CardId);
        command.Parameters.AddBigInt("@StatusCommentId", link.StatusCommentId);
        command.Parameters.AddText("@SyncProblem", link.SyncProblem, size: 400);
        command.Parameters.AddValue("@ClosedInGitHub", SqlDbType.Bit, link.ClosedInGitHub);

        await database.ExecuteAsync(command, cancellationToken);
    }

    private static IssueLink Read(SqlDataReader reader) =>
        new(reader.GetGuid(0), new IssueRef(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)))
        {
            StatusCommentId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            SyncProblem = reader.StringOrNull(5),
            ClosedInGitHub = reader.GetBoolean(6)
        };
}
