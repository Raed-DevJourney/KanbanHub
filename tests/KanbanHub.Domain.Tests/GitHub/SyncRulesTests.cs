using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Tests.GitHub;

/// <summary>A three-step board and a card on it, as the sync reads them.</summary>
internal static class Synced
{
    public static readonly SyncedStep Specify = new(Guid.NewGuid(), "Specify", 1);
    public static readonly SyncedStep Implement = new(Guid.NewGuid(), "Implement", 2) { HasBlockedColumn = true, WipCount = 3, WipLimit = 5 };
    public static readonly SyncedStep Validate = new(Guid.NewGuid(), "Validate", 3) { IsFinal = true };

    public static SyncedCard Card(SyncedStep? step, Location location) => new()
    {
        CardId = Guid.NewGuid(),
        BoardId = Guid.NewGuid(),
        Number = 12,
        RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
        Content = new CardContent { Title = "Checkout", CardType = CardType.TechDebt, PriorityBucket = 1 },
        Location = location,
        StepId = step?.Id,
        Steps = [Specify, Implement, Validate]
    };
}

public class KanbanLabelsTests
{
    [Fact]
    public void A_card_in_a_step_carries_its_place_priority_and_type()
    {
        SyncedCard card = Synced.Card(Synced.Implement, Location.Done) with { IsBlocked = true };

        Assert.Equal(
            ["kanban:blocked", "kanban:done", "kanban:implement", "priority:P1", "type:tech-debt"],
            KanbanLabels.For(card));
    }

    [Fact]
    public void A_backlog_card_carries_only_what_it_is_not_where_it_is()
    {
        SyncedCard card = Synced.Card(step: null, Location.Backlog) with { Content = new CardContent { Title = "Idea" } };

        Assert.Equal(["type:feature"], KanbanLabels.For(card));
    }

    [Fact]
    public void A_step_name_becomes_a_lowercase_label_without_spaces()
    {
        Assert.Equal("kanban:code-review", KanbanLabels.ForStep(" Code  Review! "));
    }

    [Fact]
    public void Merging_keeps_peoples_own_labels_and_replaces_the_maintained_ones()
    {
        IReadOnlyList<string> merged = KanbanLabels.Merge(
            ["good first issue", "kanban:specify", "priority:P0", "Type:Bug"], ["kanban:implement", "type:feature"]);

        Assert.Equal(["good first issue", "kanban:implement", "type:feature"], merged);
    }

    [Theory]
    [InlineData("priority:P0", 0)]
    [InlineData("PRIORITY:p3", 3)]
    public void A_priority_label_names_its_bucket(string label, int bucket)
    {
        Assert.Equal((byte)bucket, KanbanLabels.PriorityOf(label));
    }

    [Theory]
    [InlineData("priority:P4")]
    [InlineData("priority:high")]
    [InlineData("kanban:done")]
    public void Other_labels_name_no_bucket(string label)
    {
        Assert.Null(KanbanLabels.PriorityOf(label));
    }

    [Fact]
    public void A_type_label_names_its_card_type_and_every_type_has_one()
    {
        Assert.Equal(CardType.AgentTask, KanbanLabels.TypeOf("type:agent-task"));
        Assert.Null(KanbanLabels.TypeOf("type:unknown"));
        Assert.All(Enum.GetValues<CardType>(), type => Assert.Equal(type, KanbanLabels.TypeOf(KanbanLabels.ForType(type))));
    }

    [Fact]
    public void A_step_label_names_its_step_but_the_sub_column_labels_name_none()
    {
        SyncedStep[] steps = [Synced.Specify, Synced.Implement, Synced.Validate];

        Assert.Equal(Synced.Implement, KanbanLabels.StepOf("kanban:Implement", steps));
        Assert.Null(KanbanLabels.StepOf(KanbanLabels.Done, steps));
        Assert.Null(KanbanLabels.StepOf(KanbanLabels.Blocked, steps));
        Assert.Null(KanbanLabels.StepOf("kanban:ship", steps));
    }
}

public class SyncTextTests
{
    private static readonly IssueLink Link = new(Guid.NewGuid(), new IssueRef(1, 2, 42));

