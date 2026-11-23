-- Stores a board's preferences and audits the change. Pass @IsArchived = 1 to soft-delete the board.
CREATE PROCEDURE kb.usp_UpdateBoard
    @BoardId            uniqueidentifier,
    @Name               nvarchar(200),
    @ReadyListCap       smallint,
    @ParkingReviewWeeks tinyint,
    @ArchiveAfterDays   smallint,
    @StandupTimeLocal   time(0),
    @TimeZoneId         varchar(64),
    @AgentLimit         tinyint,
    @AiEnabled          bit,
    @AiTokenBudget      int,
    @IsArchived         bit,
    @ActorId            uniqueidentifier,
    @NowUtc             datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    UPDATE kb.Board
    SET [Name] = @Name, ReadyListCap = @ReadyListCap, ParkingReviewWeeks = @ParkingReviewWeeks,
        ArchiveAfterDays = @ArchiveAfterDays, StandupTimeLocal = @StandupTimeLocal, TimeZoneId = @TimeZoneId,
        AgentConcurrencyLimit = @AgentLimit, AiEnabled = @AiEnabled, AiMonthlyTokenBudget = @AiTokenBudget,
        IsArchived = @IsArchived
    WHERE BoardId = @BoardId;

    INSERT kb.AuditEvent (BoardId, EventType, NewValue, ActorId, OccurredAtUtc)
    VALUES (@BoardId, IIF(@IsArchived = 1, 'board.deleted', 'board.settings'),
            (SELECT @Name AS [name], @ReadyListCap AS readyListCap, @ParkingReviewWeeks AS parkingReviewWeeks,
                    @ArchiveAfterDays AS archiveAfterDays, @StandupTimeLocal AS standupTimeLocal, @TimeZoneId AS timeZoneId,
                    @AgentLimit AS agentConcurrencyLimit, @AiEnabled AS aiEnabled, @AiTokenBudget AS aiMonthlyTokenBudget
             FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
            @ActorId, @NowUtc);

    COMMIT;
END;
