-- Built-in board roles (specification section 7.2). The Permissions values are the flags of
-- KanbanHub.Domain.Security.BuiltInRoles; an integration test fails if the two ever differ.
MERGE kb.RoleDefinition AS target
USING (VALUES
    (1, N'Owner',          134217727),
    (2, N'Flow Manager',    97910769),
    (3, N'Member',          29679105),
    (4, N'Contributor',     29622785),
    (5, N'Viewer',          25165825),
    (6, N'Agent Operator',  31457281)
) AS source (RoleId, [Name], Permissions)
ON target.RoleId = source.RoleId
WHEN MATCHED THEN
    UPDATE SET [Name] = source.[Name], IsBuiltIn = 1, Permissions = source.Permissions
WHEN NOT MATCHED THEN
    INSERT (RoleId, [Name], IsBuiltIn, Permissions) VALUES (source.RoleId, source.[Name], 1, source.Permissions);
GO

-- The managed identity the function apps run as (specification section 13.4): a database user with no login and
-- no password, allowed to read, write and run the procedures, and nothing else. The pipeline passes the
-- identity's name and client id; with no name, as on a developer's machine, this does nothing.
--
-- The user is made from the client id (WITH SID) instead of FROM EXTERNAL PROVIDER, so the SQL server needs
-- no permission to read the directory. Names cannot be parameters in these statements, hence the dynamic SQL.
-- Both values come from the deployment's own outputs, never from a person; QUOTENAME and the uniqueidentifier
-- cast still make sure they are used as a name and an id only.
DECLARE @identityName sysname = N'$(AppIdentityName)';

IF @identityName <> N''
BEGIN
    DECLARE @user nvarchar(258) = QUOTENAME(@identityName);
    DECLARE @sid nvarchar(34) =
        CONVERT(nvarchar(34), CAST(CAST(N'$(AppIdentityClientId)' AS uniqueidentifier) AS varbinary(16)), 1);
    DECLARE @grant nvarchar(max) = N'';

    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = @identityName)
        SET @grant = N'CREATE USER ' + @user + N' WITH SID = ' + @sid + N', TYPE = E; ';

    SET @grant += N'ALTER ROLE db_datareader ADD MEMBER ' + @user + N'; '
                + N'ALTER ROLE db_datawriter ADD MEMBER ' + @user + N'; '
                + N'GRANT EXECUTE ON SCHEMA::kb TO ' + @user + N';';

    EXEC sys.sp_executesql @grant;
END;
