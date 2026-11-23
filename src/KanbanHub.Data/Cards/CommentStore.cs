using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>Card comments: written through a procedure, read with plain SQL.</summary>
public sealed class CommentStore(ISqlConnectionFactory database) : ICommentStore
{
    private const string ListSql = """
        SELECT CommentId, AuthorId, Body, CreatedAtUtc, ExternalAuthor
        FROM kb.CardComment
        WHERE CardId = @CardId
        ORDER BY CreatedAtUtc, CommentId;
        """;

    public async Task<Guid?> AddAsync(CommentDraft comment, Stamp stamp, CancellationToken cancellationToken)
    {
        Guid commentId = Guid.NewGuid();
        await using SqlCommand command = Sql.Procedure("kb.usp_AddComment");
        command.Parameters.AddGuid("@CommentId", commentId);
        command.Parameters.AddGuid("@CardId", comment.CardId);
        command.Parameters.AddText("@Body", comment.Body);
        command.Parameters.AddStamp(stamp);
        command.Parameters.AddBigInt("@GitHubCommentId", comment.Source?.GitHubCommentId);
        command.Parameters.AddText("@ExternalAuthor", comment.Source?.AuthorLogin, size: 100);

        bool added = await database.ResultCodeAsync(command, cancellationToken) == 1;
        return added ? commentId : null;
    }

    public async Task<IReadOnlyList<CommentDto>> ListAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(ListSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.ListAsync(
            command, r => new CommentDto(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.UtcDateTime(3), r.StringOrNull(4)), cancellationToken);
    }
}
