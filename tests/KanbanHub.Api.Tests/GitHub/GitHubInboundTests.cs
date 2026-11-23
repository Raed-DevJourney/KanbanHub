using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;
using KanbanHub.Domain.Security;
using KanbanHub.GitHub;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests.GitHub;

/// <summary>
/// What happens on a board when people act in GitHub (section 9.2): a signed webhook delivery goes through the
/// endpoint, the event router and the same use cases the UI uses, against the real tables.
/// </summary>
public class GitHubInboundTests(KanbanDatabase database)
{
    [Fact]
    public async Task A_title_edited_in_github_reaches_the_card_and_is_not_sent_back()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify);
        int sentBefore = github.GitHub.Updates.Count;
        (issue.Title, issue.Body, issue.UpdatedAt) = ("Checkout, simplified", "Fewer steps.", DateTimeOffset.UtcNow.AddSeconds(1));

        IActionResult accepted = await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Edited, issue));
        await board.PublishEventsOfAsync(card);   // the card's own "updated" event reaches the GitHub writer

        CardDetailDto after = await board.CardAsync(card);
        Assert.Equal(202, accepted.StatusOf());
        Assert.Equal(("Checkout, simplified", "Fewer steps."), (after.Card.Title, after.Description));
        Assert.Equal(sentBefore, github.GitHub.Updates.Count);
    }

    [Fact]
    public async Task An_edit_made_in_github_before_our_own_write_loses_to_it()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync();
        await board.Cards.Edit(Requests.From(board.Owner, new { title = "Renamed on the card" }, await board.ETagOfAsync(card)), card, Ct);
        await board.PublishEventsOfAsync(card);
        (issue.Title, issue.UpdatedAt) = ("An older rename from GitHub", DateTimeOffset.UtcNow.AddMinutes(-10));

        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Edited, issue));

        Assert.Equal("Renamed on the card", (await board.CardAsync(card)).Card.Title);
    }

    [Fact]
    public async Task A_delivery_is_handled_once_and_what_our_own_bot_caused_is_ignored()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync();
        string deliveryId = Guid.NewGuid().ToString();

        (issue.Title, issue.UpdatedAt) = ("First", DateTimeOffset.UtcNow.AddSeconds(1));
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Edited, issue), deliveryId);
        (issue.Title, issue.UpdatedAt) = ("Second", DateTimeOffset.UtcNow.AddSeconds(2));
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Edited, issue), deliveryId);                       // redelivered
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Edited, issue, sender: "KanbanHub[bot]"));        // our own echo

        Assert.Equal("First", (await board.CardAsync(card)).Card.Title);
        Assert.Equal(1, await StatusOfAsync(deliveryId));
    }

    [Fact]
    public async Task A_delivery_with_a_wrong_signature_or_without_githubs_headers_is_refused()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        byte[] body = "{}"u8.ToArray();

        IActionResult forged = await github.Board.Webhook.Receive(
            GitHubScenario.WebhookRequest(body, "sha256=" + new string('0', 64), "issues", Guid.NewGuid().ToString()), Ct);
        IActionResult anonymous = await github.Board.Webhook.Receive(GitHubScenario.WebhookRequest(body, "", "issues", "1"), Ct);

        forged.ProblemOf(401, "invalid-signature");
        anonymous.ProblemOf(401, "invalid-signature");
    }

    [Fact]
    public async Task A_label_that_would_break_wip_leaves_the_card_where_it_is_and_says_why()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        await board.SetLimitsAsync(board.Owner, new StepLimit(board.Implement.Id, 1, WipPolicy.Strict));
        await board.AddCardAtAsync(board.Implement, Location.Active);
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify, Location.Done);
        issue.Labels.Add("kanban:implement");

        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Labeled, issue, label: "kanban:implement"));

        CardDto after = (await board.CardAsync(card)).Card;
        Assert.Equal((board.Specify.Id, Location.Done), (after.StepId!.Value, after.Location));
        Assert.Equal(
            "Not moved to Implement Active: Implement is at its WIP limit (1 of 1). Help finish a card there first.",
            after.Issue!.SyncProblem);
        Assert.Equal(["kanban:done", "kanban:specify", "type:feature"], issue.Labels);   // the label that isn't true was taken off again
        Assert.Equal("card.updated", (await database.Connections.EventsOfCardAsync(card))[^1].Type);   // open boards are told to show the badge
        Assert.Contains("- **Not applied:** Not moved to Implement Active", GitHubScenario.StatusCommentOf(issue), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_label_moves_the_card_as_the_github_app_and_a_later_move_clears_an_old_refusal()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify, Location.Done);
        issue.Labels.Add("kanban:validate");
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Labeled, issue, label: "kanban:validate"));   // skips a step
        string? refusal = (await board.CardAsync(card)).Card.Issue!.SyncProblem;
        issue.Labels.Add("kanban:implement");

        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Labeled, issue, label: "kanban:implement"));
        await board.PublishEventsOfAsync(card);

        CardDto after = (await board.CardAsync(card)).Card;
        (byte Source, Guid Actor) lastMove = await LastMoveAsync(card);
        Assert.StartsWith("Not moved to Validate Active:", refusal, StringComparison.Ordinal);
        Assert.Equal((board.Implement.Id, Location.Active, null), (after.StepId!.Value, after.Location, after.Issue!.SyncProblem));
        Assert.Equal(((byte)MoveSource.GitHub, board.Service<SyncSettings>().PrincipalId), lastMove);
        Assert.Equal(["kanban:implement", "type:feature"], issue.Labels);
    }

    [Fact]
    public async Task The_blocked_priority_and_type_labels_set_the_cards_flag_and_fields()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Implement);

        await LabelAsync(github, issue, "kanban:blocked");
        CardDto blocked = (await board.CardAsync(card)).Card;
        await LabelAsync(github, issue, "priority:P2");
        await LabelAsync(github, issue, "type:bug");
        CardDto labeled = (await board.CardAsync(card)).Card;
        await UnlabelAsync(github, issue, "kanban:blocked");
        await UnlabelAsync(github, issue, "priority:P2");
        CardDto cleared = (await board.CardAsync(card)).Card;

        Assert.Equal((true, GitHubEdits.BlockedInGitHub, Location.Track), (blocked.IsBlocked, blocked.BlockedReason, blocked.Location));
        Assert.Equal(((byte?)2, CardType.Bug), (labeled.PriorityBucket, labeled.CardType));
        Assert.Equal((false, Location.Active, null), (cleared.IsBlocked, cleared.Location, cleared.PriorityBucket));
    }

    [Fact]
    public async Task Closing_an_issue_marks_the_card_for_the_team_and_reopening_clears_the_mark()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Implement);

        issue.State = IssueState.Completed;
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Closed, issue));
        await board.PublishEventsOfAsync(card);
        CardDto closed = (await board.CardAsync(card)).Card;
        string statusWhileClosed = GitHubScenario.StatusCommentOf(issue);
        issue.State = IssueState.Open;
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Reopened, issue));
        CardDto reopened = (await board.CardAsync(card)).Card;

        Assert.Equal((Location.Active, true), (closed.Location, closed.Issue!.ClosedInGitHub));   // a close is a request, not a move
        Assert.Contains("Closed here, still in flow there", statusWhileClosed, StringComparison.Ordinal);
        Assert.DoesNotContain("Closed here", GitHubScenario.StatusCommentOf(issue), StringComparison.Ordinal);
        Assert.DoesNotContain(github.GitHub.Updates, update => update.Change.State is not null);   // and the sync doesn't reopen it
        Assert.False(reopened.Issue!.ClosedInGitHub);
    }

    [Fact]
    public async Task Not_planned_is_left_to_the_team_where_the_github_app_may_not_cancel()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify);

        issue.State = IssueState.NotPlanned;
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Closed, issue));

        CardDto kept = (await board.CardAsync(card)).Card;
        Assert.Equal((Location.Active, true), (kept.Location, kept.Issue!.ClosedInGitHub));   // the app is a Member: it may not cancel
        Assert.Contains("Cancel the card here if that is right", kept.Issue.SyncProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_planned_cancels_the_card_where_the_github_app_was_given_a_role_that_may()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        await PromoteAppAsync(board, BuiltInRoles.FlowManager);   // before the app first acts: permissions are cached for a minute
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify);

        issue.State = IssueState.NotPlanned;
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Closed, issue));

        CardDetailDto canceled = await board.CardAsync(card);
        Assert.Equal((Location.Canceled, IssueClosure.NotPlannedReason), (canceled.Card.Location, canceled.CanceledReason));
        Assert.Null(canceled.Card.Issue!.SyncProblem);
    }

    [Fact]
    public async Task A_comment_made_in_github_joins_the_cards_conversation_once_and_is_not_echoed_back()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync();

        await github.DeliverAsync("issue_comment", github.CommentEvent(issue, commentId: 555, "Looks good to me"));
        await github.DeliverAsync("issue_comment", github.CommentEvent(issue, commentId: 555, "Looks good to me"));   // a second delivery of the same comment
        await board.PublishEventsOfAsync(card);

        var comments = (await board.Comments.List(Requests.From(board.Owner), card, Ct)).ValueOf<IReadOnlyList<CommentDto>>();
        CommentDto fromGitHub = Assert.Single(comments);
        Assert.Equal(("Looks good to me", "octocat", board.Service<SyncSettings>().PrincipalId),
            (fromGitHub.Body, fromGitHub.ExternalAuthor, fromGitHub.AuthorId));
        Assert.Single(issue.Comments);   // only the status comment: nothing was sent back
    }

    private static async Task LabelAsync(GitHubScenario github, StoredIssue issue, string label)
    {
        issue.Labels.Add(label);
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Labeled, issue, label));
    }

    private static async Task UnlabelAsync(GitHubScenario github, StoredIssue issue, string label)
    {
        issue.Labels.Remove(label);
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Unlabeled, issue, label));
    }

    /// <summary>Gives the GitHub App another role on the board, as an owner would under Members.</summary>
    private Task PromoteAppAsync(BoardScenario board, BoardRole role) =>
        database.Connections.ExecuteAsync(
            "UPDATE kb.RoleAssignment SET RoleId = @RoleId WHERE BoardId = @BoardId AND PrincipalId = @AppId;",
            new { RoleId = role.Id, board.BoardId, AppId = board.Service<SyncSettings>().PrincipalId });

    private Task<(byte Source, Guid Actor)> LastMoveAsync(Guid card) =>
        database.Connections.QuerySingleAsync(
            "SELECT TOP (1) Source, ActorId FROM kb.CardMovement WHERE CardId = @CardId ORDER BY MovementId DESC;",
            new { CardId = card }, row => (row.GetByte(0), row.GetGuid(1)));

    private async Task<int> StatusOfAsync(string deliveryId) =>
        await database.Connections.QuerySingleAsync(
            "SELECT [Status] FROM kb.WebhookDelivery WHERE DeliveryId = @DeliveryId;",
            new { DeliveryId = deliveryId }, row => (int)row.GetByte(0));
}

