using KanbanHub.Web.Api;

namespace KanbanHub.Web.State;

/// <summary>What every settings form does after its save call: say how it went and show the board as it is now.</summary>
public sealed class SettingsSaver(BoardStore store, Notices notices)
{
    /// <summary>Returns true when the server accepted the change.</summary>
    public async Task<bool> CompleteAsync(Task<ApiResult<Done>> save, string successMessage)
    {
        ApiProblem? problem = (await save).Value as ApiProblem;
        if (problem is null) notices.Show(NoticeKind.Success, successMessage);
        else notices.ShowProblem(problem);

        await store.ReloadAsync(CancellationToken.None);
        return problem is null;
    }
}
