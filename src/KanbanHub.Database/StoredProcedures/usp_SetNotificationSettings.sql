-- Stores a person's channel choice for one kind of notification.
CREATE PROCEDURE kb.usp_SetNotificationPreference
    @UserId uniqueidentifier,
    @Kind   varchar(40),
    @InApp  bit,
    @Email  bit
AS
BEGIN
    SET NOCOUNT ON;

    MERGE kb.NotificationPreference AS target
    USING (SELECT @UserId AS UserId, @Kind AS Kind) AS source ON target.UserId = source.UserId AND target.Kind = source.Kind
    WHEN MATCHED THEN UPDATE SET InApp = @InApp, Email = @Email
    WHEN NOT MATCHED THEN INSERT (UserId, Kind, InApp, Email) VALUES (@UserId, @Kind, @InApp, @Email);
END;
GO
-- Mutes or unmutes a card for one person.
CREATE PROCEDURE kb.usp_SetCardMute
    @UserId  uniqueidentifier,
    @CardId  uniqueidentifier,
    @IsMuted bit
AS
BEGIN
    SET NOCOUNT ON;

    DELETE kb.CardMute WHERE UserId = @UserId AND CardId = @CardId;
    IF @IsMuted = 1 INSERT kb.CardMute (UserId, CardId) VALUES (@UserId, @CardId);
END;
GO
-- Remembers who signed in, with the name and email their token carries.
CREATE PROCEDURE kb.usp_UpsertAppUser
    @UserId      uniqueidentifier,
    @DisplayName nvarchar(200),
    @Email       nvarchar(320),
    @NowUtc      datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    MERGE kb.AppUser AS target
    USING (SELECT @UserId AS UserId) AS source ON target.UserId = source.UserId
    WHEN MATCHED THEN UPDATE SET DisplayName = @DisplayName, Email = @Email, LastSeenAtUtc = @NowUtc
    WHEN NOT MATCHED THEN INSERT (UserId, DisplayName, Email, LastSeenAtUtc) VALUES (@UserId, @DisplayName, @Email, @NowUtc);
END;
GO
-- Marks outbox events as published. @IdsJson: [1, 2, 3].
CREATE PROCEDURE kb.usp_MarkOutboxDispatched
    @IdsJson nvarchar(max),
    @NowUtc  datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE o
    SET DispatchedAtUtc = @NowUtc
    FROM kb.Outbox o
    JOIN OPENJSON(@IdsJson) ids ON CAST(ids.[value] AS bigint) = o.OutboxId
    WHERE o.DispatchedAtUtc IS NULL;
END;
