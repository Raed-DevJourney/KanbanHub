-- Marks one notification of a person as read (or every one they can see now, when @NotificationId is NULL),
-- or snoozes one: it leaves the inbox and comes back unread at @SnoozedUntilUtc.
-- Returns how many notifications changed.
CREATE PROCEDURE kb.usp_UpdateNotifications
    @UserId          uniqueidentifier,
    @NotificationId  bigint,
    @MarkRead        bit,
    @SnoozedUntilUtc datetime2(3),
    @NowUtc          datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE kb.[Notification]
    SET ReadAtUtc       = IIF(@MarkRead = 1, COALESCE(ReadAtUtc, @NowUtc), NULL),
        SnoozedUntilUtc = IIF(@MarkRead = 1, SnoozedUntilUtc, @SnoozedUntilUtc)
    WHERE UserId = @UserId
      AND (NotificationId = @NotificationId
           OR (@NotificationId IS NULL AND (SnoozedUntilUtc IS NULL OR SnoozedUntilUtc <= @NowUtc)));

    SELECT Changed = @@ROWCOUNT;
END;
