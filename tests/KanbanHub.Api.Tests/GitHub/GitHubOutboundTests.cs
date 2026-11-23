using System.Security.Claims;
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

/// <summary>Linking boards to repositories and cards to issues (specification section 9.5).</summary>
public class GitHubLinkTests(KanbanDatabase database)
{
    [Fact]
    public async Task Linking_a_repository_is_for_owners_and_gives_the_github_app_the_member_role()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        ClaimsPrincipal manager = await board.UserWithAsync(BuiltInRoles.FlowManager);
        ClaimsPrincipal viewer = await board.UserWithAsync(BuiltInRoles.Viewer);

        IActionResult byAManager = await github.LinkRepositoryAsync(manager, "contoso/another", SyncMode.OnDemand);
        var members = (await board.Boards.ListMembers(Requests.From(board.Owner), board.BoardId, Ct)).ValueOf<IReadOnlyList<MemberAssignment>>();
        var listed = (await board.GitHubLinks.ListRepositories(Requests.From(viewer), board.BoardId, Ct)).ValueOf<IReadOnlyList<RepoLinkDto>>();

        byAManager.ProblemOf(403, "forbidden");
        Assert.Equal(("contoso/checkout", SyncMode.OnDemand), (github.Repository.FullName, github.Repository.Mode));
        Assert.Contains(new MemberAssignment(board.Service<SyncSettings>().PrincipalId, PrincipalType.ServicePrincipal, BuiltInRoles.Member.Id), members);
        Assert.Equal([github.Repository], listed);
    }

    [Fact]
    public async Task A_repository_must_be_named_properly_have_the_app_installed_and_feed_one_board_only()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        board.GitHub.NotInstalled.Add("contoso/private");
        var another = new CreateBoardRequest(board.WorkspaceId, "Another", "quick-start", "UTC");
        Guid otherBoard = (await board.Boards.CreateBoard(Requests.From(board.Owner, another), Ct)).ValueOf<BoardSummaryDto>().Id;

        IActionResult badName = await github.LinkRepositoryAsync(board.Owner, "not a repository", SyncMode.OnDemand);
        IActionResult notInstalled = await github.LinkRepositoryAsync(board.Owner, "contoso/private", SyncMode.OnDemand);
        IActionResult taken = await board.GitHubLinks.LinkRepository(
            Requests.From(board.Owner, new LinkRepositoryRequest("contoso/checkout", SyncMode.Mirror)), otherBoard, Ct);
        IActionResult changed = await github.LinkRepositoryAsync(board.Owner, "contoso/checkout", SyncMode.Mirror, label: "roadmap");

        Assert.Equal("Name the repository as owner/name.", badName.ProblemOf(422, "validation-failed").Detail);
        Assert.Contains("isn't installed on contoso/private", notInstalled.ProblemOf(422, "validation-failed").Detail, StringComparison.Ordinal);
        Assert.Contains("already feeds another board", taken.ProblemOf(422, "validation-failed").Detail, StringComparison.Ordinal);
        Assert.Equal((SyncMode.Mirror, "roadmap"), (changed.ValueOf<RepoLinkDto>().Mode, changed.ValueOf<RepoLinkDto>().LabelFilter));
    }

    [Fact]
    public async Task Unlinking_a_repository_unlinks_its_cards_and_leaves_cards_and_issues_as_they_are()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync();

        IActionResult unlinked = await board.GitHubLinks.UnlinkRepository(Requests.From(board.Owner), board.BoardId, github.Repository.RepositoryId, Ct);
        IActionResult again = await board.GitHubLinks.UnlinkRepository(Requests.From(board.Owner), board.BoardId, github.Repository.RepositoryId, Ct);

        Assert.Equal(204, unlinked.StatusOf());
        again.ProblemOf(404, "not-found");
        Assert.Null((await board.CardAsync(card)).Card.Issue);
        Assert.Equal("Checkout", issue.Title);
    }

    [Fact]
    public async Task Creating_an_issue_from_a_card_gives_it_the_cards_text_labels_and_one_status_comment()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        Guid card = await board.AddCardAsync(new CardContent { Title = "Checkout", Description = "As a shopper…", CardType = CardType.Bug, PriorityBucket = 0 });
        await board.WalkToAsync(card, board.Implement, Location.Active);

        IActionResult linked = await github.LinkIssueAsync(board.Owner, card, issueNumber: null);

        StoredIssue issue = await github.IssueOfAsync(card);
        GitHubIssueDto onCard = (await board.CardAsync(card)).Card.Issue!;
        string status = GitHubScenario.StatusCommentOf(issue);
        Assert.Equal(204, linked.StatusOf());
        Assert.Equal(("Checkout", "As a shopper…", IssueState.Open), (issue.Title, issue.Body, issue.State));
        Assert.Equal(["kanban:implement", "priority:P0", "type:bug"], issue.Labels);
        Assert.Contains("- **Where:** Implement · Active", status, StringComparison.Ordinal);
        Assert.Contains("- **WIP in Implement:** 1 of 5", status, StringComparison.Ordinal);
        Assert.Contains($"https://kanbanhub.test/boards/{board.BoardId}", status, StringComparison.Ordinal);
        Assert.Equal(("contoso/checkout", issue.Reference.Number, $"https://github.com/contoso/checkout/issues/{issue.Reference.Number}"),
            (onCard.Repository, onCard.Number, onCard.Url));
    }

    [Fact]
    public async Task Linking_an_existing_issue_leaves_both_titles_alone_until_one_side_changes()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        StoredIssue issue = github.GitHub.Open(github.Repo, "Issue title", "help wanted");
        Guid card = await board.AddCardAsync(title: "Card title");

        IActionResult linked = await github.LinkIssueAsync(board.Owner, card, issue.Reference.Number);
        (string Issue, string Card) afterLinking = (issue.Title, (await board.CardAsync(card)).Card.Title);
        await board.Cards.Edit(Requests.From(board.Owner, new { title = "Renamed on the card" }, await board.ETagOfAsync(card)), card, Ct);
        await board.PublishEventsOfAsync(card);

        Assert.Equal(204, linked.StatusOf());
        Assert.Equal(("Issue title", "Card title"), afterLinking);
        Assert.Equal(["help wanted", "type:feature"], issue.Labels);
        Assert.Equal("Renamed on the card", issue.Title);
    }

    [Fact]
    public async Task A_card_links_to_one_issue_of_a_repository_its_board_is_linked_to()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync();
        Guid other = await board.AddCardAsync();
        ClaimsPrincipal contributor = await board.UserWithAsync(BuiltInRoles.Contributor);

        (await github.LinkIssueAsync(board.Owner, card, issueNumber: null)).ProblemOf(422, "validation-failed");                       // linked already
        (await github.LinkIssueAsync(board.Owner, other, issue.Reference.Number)).ProblemOf(422, "validation-failed");                // that issue is taken
        (await github.LinkIssueAsync(board.Owner, other, issueNumber: 999)).ProblemOf(422, "validation-failed");                      // no such issue
        (await github.LinkIssueAsync(contributor, other, issueNumber: null)).ProblemOf(403, "forbidden");
        (await board.GitHubLinks.LinkIssue(Requests.From(board.Owner, new LinkIssueRequest(RepositoryId: 1)), other, Ct)).ProblemOf(422, "validation-failed");
        (await github.LinkIssueAsync(board.Owner, Guid.NewGuid(), issueNumber: null)).ProblemOf(404, "not-found");

        Assert.Equal(204, (await board.GitHubLinks.UnlinkIssue(Requests.From(board.Owner), card, Ct)).StatusOf());
        (await board.GitHubLinks.UnlinkIssue(Requests.From(board.Owner), card, Ct)).ProblemOf(404, "not-found");
    }
}

