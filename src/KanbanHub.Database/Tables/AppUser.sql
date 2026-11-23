-- People the app has seen sign in, so notifications can address them by name and email.
CREATE TABLE kb.AppUser (
    UserId        uniqueidentifier NOT NULL CONSTRAINT PK_AppUser PRIMARY KEY,   -- Entra object id
    DisplayName   nvarchar(200)    NOT NULL,
    Email         nvarchar(320)    NULL,
    LastSeenAtUtc datetime2(3)     NOT NULL
);