    [Fact]
    public void Card_text_loses_its_power_to_format_mention_or_reference()
    {
        string safe = MarkdownText.Inline("Ask @octocat about #12\n**now** [here](http://x)");

        Assert.Equal("Ask @\u200Boctocat about \\#\u200B12 \\*\\*now\\*\\* \\[here\\]\\(http://x\\)", safe);
    }

    [Fact]
    public void The_status_comment_says_where_the_card_is_the_steps_wip_and_why_it_is_blocked()
    {
        SyncedCard card = Synced.Card(Synced.Implement, Location.Track) with { IsBlocked = true, BlockedReason = "Waiting for @legal" };

        string comment = StatusComment.Render(card, Link, "https://kanbanhub.test/boards/1");

        Assert.StartsWith(StatusComment.Marker, comment, StringComparison.Ordinal);
        Assert.Contains("**KanbanHub** card #12", comment, StringComparison.Ordinal);
        Assert.Contains("- **Where:** Implement · Blocked", comment, StringComparison.Ordinal);
        Assert.Contains("- **WIP in Implement:** 3 of 5", comment, StringComparison.Ordinal);
        Assert.Contains("- **Blocked:** Waiting for @\u200Blegal", comment, StringComparison.Ordinal);
        Assert.Contains("[Open the board](https://kanbanhub.test/boards/1)", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_comment_explains_what_could_not_be_applied_and_a_close_that_waits()
    {
        IssueLink troubled = Link with { SyncProblem = "Implement is at its WIP limit (5 of 5)", ClosedInGitHub = true };

        string comment = StatusComment.Render(Synced.Card(Synced.Specify, Location.Done), troubled, boardUrl: null);

        Assert.Contains("- **Not applied:** Implement is at its WIP limit \\(5 of 5\\)", comment, StringComparison.Ordinal);
        Assert.Contains("Closed here, still in flow there", comment, StringComparison.Ordinal);
        Assert.Contains("- **WIP in Specify:** 0, no limit", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("Open the board", comment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Location.Backlog, "Backlog")]
    [InlineData(Location.Parked, "Parking lot")]
    [InlineData(Location.Canceled, "Canceled")]
    public void Outside_the_steps_the_status_comment_names_the_place(Location location, string place)
    {
        string comment = StatusComment.Render(Synced.Card(step: null, location), Link, boardUrl: null);

        Assert.Contains($"- **Where:** {place}", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("WIP", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_card_says_so()
    {
        string comment = StatusComment.Render(Synced.Card(Synced.Validate, Location.Done), Link, boardUrl: null);

        Assert.Contains("- **Where:** Validate · Done (finished)", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void The_done_rule_check_passes_when_every_required_item_is_ticked()
    {
        ChecklistLine[] checklist = [new("Code reviewed", true, true), new("Unit tests pass", true, false), new("Docs *updated*", false, false)];

        CheckRun notYet = DoneRuleSummary.For("abc123", 12, checklist);
        CheckRun met = DoneRuleSummary.For("abc123", 12, [checklist[0], checklist[1] with { IsChecked = true }, checklist[2]]);
        CheckRun noRule = DoneRuleSummary.For("abc123", 12, []);

        Assert.Equal((false, true, true), (notYet.Passed, met.Passed, noRule.Passed));
        Assert.Equal(
            "Done rule of KanbanHub card #12: not met yet.\n\n- [x] Code reviewed\n- [ ] Unit tests pass\n- [ ] Docs \\*updated\\* (optional)",
            notYet.Summary);
        Assert.Contains("no done rule", noRule.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Fixes #42", new[] { 42 })]
    [InlineData("closes: #7 and Resolved #8, also fixed #7", new[] { 7, 8 })]
    [InlineData("See #42, relates to #43", new int[0])]
    [InlineData(null, new int[0])]
    public void A_pull_request_closes_the_issues_its_keywords_name(string? body, int[] numbers)
    {
        Assert.Equal(numbers, ClosingKeywords.FindIssueNumbers(body));
    }
}

public class SlashCommandsTests
{
    [Theory]
    [InlineData("Looks good to me")]
    [InlineData("see /kanban move next")]
    public void A_comment_that_does_not_start_with_the_prefix_is_just_a_comment(string body)
    {
        Assert.Null(SlashCommands.TryParse(body));
    }

    [Fact]
    public void The_first_line_gives_the_command()
    {
        Assert.IsType<MoveNext>(SlashCommands.TryParse("  /kanban MOVE next\nthanks!")!.Value.Value);
        Assert.IsType<UnblockCard>(SlashCommands.TryParse("/kanban unblock")!.Value.Value);
        Assert.IsType<TrackCard>(SlashCommands.TryParse("/kanban track")!.Value.Value);
        Assert.Equal(new BlockCard("Waiting for Legal"), SlashCommands.TryParse("/kanban block Waiting for Legal")!.Value.Value);
    }

    [Theory]
    [InlineData("/kanban")]
    [InlineData("/kanban move back")]
    [InlineData("/kanban block")]
    [InlineData("/kanban unblock now")]
    [InlineData("/kanban dance")]
    public void Anything_else_after_the_prefix_asks_for_help(string body)
    {
        Assert.IsType<ShowHelp>(SlashCommands.TryParse(body)!.Value.Value);
    }
}

public class FieldSyncTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_value_is_pushed_only_when_it_differs_from_what_was_last_synced()
    {
        Assert.True(FieldSync.ShouldPush("New title", last: null));
        Assert.True(FieldSync.ShouldPush("New title", Entry(SyncDirection.Outbound, "Old title")));
        Assert.False(FieldSync.ShouldPush("Same title", Entry(SyncDirection.Outbound, "Same title")));
        Assert.False(FieldSync.ShouldPush("Came from GitHub", Entry(SyncDirection.Inbound, "Came from GitHub")));   // no echo back
    }

    [Fact]
    public void Line_endings_and_surrounding_space_do_not_make_a_difference()
    {
        Assert.False(FieldSync.ShouldPush("One\r\nTwo  ", Entry(SyncDirection.Inbound, "One\nTwo")));
        Assert.False(FieldSync.ShouldPush(null, Entry(SyncDirection.Inbound, "")));
    }

    [Fact]
    public void A_value_from_GitHub_is_applied_unless_it_is_an_echo_or_older_than_our_own_write()
    {
        LedgerEntry ours = Entry(SyncDirection.Outbound, "Ours");

        Assert.True(FieldSync.ShouldApply("Theirs", Noon, last: null));
        Assert.False(FieldSync.ShouldApply("Ours", Noon.AddMinutes(5), ours));            // the echo of our own write
        Assert.False(FieldSync.ShouldApply("Theirs", Noon.AddMinutes(-5), ours));          // made before we wrote: ours is newer
        Assert.True(FieldSync.ShouldApply("Theirs", Noon.AddMinutes(5), ours));            // made after: last writer wins
        Assert.True(FieldSync.ShouldApply("Theirs again", Noon.AddMinutes(-5), Entry(SyncDirection.Inbound, "Theirs")));
    }

    [Fact]
    public void An_issue_is_reopened_only_by_the_side_that_closed_it()
    {
        Assert.True(FieldSync.ShouldReopen(Entry(SyncDirection.Outbound, nameof(IssueState.Completed))));
        Assert.False(FieldSync.ShouldReopen(Entry(SyncDirection.Inbound, nameof(IssueState.Completed))));
        Assert.False(FieldSync.ShouldReopen(Entry(SyncDirection.Outbound, nameof(IssueState.Open))));
        Assert.False(FieldSync.ShouldReopen(null));
    }

    private static LedgerEntry Entry(SyncDirection direction, string value) =>
        new(direction, SyncFields.Title, FieldSync.Hash(value), Noon);
}

public class MoveTargetsTests
{
    [Fact]
    public void Next_is_one_column_forward_along_the_flow()
    {
        Assert.Equal((Synced.Specify.Id, Location.Active, "Specify Active"), Describe(MoveTargets.Next(Synced.Card(null, Location.Backlog))));
        Assert.Equal((Synced.Implement.Id, Location.Done, "Implement Done"), Describe(MoveTargets.Next(Synced.Card(Synced.Implement, Location.Active))));
        Assert.Equal((Synced.Validate.Id, Location.Active, "Validate Active"), Describe(MoveTargets.Next(Synced.Card(Synced.Implement, Location.Done))));
        Assert.Equal((Synced.Implement.Id, Location.Active, "Implement Active"), Describe(MoveTargets.Next(Synced.Card(Synced.Implement, Location.Track))));
    }

    [Theory]
    [InlineData(Location.Parked)]
    [InlineData(Location.Canceled)]
    public void A_card_out_of_the_flow_has_no_next_column(Location location)
    {
        Assert.Null(MoveTargets.Next(Synced.Card(step: null, location)));
    }

    [Fact]
    public void A_finished_card_has_no_next_column()
    {
        Assert.Null(MoveTargets.Next(Synced.Card(Synced.Validate, Location.Done)));
    }

    [Fact]
    public void Track_goes_to_the_steps_blocked_sub_column_where_it_has_one()
    {
        Assert.Equal("Implement Blocked", MoveTargets.Blocked(Synced.Card(Synced.Implement, Location.Active))?.Name);
        Assert.Null(MoveTargets.Blocked(Synced.Card(Synced.Specify, Location.Active)));
    }

    [Fact]
    public void Every_way_a_move_can_end_is_said_in_words()
    {
        var target = new MoveTarget(Synced.Implement.Id, Location.Active, "Implement Active");
        WipViolation[] full = [new(WipScope.Step, "Implement", 5, 5, WipPolicy.Strict)];

        Assert.Equal("Moved to Implement Active.", MoveReplies.Describe(new Moved(Guid.NewGuid(), Guid.NewGuid(), 1, [], []), target));
        Assert.Equal(
            "Not moved to Implement Active: Implement is at its WIP limit (5 of 5). Help finish a card there first.",
            MoveReplies.Describe(new WipLimitReached(full, []), target));
        Assert.Contains("needs a reason given in KanbanHub", MoveReplies.Describe(new OverrideReasonRequired(full), target), StringComparison.Ordinal);
        Assert.Contains("the done rule isn't met yet (Unit tests pass)", MoveReplies.Describe(new DoneRuleUnmet(["Unit tests pass"]), target), StringComparison.Ordinal);
        Assert.Contains("changed at the same moment", MoveReplies.Describe(new ConcurrencyConflict([]), target), StringComparison.Ordinal);
        Assert.EndsWith("That skips a step.", MoveReplies.Describe(new IllegalTransition("That skips a step."), target), StringComparison.Ordinal);
        Assert.Contains("lacks the CardMove permission", MoveReplies.Describe(new Forbidden(Permission.CardMove), target), StringComparison.Ordinal);
    }

    private static (Guid? StepId, Location Location, string Name) Describe(MoveTarget? target) =>
        (target!.StepId, target.Location, target.Name);
}

public class SyncModelTests
{
    private static readonly IssueSnapshot Open = new()
    {
        Issue = new IssueRef(1, 2, 3), NodeId = "I_1", Title = "An issue", Labels = ["bug", "Kanban"]
    };

    [Fact]
    public void A_mirror_takes_open_issues_and_respects_its_label()
    {
        var everything = new RepoLink(Guid.NewGuid(), new RepoRef(1, 2), "contoso/shop", SyncMode.Mirror, LabelFilter: null);

        Assert.True(everything.Mirrors(Open));
        Assert.True((everything with { LabelFilter = "kanban" }).Mirrors(Open));
        Assert.False((everything with { LabelFilter = "roadmap" }).Mirrors(Open));
        Assert.False(everything.Mirrors(Open with { State = IssueState.Completed }));
        Assert.False((everything with { Mode = SyncMode.OnDemand }).Mirrors(Open));
    }

    [Fact]
    public void The_app_knows_its_own_bot_and_acts_as_its_own_principal()
    {
        var settings = new SyncSettings(Guid.NewGuid(), "kanbanhub[bot]", new Uri("https://kanbanhub.test/"));
        Guid board = Guid.NewGuid();

        Assert.True(settings.IsOwnBot("KanbanHub[bot]"));
        Assert.False(settings.IsOwnBot("octocat"));
        Assert.Equal(settings.PrincipalId, settings.Principal.GetObjectId());
        Assert.Equal($"https://kanbanhub.test/boards/{board}", settings.BoardUrl(board));
        Assert.Null((settings with { WebBaseUrl = null }).BoardUrl(board));
    }

    [Fact]
    public void An_issue_change_with_nothing_in_it_is_empty()
    {
        Assert.True(new IssueChange().IsEmpty);
        Assert.False(new IssueChange { State = IssueState.Open }.IsEmpty);
    }
}
