-- Starts an agent session on a card, if the board has a free agent slot (section 10.2) and the card has no
-- open session. The board row is locked, so sessions started at the same moment take turns: the limit holds.
-- Returns ResultCode 0, 1 when the board is at its limit, or 2 when the card already has an open session.
CREATE PROCEDURE kb.usp_StartAgentDispatch
    @DispatchId       uniqueidentifier,
    @CardId           uniqueidentifier,
    @Agent            varchar(40),
    @TargetRepo       nvarchar(200),
    @BaseBranch       nvarchar(200),
    @Model            nvarchar(100),
    @InstructionsHash binary(32),
    @ActorId          uniqueidentifier,
    @NowUtc           datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @BoardId uniqueidentifier = (SELECT BoardId FROM kb.Card WHERE CardId = @CardId AND IsDeleted = 0);
    DECLARE @Limit tinyint = (SELECT AgentConcurrencyLimit FROM kb.Board WITH (UPDLOCK, HOLDLOCK) WHERE BoardId = @BoardId);

    IF EXISTS (SELECT 1 FROM kb.AgentDispatch WHERE CardId = @CardId AND [State] IN (1, 2))
    BEGIN
        SELECT ResultCode = 2;
        ROLLBACK; RETURN;
    END;

    IF (SELECT COUNT(*) FROM kb.AgentDispatch WHERE BoardId = @BoardId AND [State] IN (1, 2)) >= @Limit
    BEGIN
        SELECT ResultCode = 1;
        ROLLBACK; RETURN;
    END;

    INSERT kb.AgentDispatch (DispatchId, CardId, BoardId, Agent, TargetRepo, BaseBranch, Model, [State],
                             InstructionsHash, DispatchedBy, DispatchedAtUtc)
    VALUES (@DispatchId, @CardId, @BoardId, @Agent, @TargetRepo, @BaseBranch, @Model, 1,
            @InstructionsHash, @ActorId, @NowUtc);

    -- Who sent what to which agent (section 10.3). The instructions themselves are not kept, only their hash.
    INSERT kb.AuditEvent (BoardId, CardId, EventType, Field, NewValue, ActorId, OccurredAtUtc)
    VALUES (@BoardId, @CardId, 'agent.dispatched', @Agent,
            (SELECT @TargetRepo AS targetRepo, @BaseBranch AS baseBranch, @Model AS model,
                    CONVERT(varchar(64), @InstructionsHash, 2) AS instructionsHash
             FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
            @ActorId, @NowUtc);

    EXEC kb.usp_AnnounceCardChanged @CardId;   -- the card now shows "Agent working"

    COMMIT;

    SELECT ResultCode = 0;
END;
GO

-- Moves a session on: its pull request appeared, was merged or closed, the start failed, or a person ended it.
CREATE PROCEDURE kb.usp_UpdateAgentDispatch
    @DispatchId        uniqueidentifier,
    @State             tinyint,
    @PullRequestNumber int,
    @NowUtc            datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @CardId uniqueidentifier, @BoardId uniqueidentifier;

    UPDATE kb.AgentDispatch
    SET [State] = @State,
        PullRequestNumber = COALESCE(@PullRequestNumber, PullRequestNumber),
        EndedAtUtc = IIF(@State IN (1, 2), NULL, @NowUtc),
        @CardId = CardId, @BoardId = BoardId
    WHERE DispatchId = @DispatchId;

    IF @CardId IS NOT NULL
    BEGIN
        -- The audit trail names the person who dispatched: they answer for the session, whoever or whatever moved it on.
        INSERT kb.AuditEvent (BoardId, CardId, EventType, Field, NewValue, ActorId, OccurredAtUtc)
        SELECT @BoardId, @CardId, 'agent.session', d.Agent,
               CONCAT(CHOOSE(d.[State], N'working', N'in review', N'merged', N'closed', N'failed', N'ended'),
                      N' (pull request #' + CONVERT(nvarchar(12), d.PullRequestNumber) + N')'),
               d.DispatchedBy, @NowUtc
        FROM kb.AgentDispatch d
        WHERE d.DispatchId = @DispatchId;

        EXEC kb.usp_AnnounceCardChanged @CardId;
    END;

    COMMIT;
END;
GO

-- Connects a person's GitHub account, or replaces the refresh token GitHub just rotated.
CREATE PROCEDURE kb.usp_SaveGitHubUserLink
    @UserId             uniqueidentifier,
    @GitHubUserId       bigint,
    @Login              nvarchar(100),
    @RefreshTokenCipher varbinary(1024),
    @NowUtc             datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    UPDATE kb.GitHubUserLink WITH (UPDLOCK, HOLDLOCK)
    SET GitHubUserId = @GitHubUserId, [Login] = @Login, RefreshTokenCipher = @RefreshTokenCipher
    WHERE UserId = @UserId;

    IF @@ROWCOUNT = 0
        INSERT kb.GitHubUserLink (UserId, GitHubUserId, [Login], RefreshTokenCipher, ConnectedAtUtc)
        VALUES (@UserId, @GitHubUserId, @Login, @RefreshTokenCipher, @NowUtc);

    COMMIT;
END;
GO

-- Returns how many connections were removed (0 or 1).
CREATE PROCEDURE kb.usp_RemoveGitHubUserLink
    @UserId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    DELETE kb.GitHubUserLink WHERE UserId = @UserId;

    SELECT Removed = @@ROWCOUNT;
END;
GO

-- Remembers the state of an authorization that just started, and forgets the ones that ran out of time.
CREATE PROCEDURE kb.usp_SaveGitHubAuthState
    @State        varchar(64),
    @UserId       uniqueidentifier,
    @ExpiresAtUtc datetime2(3),
    @NowUtc       datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE kb.GitHubAuthState WHERE ExpiresAtUtc < @NowUtc;

    INSERT kb.GitHubAuthState ([State], UserId, ExpiresAtUtc)
    VALUES (@State, @UserId, @ExpiresAtUtc);
END;
GO

-- Uses up a state: it counts once, for the person who started the authorization, while it is fresh.
-- Returns Consumed = 1 when it did.
CREATE PROCEDURE kb.usp_ConsumeGitHubAuthState
    @State  varchar(64),
    @UserId uniqueidentifier,
    @NowUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE kb.GitHubAuthState
    WHERE [State] = @State AND UserId = @UserId AND ExpiresAtUtc >= @NowUtc;

    SELECT Consumed = @@ROWCOUNT;
END;
