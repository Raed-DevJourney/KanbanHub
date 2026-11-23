namespace KanbanHub.Domain.Flow;

/// <summary>Who asked for a move. Stored in <c>kb.CardMovement.Source</c>; every source passes the same gates (rule R11).</summary>
public enum MoveSource : byte
{
    User = 1,
    GitHub = 2,
    Agent = 3,
    Api = 4,
    Automation = 5
}
