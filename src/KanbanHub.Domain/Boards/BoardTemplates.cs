namespace KanbanHub.Domain.Boards;

/// <summary>One step of a template: its name, flags and the book's starting WIP limit.</summary>
public sealed record class StepTemplate(string Name, string Color, int WipLimit)
{
    public bool HasTrack { get; init; } = true;

    public bool CountsParentsOnly { get; init; }

    public bool IsAgentEligible { get; init; }
}

/// <summary>A ready-made workflow a board can start from (FR-B02). The last step is the final one.</summary>
public sealed record class BoardTemplate(string Key, string DisplayName, IReadOnlyList<StepTemplate> Steps);

public static class BoardTemplates
{
    /// <summary>
    /// The book's quick-start board with its worked-example limits (2, 5, 3).
    /// Specify is the breakdown step, so it counts original items rather than their tasks.
    /// </summary>
    public static BoardTemplate QuickStart { get; } = new("quick-start", "Brechner quick-start",
    [
        new StepTemplate("Specify", "#7C3AED", WipLimit: 2) { CountsParentsOnly = true },
        new StepTemplate("Implement", "#2563EB", WipLimit: 5) { IsAgentEligible = true },
        new StepTemplate("Validate", "#0D9488", WipLimit: 3)
    ]);

    public static IReadOnlyList<BoardTemplate> All { get; } = [QuickStart];

    public static BoardTemplate? Find(string key) =>
        All.FirstOrDefault(template => string.Equals(template.Key, key, StringComparison.OrdinalIgnoreCase));
}
