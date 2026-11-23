namespace KanbanHub.Domain.Boards;

/// <summary>A step as the workflow editor submits it: an existing step by id, or a new one without.</summary>
public sealed record class StepDraft(Guid? Id, string Name, string Color)
{
    public string? Description { get; init; }

    public bool HasTrack { get; init; }

    public bool CountsParentsOnly { get; init; }

    public bool IsAgentEligible { get; init; }
}

/// <summary>The shape every workflow must have (FR-B03). The last step in the list is the final one.</summary>
public static class WorkflowRules
{
    public const int MinSteps = 2;
    public const int MaxSteps = 12;
    public const int MaxStepNameLength = 100;
    public const int MaxStepDescriptionLength = 1000;

    public static IReadOnlyList<string> FindProblems(IReadOnlyList<StepDraft> steps)
    {
        List<string> problems = [];
        if (steps.Count is < MinSteps or > MaxSteps)
            problems.Add($"A workflow has {MinSteps} to {MaxSteps} steps.");
        if (steps.Any(step => string.IsNullOrWhiteSpace(step.Name) || step.Name.Length > MaxStepNameLength))
            problems.Add($"Every step needs a name of 1 to {MaxStepNameLength} characters.");
        if (steps.Select(step => step.Name?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != steps.Count)
            problems.Add("Step names must be unique.");
        if (steps.Any(step => !HexColor.IsValid(step.Color)))
            problems.Add("Every step needs a color like #2563EB.");
        if (steps.Any(step => step.Description?.Length > MaxStepDescriptionLength))
            problems.Add($"A step description can be at most {MaxStepDescriptionLength} characters.");
        if (steps.Where(step => step.Id is not null).GroupBy(step => step.Id).Any(group => group.Count() > 1))
            problems.Add("A step can appear in the workflow only once.");
        return problems;
    }
}
