-- A board's own colors (specification section 6). Only overrides are stored; every other token keeps its default.
CREATE TABLE kb.PaletteColor (
    BoardId  uniqueidentifier NOT NULL CONSTRAINT FK_Palette_Board REFERENCES kb.Board(BoardId),
    TokenKey varchar(40)      NOT NULL,   -- status.blocked, type.bug, ...
    Color    char(7)          NOT NULL,   -- #RRGGBB
    CONSTRAINT PK_PaletteColor PRIMARY KEY (BoardId, TokenKey)
);
