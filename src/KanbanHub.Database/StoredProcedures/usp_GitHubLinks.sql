-- Links a repository to a board, or changes how an existing link syncs.
-- The link is what lets changes made in GitHub reach the board, so the GitHub App's own principal gets the Member
-- role there unless it already holds one: it then edits, moves and blocks cards through the same rules as people.
-- Returns ResultCode 0, 4 when the repository feeds another board, or 6 when the board doesn't exist.
CREATE PROCEDURE kb.usp_LinkRepository
    @BoardId         uniqueidentifier,
    @RepositoryId    bigint,
    @InstallationId  bigint,
    @FullName        nvarchar(200),
    @SyncMode        tinyint,
    @LabelFilter     nvarchar(100),
    @SyncPrincipalId uniqueidentifier,
    @ActorId         uniqueidentifier,
    @NowUtc          datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM kb.Board WHERE BoardId = @BoardId AND IsArchived = 0)
    BEGIN
        SELECT ResultCode = 6;
        ROLLBACK; RETURN;
    END;

    IF EXISTS (SELECT 1 FROM kb.GitHubRepoLink WITH (UPDLOCK, HOLDLOCK) WHERE RepositoryId = @RepositoryId AND BoardId <> @BoardId)
    BEGIN
        SELECT ResultCode = 4;
        ROLLBACK; RETURN;
    END;

    UPDATE kb.GitHubRepoLink
    SET InstallationId = @InstallationId, FullName = @FullName, SyncMode = @SyncMode, LabelFilter = @LabelFilter
    WHERE RepositoryId = @RepositoryId;

    IF @@ROWCOUNT = 0
        INSERT kb.GitHubRepoLink (RepositoryId, BoardId, InstallationId, FullName, SyncMode, LabelFilter, CreatedAtUtc, CreatedBy)
        VALUES (@RepositoryId, @BoardId, @InstallationId, @FullName, @SyncMode, @LabelFilter, @NowUtc, @ActorId);

    IF NOT EXISTS (SELECT 1 FROM kb.RoleAssignment WHERE BoardId = @BoardId AND PrincipalId = @SyncPrincipalId)
        INSERT kb.RoleAssignment (RoleAssignmentId, BoardId, PrincipalId, PrincipalType, RoleId, CreatedAtUtc, CreatedBy)
        VALUES (NEWID(), @BoardId, @SyncPrincipalId, 3, 3, @NowUtc, @ActorId);   -- service principal, Member

    INSERT kb.AuditEvent (BoardId, EventType, Field, NewValue, ActorId, OccurredAtUtc)
    VALUES (@BoardId, 'github.repo.linked', @FullName, IIF(@SyncMode = 2, N'mirror', N'on demand'), @ActorId, @NowUtc);

    -- The board itself is the aggregate: the workers answer this event by importing the repository's issues.
    INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
    VALUES (@BoardId, @BoardId, 'github.repo.linked', (SELECT @RepositoryId AS repositoryId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), @NowUtc);

    COMMIT;

    SELECT ResultCode = 0;
END;
GO

-- Removes a repository's link and the issue links of its cards. The cards and the issues themselves stay as they are.
-- Returns how many repository links were removed (0 or 1).
CREATE PROCEDURE kb.usp_UnlinkRepository
    @BoardId      uniqueidentifier,
    @RepositoryId bigint,
    @ActorId      uniqueidentifier,
    @NowUtc       datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @FullName nvarchar(200) =
        (SELECT FullName FROM kb.GitHubRepoLink WHERE RepositoryId = @RepositoryId AND BoardId = @BoardId);

    DELETE kb.GitHubPullRequest WHERE RepositoryId = @RepositoryId;
    DELETE kb.GitHubIssueLink WHERE RepositoryId = @RepositoryId;
    DELETE kb.GitHubRepoLink WHERE RepositoryId = @RepositoryId AND BoardId = @BoardId;
    DECLARE @Removed int = @@ROWCOUNT;

    IF @Removed > 0
        INSERT kb.AuditEvent (BoardId, EventType, Field, ActorId, OccurredAtUtc)
        VALUES (@BoardId, 'github.repo.unlinked', @FullName, @ActorId, @NowUtc);

    IF @Removed = 0 ROLLBACK ELSE COMMIT;

    SELECT Removed = @Removed;
END;
GO

CREATE PROCEDURE kb.usp_RenameRepository
    @RepositoryId bigint,
    @FullName     nvarchar(200)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE kb.GitHubRepoLink SET FullName = @FullName WHERE RepositoryId = @RepositoryId;
END;
GO

-- Links a card to an issue. A card has one issue and an issue one card.
-- Returns ResultCode 0, or 4 when either side is linked already.
CREATE PROCEDURE kb.usp_LinkIssue
    @CardId         uniqueidentifier,
    @InstallationId bigint,
    @RepositoryId   bigint,
    @IssueNumber    int,
    @IssueNodeId    varchar(100),
    @NowUtc         datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF EXISTS (SELECT 1 FROM kb.GitHubIssueLink WITH (UPDLOCK, HOLDLOCK)
               WHERE CardId = @CardId OR (RepositoryId = @RepositoryId AND IssueNumber = @IssueNumber))
    BEGIN
        SELECT ResultCode = 4;
        ROLLBACK; RETURN;
    END;

    INSERT kb.GitHubIssueLink (CardId, InstallationId, RepositoryId, IssueNumber, IssueNodeId, LinkedAtUtc)
    VALUES (@CardId, @InstallationId, @RepositoryId, @IssueNumber, @IssueNodeId, @NowUtc);

    EXEC kb.usp_AnnounceCardChanged @CardId;   -- the card now shows its issue

    COMMIT;

    SELECT ResultCode = 0;
