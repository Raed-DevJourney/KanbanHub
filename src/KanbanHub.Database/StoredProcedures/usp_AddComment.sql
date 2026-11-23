-- Adds a comment to a card and queues the event that notifies people and reaches the linked issue.
-- A comment that came from GitHub carries its id there and is stored once, however often it is delivered.
-- Returns Added = 1, or 0 when that GitHub comment was stored before.
CREATE PROCEDURE kb.usp_AddComment
    @CommentId       uniqueidentifier,
    @CardId          uniqueidentifier,
    @Body            nvarchar(max),
    @ActorId         uniqueidentifier,
    @NowUtc          datetime2(3),
    @GitHubCommentId bigint = NULL,
    @ExternalAuthor  nvarchar(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF @GitHubCommentId IS NOT NULL
       AND EXISTS (SELECT 1 FROM kb.CardComment WITH (UPDLOCK, HOLDLOCK) WHERE GitHubCommentId = @GitHubCommentId)
    BEGIN
        COMMIT;
        SELECT Added = 0;
        RETURN;
    END;

    INSERT kb.CardComment (CommentId, CardId, Body, AuthorId, Origin, GitHubCommentId, ExternalAuthor, CreatedAtUtc)
    VALUES (@CommentId, @CardId, @Body, @ActorId, IIF(@GitHubCommentId IS NULL, 1, 2), @GitHubCommentId, @ExternalAuthor, @NowUtc);

    INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
    SELECT @CardId, c.BoardId, 'card.commented',
           (SELECT @CardId AS cardId, @CommentId AS commentId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), @NowUtc
    FROM kb.Card c
    WHERE c.CardId = @CardId;

    COMMIT;

    SELECT Added = 1;
END;
