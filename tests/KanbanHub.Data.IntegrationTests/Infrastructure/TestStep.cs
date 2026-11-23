using KanbanHub.Domain.Flow;

namespace KanbanHub.Data.IntegrationTests.Infrastructure;

/// <summary>A step of the test board, with shorthand for its three sub-columns.</summary>
internal sealed record TestStep(Guid Id, string Name, int Order)
{
    public CardPosition Active => CardPosition.InStep(Id, Order, Location.Active);

    public CardPosition Done => CardPosition.InStep(Id, Order, Location.Done);

    public CardPosition Track => CardPosition.InStep(Id, Order, Location.Track);
}
