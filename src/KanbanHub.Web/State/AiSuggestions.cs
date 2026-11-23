using KanbanHub.Contracts;
using KanbanHub.Domain.Ai;
using KanbanHub.Web.Api;

namespace KanbanHub.Web.State;

/// <summary>How the app handles an answer from the AI: a suggestion to show, or a reason to tell the person.</summary>
public static class AiSuggestions
{
    /// <summary>The suggestion, or null after the reason there is none was shown.</summary>
    public static async Task<T?> OrExplainAsync<T>(this Task<ApiResult<T>> call, Notices notices)
        where T : class
    {
        object? answer = (await call).Value;
        if (answer is ApiProblem problem) notices.ShowProblem(problem);

        return answer as T;
    }

    /// <summary>A proposed task on one line, for the list a person reads before deciding.</summary>
    public static string Describe(ProposedTask task) =>
        $"{task.Title} (about {task.EstimatedDays:0.#} days): {string.Join("; ", task.AcceptanceCriteria)}";

    /// <summary>The card an insight cites, as the board knows it; just the number when the card left the board.</summary>
    public static string CiteCard(int number, IEnumerable<CardDto> cards) =>
        cards.FirstOrDefault(card => card.Number == number) is { } card ? $"#{number} {card.Title}" : $"#{number}";

    public static string KindOf(AiFeature kind) => kind switch
    {
        AiFeature.StandupBrief => "Standup brief",
        AiFeature.Bottleneck => "Constraint",
        AiFeature.WipAdvice => "WIP advice",
        _ => kind.ToString()
    };
}
