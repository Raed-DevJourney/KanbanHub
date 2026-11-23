-- Adds what one call to the model cost to the board's usage of that month.
CREATE PROCEDURE kb.usp_RecordAiUsage
    @BoardId uniqueidentifier,
    @Feature tinyint,
    @Tokens  int,
    @NowUtc  datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Month date = DATEFROMPARTS(YEAR(@NowUtc), MONTH(@NowUtc), 1);

    BEGIN TRANSACTION;

    UPDATE kb.AiUsage WITH (UPDLOCK, HOLDLOCK)
    SET Tokens = Tokens + @Tokens, Calls = Calls + 1
    WHERE BoardId = @BoardId AND UsageMonth = @Month AND Feature = @Feature;

    IF @@ROWCOUNT = 0
        INSERT kb.AiUsage (BoardId, UsageMonth, Feature, Tokens, Calls)
        VALUES (@BoardId, @Month, @Feature, @Tokens, 1);

    COMMIT;
END;
GO

-- Remembers that a card's aging alert was raised. Raising it twice changes nothing.
CREATE PROCEDURE kb.usp_MarkAgingAlerted
    @CardId       uniqueidentifier,
    @BoardId      uniqueidentifier,
    @AgeDays      decimal(9,1),
    @ExpectedDays decimal(9,1),
    @NowUtc       datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT kb.CardAgingAlert (CardId, BoardId, AgeDays, ExpectedDays, AlertedAtUtc)
    SELECT @CardId, @BoardId, @AgeDays, @ExpectedDays, @NowUtc
    WHERE NOT EXISTS (SELECT 1 FROM kb.CardAgingAlert WITH (UPDLOCK, HOLDLOCK) WHERE CardId = @CardId);
END;
GO

-- Makes the given insights the board's open ones of that kind: the ones they replace are superseded.
-- @InsightsJson: an array of insight objects; an empty array just withdraws the old ones.
CREATE PROCEDURE kb.usp_ReplaceInsights
    @BoardId      uniqueidentifier,
    @Kind         tinyint,
    @InsightsJson nvarchar(max),
    @NowUtc       datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    UPDATE kb.AiInsight
    SET [State] = 4, ResolvedAtUtc = @NowUtc
    WHERE BoardId = @BoardId AND Kind = @Kind AND [State] = 1;

    INSERT kb.AiInsight (BoardId, Kind, PayloadJson, [State], CreatedAtUtc)
    SELECT @BoardId, @Kind, [value], 1, @NowUtc
    FROM OPENJSON(@InsightsJson)
    ORDER BY CAST([key] AS int);

    COMMIT;
END;
GO

-- A person dismissed (2) or accepted (3) an open insight. Audited: an accepted suggestion is a decision someone made.
CREATE PROCEDURE kb.usp_ResolveInsight
    @InsightId bigint,
    @State     tinyint,
    @ActorId   uniqueidentifier,
    @NowUtc    datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @BoardId uniqueidentifier, @Title nvarchar(400);

    UPDATE kb.AiInsight
    SET [State] = @State, ResolvedBy = @ActorId, ResolvedAtUtc = @NowUtc,
        @BoardId = BoardId, @Title = JSON_VALUE(PayloadJson, '$.title')
    WHERE InsightId = @InsightId AND [State] = 1;

    IF @BoardId IS NOT NULL
        INSERT kb.AuditEvent (BoardId, EventType, NewValue, ActorId, OccurredAtUtc)
        VALUES (@BoardId, IIF(@State = 3, 'insight.accepted', 'insight.dismissed'), @Title, @ActorId, @NowUtc);

    COMMIT;
END;
