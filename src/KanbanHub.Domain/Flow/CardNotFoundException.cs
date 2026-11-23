namespace KanbanHub.Domain.Flow;

public sealed class CardNotFoundException(Guid cardId) : Exception($"Card {cardId} was not found.")
{
    public Guid CardId { get; } = cardId;
}
