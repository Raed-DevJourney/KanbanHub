-- Stores notifications and returns the ones that are new, so pushes and emails go out once.
-- @NotificationsJson: [{ "userId", "boardId", "cardId", "kind", "title", "detail", "dedupeKey", "showInInbox", "sendEmail" }].
CREATE PROCEDURE kb.usp_SaveNotifications
    @NotificationsJson nvarchar(max),
    @NowUtc            datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @New TABLE (UserId uniqueidentifier, DedupeKey varchar(100));

    INSERT kb.[Notification] (UserId, BoardId, CardId, Kind, Title, Detail, DedupeKey, ShowInInbox, CreatedAtUtc)
    OUTPUT inserted.UserId, inserted.DedupeKey INTO @New
    SELECT n.userId, n.boardId, n.cardId, n.kind, n.title, n.detail, n.dedupeKey, n.showInInbox, @NowUtc
    FROM OPENJSON(@NotificationsJson)
         WITH (userId uniqueidentifier, boardId uniqueidentifier, cardId uniqueidentifier, kind varchar(40),
               title nvarchar(400), detail nvarchar(500), dedupeKey varchar(100), showInInbox bit) n
    WHERE NOT EXISTS (SELECT 1 FROM kb.[Notification] existing
                      WHERE existing.UserId = n.userId AND existing.DedupeKey = n.dedupeKey);

    SELECT UserId, DedupeKey FROM @New;
END;
