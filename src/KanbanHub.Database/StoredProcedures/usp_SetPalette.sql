-- Replaces a board's color overrides. @ColorsJson: [{ "key", "color" }]. Audited like WIP limits (section 6.4).
CREATE PROCEDURE kb.usp_SetPalette
    @BoardId    uniqueidentifier,
    @ColorsJson nvarchar(max),
    @ActorId    uniqueidentifier,
    @NowUtc     datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DELETE kb.PaletteColor WHERE BoardId = @BoardId;

    INSERT kb.PaletteColor (BoardId, TokenKey, Color)
    SELECT @BoardId, c.[key], c.color
    FROM OPENJSON(@ColorsJson) WITH ([key] varchar(40), color char(7)) c;

    INSERT kb.AuditEvent (BoardId, EventType, NewValue, ActorId, OccurredAtUtc)
    VALUES (@BoardId, 'palette', @ColorsJson, @ActorId, @NowUtc);

    COMMIT;
END;
