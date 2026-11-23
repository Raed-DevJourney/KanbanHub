using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;
using KanbanHub.GitHub;
using KanbanHub.TestSupport;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests.GitHub;

/// <summary>Mirroring a repository into a backlog, the backfill and the reconciler (section 9.5).</summary>
public class GitHubMirrorTests(KanbanDatabase database)
{
    [Fact]
    public async Task Linking_a_mirror_imports_the_open_issues_page_by_page()
    {
        BoardScenario board = await StartAsync(database);
        RepoRef repo = (await board.GitHub.FindRepositoryAsync("contoso/checkout", Ct))!.Repo;
        board.GitHub.PageSize = 1;
        board.GitHub.Open(repo, "A bug to fix", "type:bug", "priority:P1", "good first issue");
        board.GitHub.Open(repo, "A feature to build");
        board.GitHub.Open(repo, "Already done").State = IssueState.Completed;

        await board.GitHubLinks.LinkRepository(Requests.From(board.Owner, new LinkRepositoryRequest("contoso/checkout", SyncMode.Mirror)), board.BoardId, Ct);
        await board.PublishEventsOfAsync(board.BoardId);   // "a repository was linked" reaches the backfill

        IReadOnlyList<CardDto> cards = (await board.SnapshotAsync()).Cards;
        CardDto bug = cards.Single(card => card.Title == "A bug to fix");
        StoredIssue bugIssue = board.GitHub.IssueOf(new IssueRef(repo.InstallationId, repo.RepositoryId, bug.Issue!.Number));
        Assert.Equal(["A bug to fix", "A feature to build"], cards.Select(card => card.Title).Order());
        Assert.Equal((Location.Backlog, CardType.Bug, (byte?)1), (bug.Location, bug.CardType, bug.PriorityBucket));
        Assert.Equal(["good first issue", "priority:P1", "type:bug"], bugIssue.Labels);
        Assert.Contains("- **Where:** Backlog", GitHubScenario.StatusCommentOf(bugIssue), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_issue_in_a_mirrored_repository_becomes_a_card_once_it_carries_the_mirrored_label()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database, SyncMode.Mirror, label: "kanban");
        BoardScenario board = github.Board;
        StoredIssue issue = github.GitHub.Open(github.Repo, "Passkeys at checkout");

        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Opened, issue));
        int withoutTheLabel = (await board.SnapshotAsync()).Cards.Count;
        issue.Labels.Add("kanban");
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Labeled, issue, label: "kanban"));
        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Edited, issue));   // already a card: nothing more is created

        CardDto card = Assert.Single((await board.SnapshotAsync()).Cards);
        Assert.Equal(0, withoutTheLabel);
        Assert.Equal(("Passkeys at checkout", issue.Reference.Number), (card.Title, card.Issue!.Number));
    }

    [Fact]
    public async Task On_demand_repositories_import_nothing_by_themselves()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        StoredIssue issue = github.GitHub.Open(github.Repo, "Not ours yet");

        await github.DeliverAsync("issues", github.IssueEvent(IssueChanged.Opened, issue));
        await github.Board.PublishEventsOfAsync(github.Board.BoardId);

        Assert.Empty((await github.Board.SnapshotAsync()).Cards);
    }

    [Fact]
    public async Task The_reconciler_applies_a_change_whose_webhook_never_arrived()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync();
        (issue.Title, issue.UpdatedAt) = ("Renamed while we weren't listening", DateTimeOffset.UtcNow.AddSeconds(1));
        RepoLink repository = (await board.Service<IRepoLinkStore>().FindAsync(github.Repository.RepositoryId, Ct))!;

        await board.Service<Backfill>().SyncAllAsync(repository, updatedSince: DateTimeOffset.UtcNow.AddHours(-25), Ct);

        Assert.Equal("Renamed while we weren't listening", (await board.CardAsync(card)).Card.Title);
    }
}

/// <summary>The "Kanban done rule" check run on pull requests (section 8.3).</summary>
public class GitHubCheckRunTests(KanbanDatabase database)
{
    [Fact]
    public async Task A_pull_request_that_closes_a_linked_issue_gets_the_check_and_ticking_the_rule_updates_it()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        BoardScenario board = github.Board;
        var rule = new SetDoneRuleRequest([new DoneRuleItemDraft("Code reviewed", IsRequired: true), new DoneRuleItemDraft("Docs updated", IsRequired: false)]);
        await board.Settings.SetDoneRule(Requests.From(board.Owner, rule), board.BoardId, board.Implement.Id, Ct);
        (Guid card, StoredIssue issue) = await github.LinkedCardAsync(board.Implement);
        Guid item = (await board.CardAsync(card)).Checklist.Single(line => line.Text == "Code reviewed").Id;

        await github.DeliverAsync("pull_request", github.PullRequestEvent("opened", number: 77, "abc123", $"Fixes #{issue.Reference.Number}"));
        (long Id, CheckRun Run) opened = (github.GitHub.CheckRuns.Single().Key, github.GitHub.CheckRuns.Single().Value);
        await board.Cards.ToggleChecklistItem(Requests.From(board.Owner), card, item, Ct);
        await board.PublishEventsOfAsync(card);
        CheckRun ticked = github.GitHub.CheckRuns[opened.Id];
        await github.DeliverAsync("pull_request", github.PullRequestEvent("synchronize", number: 77, "def456", $"Fixes #{issue.Reference.Number}"));

        Assert.Equal(("abc123", false), (opened.Run.HeadSha, opened.Run.Passed));
        Assert.Contains("- [ ] Code reviewed", opened.Run.Summary, StringComparison.Ordinal);
        Assert.Contains("- [ ] Docs updated (optional)", opened.Run.Summary, StringComparison.Ordinal);
        Assert.True(ticked.Passed);                                                             // the same check run, now green
        Assert.Equal(["abc123", "def456"], github.GitHub.CheckRuns.Values.Select(run => run.HeadSha).Order());   // a new commit gets its own
    }

    [Fact]
    public async Task A_pull_request_that_closes_nothing_linked_gets_no_check()
    {
        GitHubScenario github = await GitHubScenario.StartAsync(database);
        await github.LinkedCardAsync();

        await github.DeliverAsync("pull_request", github.PullRequestEvent("opened", number: 78, "abc123", "Tidies up. See #999."));
        await github.DeliverAsync("pull_request", github.PullRequestEvent("opened", number: 79, "abc124", "Fixes #999"));

        Assert.Empty(github.GitHub.CheckRuns);
    }
}
