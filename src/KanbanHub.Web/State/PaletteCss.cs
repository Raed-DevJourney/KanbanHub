using KanbanHub.Contracts;

namespace KanbanHub.Web.State;

public static class PaletteCss
{
    /// <summary>The board's palette as CSS custom properties: "status.blocked" becomes --kh-status-blocked.</summary>
    public static string Of(BoardSnapshotDto board) =>
        string.Join(';', board.Palette.Select(color => $"--kh-{color.Key.Replace('.', '-')}:{color.Value}"));
}