/// <summary>What the sync writes to GitHub when a card changes (section 9.3 and the surfaces of section 8.3).</summary>
public class GitHubOutboundTests(KanbanDatabase database)
{
    [Fact]
    public async Task Moving_and_blocking_a_card_updates_its_labels_and_edits_the_one_status_comment_in_place()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Implement);

        await board.MustMoveAsync(card, board.Implement, Location.Done);
        await board.PublishEventsOfAsync(card);
        string[] whenDone = [.. issue.Labels];
        await board.Cards.Block(Requests.From(board.Owner, new Blocker("Waiting for @legal", BlockerCategory.External)), card, Ct);
        await board.PublishEventsOfAsync(card);

        Assert.Equal(["kanban:done", "kanban:implement", "type:feature"], whenDone);
        Assert.Contains("kanban:blocked", issue.Labels);
        string status = Assert.Single(issue.Comments.Values);
        Assert.Contains("- **Where:** Implement · Done", status, StringComparison.Ordinal);
        Assert.Contains("- **Blocked:** Waiting for @​legal", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_comment_someone_deleted_on_github_is_written_again()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify);
        issue.Comments.Clear();

        await board.MustMoveAsync(card, board.Specify, Location.Done);
        await board.PublishEventsOfAsync(card);

        Assert.Contains("- **Where:** Specify · Done", GitHubScenario.StatusCommentOf(issue), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finishing_a_card_closes_its_issue_and_canceling_one_closes_it_as_not_planned()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid finished, StoredIssue finishedIssue) = await github.LinkedCardAsync(board.Validate, title: "Shipped");
        (Guid dropped, StoredIssue droppedIssue) = await github.LinkedCardAsync(board.Specify, title: "Dropped");

        await board.MustMoveAsync(finished, board.Validate, Location.Done);
        await board.Flow.Cancel(
            Requests.From(board.Owner, new CancelCardRequest("No longer needed"), await board.ETagOfAsync(dropped), Guid.NewGuid()), dropped, Ct);
        await board.PublishEventsOfAsync(finished);
        await board.PublishEventsOfAsync(dropped);

        Assert.Equal((IssueState.Completed, IssueState.NotPlanned), (finishedIssue.State, droppedIssue.State));
        Assert.Contains("Validate · Done (finished)", GitHubScenario.StatusCommentOf(finishedIssue), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comment_written_on_the_card_reaches_the_issue_once_with_its_authors_name()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        await board.Identity.Me(Requests.From(board.Owner), Ct);   // signing in is when the name is learned
        Guid card = await board.AddCardAsync();
        await board.Comments.Add(Requests.From(board.Owner, new AddCommentRequest("Written before the link")), card, Ct);
        Assert.Equal(204, (await github.LinkIssueAsync(board.Owner, card, issueNumber: null)).StatusOf());
        StoredIssue issue = await github.IssueOfAsync(card);

        await board.Comments.Add(Requests.From(board.Owner, new AddCommentRequest("Ship it")), card, Ct);
        await board.PublishEventsOfAsync(card);
        await board.PublishEventsOfAsync(card);

        string[] comments = [.. issue.Comments.Values.Where(comment => !comment.StartsWith(StatusComment.Marker, StringComparison.Ordinal))];
        Assert.Equal(["**Test user** wrote in KanbanHub:\n\nShip it"], comments);
    }

    [Fact]
    public async Task Sync_now_needs_its_permission_and_sends_the_cards_state_again()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Specify);
        ClaimsPrincipal member = await board.UserWithAsync(BuiltInRoles.Member);
        ClaimsPrincipal manager = await board.UserWithAsync(BuiltInRoles.FlowManager);
        issue.Labels.Remove("kanban:specify");   // someone tidied up in GitHub, and no webhook arrived

        IActionResult byAMember = await board.GitHubLinks.Sync(Requests.From(member), card, Ct);
        IActionResult byAManager = await board.GitHubLinks.Sync(Requests.From(manager), card, Ct);
        IActionResult unlinkedCard = await board.GitHubLinks.Sync(Requests.From(manager), await board.AddCardAsync(), Ct);

        byAMember.ProblemOf(403, "forbidden");
        Assert.Equal(204, byAManager.StatusOf());
        unlinkedCard.ProblemOf(404, "not-found");
        Assert.Contains("kanban:specify", issue.Labels);
        Assert.Equal("Checkout", github.GitHub.Updates[^1].Change.Title);
    }

    [Fact]
    public async Task When_github_says_to_wait_the_writer_stops_with_the_time_and_a_waiting_caller_is_told()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, _) = await github.LinkedCardAsync(board.Specify);
        Guid unlinked = await board.AddCardAsync();
        DateTimeOffset retryAt = DateTimeOffset.UtcNow.AddMinutes(30);
        github.GitHub.RateLimitedUntil = retryAt;

        await board.MustMoveAsync(card, board.Specify, Location.Done);
        var limited = await Assert.ThrowsAsync<GitHubRateLimitedException>(() => board.PublishEventsOfAsync(card));
        IActionResult whileWaiting = await github.LinkIssueAsync(board.Owner, unlinked, issueNumber: null);

        Assert.Equal(retryAt, limited.RetryAt);
        whileWaiting.ProblemOf(502, "github-unavailable");
    }
}