END;
GO

-- Returns how many links were removed (0 or 1).
CREATE PROCEDURE kb.usp_UnlinkIssue
    @CardId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DELETE kb.GitHubPullRequest WHERE CardId = @CardId;
    DELETE kb.GitHubIssueLink WHERE CardId = @CardId;
    DECLARE @Removed int = @@ROWCOUNT;

    IF @Removed > 0 EXEC kb.usp_AnnounceCardChanged @CardId;

    COMMIT;

    SELECT Removed = @Removed;
END;
GO

-- Queues the event that makes open boards refetch a card whose GitHub link changed in a way the card shows.
-- Runs inside the caller's transaction. The time is the database's: nothing but the sweeper's age check reads it.
CREATE PROCEDURE kb.usp_AnnounceCardChanged
    @CardId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
    SELECT c.CardId, c.BoardId, 'card.updated', (SELECT c.CardId AS cardId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), SYSUTCDATETIME()
    FROM kb.Card c
    WHERE c.CardId = @CardId;
END;
GO

-- What the sync remembers about a link between its runs. A new or cleared sync problem, and "closed in GitHub",
-- show on the card, so those changes are announced to the board; the status comment's id does not.
CREATE PROCEDURE kb.usp_SaveIssueLinkState
    @CardId          uniqueidentifier,
    @StatusCommentId bigint,
    @SyncProblem     nvarchar(400),
    @ClosedInGitHub  bit
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @ShowsOnCard bit =
        (SELECT IIF(l.ClosedInGitHub <> @ClosedInGitHub OR NOT EXISTS (SELECT l.SyncProblem INTERSECT SELECT @SyncProblem), 1, 0)
         FROM kb.GitHubIssueLink l WITH (UPDLOCK)
         WHERE l.CardId = @CardId);

    UPDATE kb.GitHubIssueLink
    SET StatusCommentId = @StatusCommentId, SyncProblem = @SyncProblem, ClosedInGitHub = @ClosedInGitHub
    WHERE CardId = @CardId;

    IF @ShowsOnCard = 1 EXEC kb.usp_AnnounceCardChanged @CardId;

    COMMIT;
END;
GO

-- Adds a line to the sync ledger: which value of which field went which way, for echo suppression (section 9.3).
CREATE PROCEDURE kb.usp_RecordSync
    @CardId        uniqueidentifier,
    @Direction     tinyint,
    @Field         varchar(40),
    @ValueHash     binary(32),
    @SourceUpdated datetime2(3),
    @NowUtc        datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT kb.SyncLedger (CardId, Direction, Field, ValueHash, SourceUpdated, OccurredAtUtc)
    VALUES (@CardId, @Direction, @Field, @ValueHash, @SourceUpdated, @NowUtc);

    UPDATE kb.GitHubIssueLink
    SET LastOutboundAtUtc = IIF(@Direction = 1, @NowUtc, LastOutboundAtUtc),
        LastInboundAtUtc  = IIF(@Direction = 2, @NowUtc, LastInboundAtUtc)
    WHERE CardId = @CardId;
END;
GO

CREATE PROCEDURE kb.usp_MarkCommentSent
    @CommentId       uniqueidentifier,
    @GitHubCommentId bigint
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE kb.CardComment SET GitHubCommentId = @GitHubCommentId WHERE CommentId = @CommentId AND GitHubCommentId IS NULL;
END;
GO

-- Remembers a pull request that closes a card's issue: its latest commit, its check run, and whether it is
-- open, merged or closed. A change of state shows on the card, so the board is told.
CREATE PROCEDURE kb.usp_SavePullRequest
    @RepositoryId bigint,
    @PullNumber   int,
    @CardId       uniqueidentifier,
    @HeadSha      varchar(64),
    @CheckRunId   bigint,
    @State        tinyint,
    @IsDraft      bit
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @Known tinyint, @WasDraft bit;
    SELECT @Known = [State], @WasDraft = IsDraft
    FROM kb.GitHubPullRequest WITH (UPDLOCK, HOLDLOCK)
    WHERE RepositoryId = @RepositoryId AND PullNumber = @PullNumber AND CardId = @CardId;

    IF @Known IS NULL
        INSERT kb.GitHubPullRequest (RepositoryId, PullNumber, CardId, HeadSha, CheckRunId, [State], IsDraft)
        VALUES (@RepositoryId, @PullNumber, @CardId, @HeadSha, @CheckRunId, @State, @IsDraft);
    ELSE
        UPDATE kb.GitHubPullRequest
        SET HeadSha = @HeadSha, CheckRunId = @CheckRunId, [State] = @State, IsDraft = @IsDraft
        WHERE RepositoryId = @RepositoryId AND PullNumber = @PullNumber AND CardId = @CardId;

    IF @Known IS NULL OR @Known <> @State OR @WasDraft <> @IsDraft
        EXEC kb.usp_AnnounceCardChanged @CardId;

    COMMIT;
END;
GO

CREATE PROCEDURE kb.usp_RecordWebhookDelivery
    @DeliveryId varchar(100),
    @EventName  varchar(60),
    @Status     tinyint,
    @NowUtc     datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM kb.WebhookDelivery WITH (UPDLOCK, HOLDLOCK) WHERE DeliveryId = @DeliveryId)
        INSERT kb.WebhookDelivery (DeliveryId, EventName, [Status], HandledAtUtc)
        VALUES (@DeliveryId, @EventName, @Status, @NowUtc);
END;