/// <summary>Slash commands in issue comments (section 8.3): the app does what is asked, or says why not, on the issue.</summary>
public class GitHubCommandTests(KanbanDatabase database)
{
    [Fact]
    public async Task Move_next_moves_the_card_and_answers_on_the_issue()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify);

        await github.DeliverAsync("issue_comment", github.CommentEvent(issue, 1, "/kanban move next"));

        Assert.Equal(Location.Done, (await board.CardAsync(card)).Card.Location);
        Assert.Contains("Moved to Specify Done.", issue.Comments.Values);
        Assert.Contains("kanban:done", issue.Labels);
        Assert.Empty((await board.Comments.List(Requests.From(board.Owner), card, Ct)).ValueOf<IReadOnlyList<CommentDto>>());   // a command is not a comment
    }

    [Fact]
    public async Task A_command_that_would_break_wip_is_refused_with_the_reason()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        await board.SetLimitsAsync(board.Owner, new StepLimit(board.Implement.Id, 1, WipPolicy.Strict));
        await board.AddCardAtAsync(board.Implement, Location.Active);
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify, Location.Done);

        await github.DeliverAsync("issue_comment", github.CommentEvent(issue, 1, "/kanban move next"));

        Assert.Equal(board.Specify.Id, (await board.CardAsync(card)).Card.StepId);
        Assert.Contains(
            "Not moved to Implement Active: Implement is at its WIP limit (1 of 1). Help finish a card there first.", issue.Comments.Values);
    }

    [Fact]
    public async Task Only_people_with_write_access_to_the_repository_may_give_commands()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify);

        await github.DeliverAsync("issue_comment", github.CommentEvent(issue, 1, "/kanban move next", association: "NONE"));

        Assert.Equal(Location.Active, (await board.CardAsync(card)).Card.Location);
        Assert.Contains(IssueComments.WriteAccessNeeded, issue.Comments.Values);
    }

    [Fact]
    public async Task Block_unblock_track_and_help_do_what_they_say()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Implement);

        await github.DeliverAsync("issue_comment", github.CommentEvent(issue, 1, "/kanban block Waiting for the vendor"));
        CardDto blocked = (await board.CardAsync(card)).Card;
        await github.DeliverAsync("issue_comment", github.CommentEvent(issue, 2, "/kanban unblock"));
        await github.DeliverAsync("issue_comment", github.CommentEvent(issue, 3, "/kanban track"));
        CardDto tracked = (await board.CardAsync(card)).Card;
        await github.DeliverAsync("issue_comment", github.CommentEvent(issue, 4, "/kanban dance"));

        Assert.Equal((true, "Waiting for the vendor", Location.Track), (blocked.IsBlocked, blocked.BlockedReason, blocked.Location));
        Assert.Equal((false, Location.Track), (tracked.IsBlocked, tracked.Location));
        Assert.Contains("Blocked.", issue.Comments.Values);
        Assert.Contains("Unblocked.", issue.Comments.Values);
        Assert.Contains("Moved to Implement Blocked.", issue.Comments.Values);
        Assert.Contains(SlashCommands.Help, issue.Comments.Values);
    }
}
