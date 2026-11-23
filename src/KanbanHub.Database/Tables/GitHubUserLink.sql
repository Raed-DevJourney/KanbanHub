-- A person's connected GitHub account ("Connect GitHub"). Agent dispatch needs the person's own token;
-- only the refresh token is kept, encrypted with a Key Vault key the database never sees.
CREATE TABLE kb.GitHubUserLink (
    UserId             uniqueidentifier NOT NULL CONSTRAINT PK_GitHubUserLink PRIMARY KEY,   -- Entra object id
    GitHubUserId       bigint           NOT NULL,
    [Login]            nvarchar(100)    NOT NULL,
    RefreshTokenCipher varbinary(1024)  NOT NULL,
    ConnectedAtUtc     datetime2(3)     NOT NULL
);
GO
-- The "state" of a GitHub authorization that is under way: it ties the answer GitHub sends back to the
-- person who asked, once, for a few minutes.
CREATE TABLE kb.GitHubAuthState (
    [State]      varchar(64)      NOT NULL CONSTRAINT PK_GitHubAuthState PRIMARY KEY,
    UserId       uniqueidentifier NOT NULL,
    ExpiresAtUtc datetime2(3)     NOT NULL
);
