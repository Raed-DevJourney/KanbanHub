# Decisions

Choices made where the specification was silent, where its listings needed changing, or where the
engineering principles asked for this build (YAGNI, KISS, SOLID, Law of Demeter, short methods) pointed
a different way. Section numbers refer to the specification.

## Scope and structure

1. **Phases 0 and 1 were built together.** A scaffold alone has no code to review against the principles.
2. **A project is added by the phase that gives it code** (YAGNI). Workers, GitHub, Agents, AI, the E2E
   tests and `infra/` arrive with phases 4 to 8, so the solution never carries empty shells.
3. **Section 14.1 tables are in as listed; section 14.2 tables are designed with the feature that uses them.**
   Added so far: `DoneRule`, `DoneRuleItem`, `AuditEvent`, `CardComment`, `PaletteColor`.
4. **The full-text index on `kb.Card` is deferred** to the search feature (FR-V04). LocalDB has no full-text
   engine, so its tests will need a container or Azure SQL.
5. **A workflow is the board's own steps** (`SwimlaneId IS NULL`). Lane pipelines (FR-B05, release 2) come with lanes.
6. **Only the quick-start template exists.** FR-B02 puts the sustained-engineering template in the MVP, but it
   needs lanes with their own pipelines, which FR-B05 and section 20.1 put in release 2. The later release won.

## Domain

7. **Law of Demeter in `MoveCardHandler`.** The listing in 5.3 reaches through `card.Workflow`. Here
   `CardOnBoard.PlanMoveTo` returns a `MovePlan` that answers the questions itself. Effective permissions are
   fetched once instead of three times.
8. **Commands and requests use required init properties** instead of long positional parameter lists.
9. **One `Reason` per move.** FR-M02 demands a reason for backward moves and FR-C10 one for canceling, but the
   API body in 15.1 has only `overrideReason`. The command carries a single `Reason`: it explains a backward
   move or a cancellation, and it overrides a WIP limit when the caller holds `wip.override`.
10. **Backward means rework:** pulling into Active at an earlier step, or from Done back to Active in the same
    step. Dropping a card where it already is isn't a move and is rejected as illegal.
11. **`Transitions.Check` is the listing from 5.3, unchanged.** Its `(_, Parked)` rule lets a canceled card be
    parked, which is the only way back for a canceled card.
12. **Canceling is a move** to `Canceled` through the same procedure, so "every move goes through
    `usp_MoveCard`" holds literally. It skips `Transitions`, because FR-C10 allows it from anywhere.
13. **Retries are answered before the move is judged.** The handler in 5.3 would reject a retried move as
    illegal, because the card has already moved. `ICardRepository.FindAppliedMoveAsync` returns the original
    result first (rule R10).
14. **`Outcome<T>` is a second union** for every use case that isn't a move: the value, or `Forbidden`,
    `TenantRoleRequired`, `NotFound`, `Invalid`, `ConcurrencyConflict`. One switch in the API maps it to HTTP.
15. **`IBoardAuthorizer` has one method, `GetEffectiveAsync`;** `DenyUnlessAsync` is an extension over it.
16. **WIP math exists twice on purpose:** `StepWip` and `WipVerdicts` are the readable model (and compute the
    counts in the board snapshot); `kb.tvf_StepWipViolations` is the enforcement.
17. **WIP calculator:** the pace is the lowest step throughput (rate x people); each step needs `pace / rate`
    people and gets `ceil(1.5 x people)`, clamped to 1..99. It reproduces the book's 2, 5, 3.
18. **"Own cards" for a Contributor** means: whoever holds `backlog.create` may edit the cards they created.
19. **A Member's "configurable" backward move is off by default.** It is switched on by an extra role that
    holds `card.move.backward`; custom roles are data already, but have no endpoint yet.
20. **Pulling from the backlog doesn't require the card to be on the Ready list.** `kb.Card.IsReady` (added)
    marks the ordered Ready list; the cap is enforced when the list is reordered.

## `kb.usp_MoveCard` versus the listing in 5.4

None of these changes a rule in section 5.1; each closes a gap between the listing and the rules.

21. **The target step is validated** (same board, not retired, has Track when Track is the target). The listing
    would have moved a card onto another board's step. Result code 5 maps to `IllegalTransition`.
22. **The WIP check is decided by counting, not by location:** a move is checked when the target counts toward
    WIP and the source did not. This keeps R4 and R5 and also checks Done to Active in the final step.
23. **Parents-only family matching uses the same "counted" filter as the count.**
24. **Idempotency is checked under the card lock** and scoped to the card; a replay returns the recorded violations.
25. **`OverrideReason` is stored only when an "Allow with reason" limit was actually exceeded.**
26. **`@Reason` and `kb.CardMovement.Reason`** audit backward moves and cancellations; canceling also sets
    `kb.Card.CanceledReason`.
27. **The new row version is read inside the transaction**, so the returned ETag is the one this move produced.
28. **Helpers keep the procedure at one level of abstraction:** `tvf_StepWipViolations`, `usp_StartStepChecklist`,
    `usp_RecordMovement`, `usp_RollUpParent`.
29. **Done-rule checklist:** entering a step's Active from outside that step replaces the card's checklist for
    the step with the current rule, unchecked; moves inside the step keep the ticks. A rule for the card's type
    wins over the step's general rule.
30. **Breakdown (FR-C03):** tasks are created beside their parent and the family counts as one there. A parent
    that finishes the breakdown step takes its tasks along. When the last task leaves the step the parent
    becomes a roll-up; when every task is finished or canceled (at least one finished) it completes in the final
    step's Done. A family always locks parent first, so a task's move and its parent's cannot deadlock.

## API

31. **Reads return contracts directly; writes go through domain use cases.** Query classes in Data produce the
    DTOs; there is no second read model to map.
32. **Request bodies reuse plain domain records** (`CardContent`, `StepLimit`, `BacklogOrder`, ...) where a
    separate DTO would be a copy.
33. **`PATCH` is a JSON merge patch (RFC 7396)** for cards and board preferences. The use case loads the current
    state after authorizing, applies the patch and validates the result.
34. **One audit table.** `kb.AuditEvent` records card field changes and board-level changes (limits, done rules,
    members, workflow, palette, settings) instead of `CardFieldHistory` plus a history table per feature.
    `GET /audit` merges it with `kb.CardMovement`.
35. **Problem details follow section 5.5,** with two differences: `swarmCandidates` are card ids (the board
    already has their titles), and enums travel by name (`"Strict"`, not `"strict"`). Validation failures are 422.
36. **The board snapshot leaves out canceled cards and finished cards past the archive window** (FR-B08 without
    a job). It is not paged yet.
37. **Workflow editing retires steps rather than deleting them,** and refuses to retire a step that still holds
    cards, so movements keep pointing at real steps (FR-B10).
38. **The palette stores colors only** (status and type tokens). The WCAG contrast check of section 6.4 belongs to
    the palette editor: checked on the server with the spec's own defaults, it either rejects one of them
    (`#EA580C` against white and the dark neutral) or, with pure black allowed, can never fail. Indicator rules
    come with the indicators.
39. **The forecast counts leaf work:** a card that isn't broken down stands for its estimated task count (1 when
    not estimated), a broken-down parent is represented by its tasks, canceled work is left out. Only the
    `forecast` metric exists; the chart kinds of FR-P03 are release 2.
40. **Groups come from the token's `groups` claims.** The Microsoft Graph fallback for group overage (7.1) is not
    built: a user in more than about 200 groups would currently get only directly assigned roles.
41. **Permissions are cached in memory for 60 seconds per API instance,** as 7.2 specifies.
42. **Comments are stored as written.** Whatever renders Markdown must sanitize it; nothing renders it yet.

## Web app (Phase 3)

52. **A local dev host stands in for the Functions host.** Functions Core Tools are not on the development
    machine, so `tools/KanbanHub.DevHost` maps every HTTP-triggered function to the same route on a plain
    ASP.NET Core host, against a local SQL Server it deploys the dacpac to and seeds with one sample board. The
    endpoint classes, JSON options and database procedures are the real ones; only sign-in is replaced, by six
    made-up personas (one per role) named in an `X-Dev-User` header. It refuses to start outside Development and
    is not part of any deployment.
53. **The web app's dev sign-in exists in Debug builds only.** `Development/` is excluded from Release builds,
    which sign in with MSAL and nothing else. The MSAL path compiles but has not been run: it needs a tenant.
54. **No component library.** Native HTML elements and one stylesheet of design tokens (section 16.4) cover what
    the screens need, which avoids picking a library before its .NET 11 support is settled.
55. **The browser previews with the domain's own rules.** `BoardStore` reuses `Transitions`, `MovePlan` and
    `StepWip` to decide which columns open for a dragged card and to count WIP. It is a preview: every move is
    still sent to the server, whose answer wins (the card snaps back with the server's explanation).
56. **The board's components get the store as a cascading value,** so they re-render on any board, drag or filter
    change. Injecting it left components without changing parameters (backlog, parking lot) stale.
57. **After a successful move the board is reloaded.** That keeps WIP counts, roll-ups and other people's changes
    honest until real-time updates arrive in Phase 4.
58. **Keyboard moves walk the board's slots left to right** (backlog, each step's sub-columns, parking lot):
    Space picks up, the arrow keys choose, Space drops, Escape cancels, and a live region says where the card
    is and whether it can land. A few lines of script stop Space and the arrows from scrolling and return focus
    to the moved card.
59. **Chip text is black or white, whichever contrasts more** on the board's color for that chip. One of the two
    always reaches 4.58:1, so the palette editor shows the ratio rather than blocking a save.
60. **Added to the API for the UI:** `GET /boards/{id}/members`, `parkedFromStepId` on cards, enums by name and
    nulls omitted in every response, and the status precedence of section 6.1 as `CardStatuses` in the domain.
61. **The UI was verified by driving the running app** (real DOM drag and keyboard events against the dev host)
    and by unit tests of its logic. There is no Playwright suite yet (`KanbanHub.Web.E2E`), no axe run and no
    screen-reader pass, so WCAG 2.2 AA is designed for but not audited.
62. **Not built in Phase 3:** My Work, assignees and the just-in-time assignment prompt (they need an assignee
    API), the command palette, the Table view, saved views, aging dots from
    cycle-time percentiles (cards show days in column), the "at risk" status, bulk paste and CSV import,
    Markdown rendering (descriptions and comments show as plain text), list virtualization, the high-contrast
    and color-blind-safe modes, the density setting, a people picker (members are entered by object id) and the
    five-step onboarding wizard (there is a single create form).

## Real-time and notifications (Phase 4)

63. **Four packages beyond section 13.3 were added without asking first; say if any should go.**
    `Azure.Communication.Email` and `Microsoft.AspNetCore.SignalR.Client` have no alternative: the build prompt
    names Communication Services email, and a Blazor client needs the SignalR client. `...Extensions.Timer` runs
    the sweeper that 9.3 calls for. `Microsoft.Azure.SignalR.Management` is the one real choice (next item).
64. **SignalR goes through the Management SDK, not the `[SignalROutput]` binding of the sample in section 15.**
    The SDK is the library the listed `...Extensions.SignalRService` package itself depends on. Used directly it
    lets the connection be issued for the user id from the validated token, lets a connection join a board's
    group only after a `board.read` check, and keeps the consumers plain classes behind `IRealtimeGateway`,
    which the dev host and the tests replace. The listed extension package is therefore not referenced.
65. **A new project, `KanbanHub.Messaging`,** holds the three Azure adapters (SignalR, the topic, email) and
    their registration, shared by both function apps. It is not in the layout of 13.2; putting the adapters in
    the Domain would end its purity and in Data would mix them with SQL.
66. **A browser follows one board at a time through `POST /realtime/boards/{id}/join`.** Section 15.1 has
    negotiate add the user to every board group they may read; joining per board keeps the permission check at
    the moment of following and spares people with many boards pushes they don't look at. There is no "leave":
    the client ignores pushes for boards it no longer shows.
67. **The API tells watchers itself, then feeds the topic** (`BroadcastingPublisher`), so a move reaches other
    viewers without waiting for a consumer (FR-M06). Publishing runs inside the request, after the commit; if it
    fails the request still succeeded and the workers' sweeper repeats it within about a minute. The sweeper
    takes events older than 30 seconds. Duplicates are harmless: the topic deduplicates by outbox id, a
    notification is unique per person and event, and a repeated push only causes a refetch.
68. **A push is a hint, not a patch.** 16.2 describes small patches; the board instead refetches its snapshot,
    with changes that arrive during a refetch sharing one more. It is always consistent with roll-ups and WIP
    counts and costs one `GET` per change seen. Arriving at a board costs two (the snapshot, then one more once
    the connection follows the board, to cover the moment in between).
69. **Four kinds of notification are sent:** card blocked, card unblocked, WIP override used, new comment.
    Assignees and watchers don't exist yet, so "who is told" is the card's creator plus the board's Owners and
    Flow Managers (for comments: the creator plus earlier commenters). Managers who hold their role through a
    group are not found: only direct assignments are read. Not built from 11.3: mentions, assignment, aging,
    due dates, agent, sync and summary notifications, Teams, digest batching and quiet hours (email goes at once).
70. **Three tables beyond section 14:** `kb.AppUser` (name and email from the token, remembered on `GET /me`, so
    there is an address to send email to), `kb.CardMute`, and a `kb.Notification` shaped for the inbox
    (`DedupeKey`, `ShowInInbox`, `SnoozedUntilUtc`) rather than the one-line sketch in 14.2.
71. **Inbox rules:** a snoozed notification leaves the inbox and returns unread (1 to 168 hours); "mark all
    read" covers what is on screen, not what was snoozed; muting a card needs `board.read` on it; a WIP
    override ignores mutes and cannot be switched off in the app, as 11.3 requires.
72. **Standup mode** walks the steps from last to first, lists blocked cards first and then the longest in
    place, leaves out finished work, shows each step's room, and ends on "is anyone blocked?". "Aging" is three
    days in one place, a fixed stand-in until cycle-time percentiles exist. The AI brief (Phase 7) and the
    just-in-time assignment prompt (needs assignees) are not built. The link is shown to people who may move cards.
73. **Ticking a done-rule item now writes a `card.updated` event,** so watchers see it.
74. **The dev host does all three Azure jobs in one process:** its own SignalR hub, consumers run in-process
    after each request, email written to the log. Its hub trusts the user id a connection presents, like the
    rest of that host.
75. **What ran and what didn't.** Run: the rules, stores, endpoints and web logic (tests), and live updates,
    inbox, email preference, mute, reconnect and standup in a browser against the dev host (another tab showed
    a change 56 to 78 ms after the request). Never run, only compiled, because they need Azure or the Functions
    host: `SignalRServiceGateway`, `ServiceBusEventPublisher`, `AcsEmailSender`, `AddKanbanHubMessaging`,
    `OutboxPublishMiddleware` and the whole `KanbanHub.Workers` app. The topic, its `notifications`
    subscription and the role assignments are created by the infrastructure in Phase 8.

## Blocked cards (decided with the product owner, 2026-11-16)

76. **Blocking a card in Active moves it to its step's Track sub-column, which the board now calls "Blocked".**
    It stands out there and stops counting toward WIP, as journey 6 in section 3 describes (the acceptance
    scenario in 19.2 has a member make that move by hand; that still works). Unblocking brings the card back to
    Active when the step has room, the same WIP check as any Track to Active move (rule R5); when the step is
    full the card stays in Blocked, unblocked, until someone pulls it back. Cards blocked anywhere else, or in a
    step whose Blocked sub-column is switched off, keep their place and show the badge only.
77. **The flag and the move are two steps, not one transaction** (`CardBlocking`): the flag is set, then the move
    goes through `kb.usp_MoveCard`, so WIP is still enforced in one place. If the move fails, the card is blocked
    where it was. The permission to block covers the move; `card.move` is not needed for it.
78. **Left as they were:** a still-blocked card can be dragged back to Active (it keeps its badge), and a card
    dragged into Blocked without being flagged shows "Waiting". Cards blocked before this change stay where they
    were until they are blocked again.
79. **The audit trail never splits one instant across pages** (`TOP ... WITH TIES`). Blocking writes the flag and
    its move with the same timestamp, and the trail is paged by time; within an instant the move is listed as
    the newer entry.

## GitHub App (Phase 5)

80. **The sync's rules and use cases live in the Domain (`Domain/GitHub`); `KanbanHub.GitHub` holds only the
    adapters** (signature check, payload parser, token provider, REST client). Section 13.2 puts the event router
    and field mapper in `KanbanHub.GitHub`; keeping them in the Domain makes them testable without GitHub and lets
    the dev host and the tests swap the adapters. The shared composition (`AddKanbanHubCore`) moved to
    `KanbanHub.Data`, so the API, the workers and the dev host register the same use cases.
81. **The GitHub App acts as its own principal, with its own role** (section 3: service identities never borrow a
    person's role). Linking a repository gives it the Member role on the board, visible and changeable under
    Members. So GitHub can edit, move forward, block and comment, and cannot cancel, move backward or override a
    WIP limit unless an owner gives the app a role that can.
82. **A repository feeds one board,** so an issue in a mirrored repository has one place to go. Repositories are
    called by id (`repositories/{id}/...`, the form GitHub redirects to after a rename), so a rename doesn't
    break the sync. That addressing has only been exercised against a stand-in, not against GitHub.
83. **What section 9.4 maps and this phase syncs:** title, body, card type (`type:` label; org-level issue types
    are not used), priority bucket, step and sub-column labels, blocked, comments, closed and not planned.
    **Not synced:** assignees, tags (other labels are preserved, not imported), sub-issues, dependencies,
    milestones and target release, due dates and the Projects mirror, linked pull requests beyond the check run,
    and ticking done-rule items from GitHub evidence.
84. **Linking rewrites nobody's text.** Card and issue keep the title and body they have and count as in sync;
    from then on a change on either side reaches the other. Labels, state and the status comment are written at once.
85. **A close in GitHub is a request, never a move** ("request completion" of 9.4). The card is marked "closed in
    GitHub" and the team finishes it here, where the done rule applies. "Trust GitHub" (jump to final Done) is not
    built. "Not planned" cancels the card only when the app's role may cancel. A reopened issue clears the mark
    but doesn't move a finished or canceled card back; it leaves a note instead.
86. **Echo suppression has two layers:** events our own bot caused are dropped, and the ledger keeps a hash of the
    last value synced per field, in either direction, so the sync only ever sends what differs. A refused request
    from GitHub leaves a note on the link (the "Sync issue" badge), the label that isn't true is taken off again,
    and the status comment says why. Removing a place label in GitHub asks for nothing.
87. **Slash commands act as the app, for people GitHub says can write to the repository** (owner, member or
    collaborator on the comment). Mapping GitHub users to Entra users comes with "Connect GitHub" in Phase 6.
88. **The check run covers pull requests whose text says they close a linked issue** ("Fixes #42", same
    repository). It lists the done-rule items of the card's current step and fails until the required ones are
    ticked; a new commit gets a new check run.
89. **The backfill is not a Durable Functions orchestration.** The Durable extension pulls in
    `Microsoft.AspNetCore.WebUtilities` 2.3.0, which NuGet flags as vulnerable (NU1902, GHSA-6527-9fp8-r4x6) and
    which breaks the zero-warnings rule. The backfill runs page by page inside the GitHub writer function
    instead; if GitHub says to wait, the message is rescheduled and what was imported is not imported twice. Say
    if the Durable extension should be taken with that advisory suppressed. The nightly reconciler lists what
    changed in the last 25 hours in every linked repository.
90. **Queues:** deliveries wait on `github-inbound` (sessions per issue, duplicate detection by delivery id).
    Card events carry the card id as their session, and a `github` subscription of `card-events` forwards to the
    session queue `github-outbound`. A message that hits a rate limit is put back on its queue for the time
    GitHub names, under a new message id so duplicate detection lets it through. Phase 8 creates all of this.
91. **The webhook is at `/api/github/webhook`,** as sections 9.1 and 9.7 have it, not under `/v1` as the table in
    15.1 implies. Two secrets are accepted during a rotation. A bad signature is a 401 problem.
92. **Packages:** `Azure.Security.KeyVault.Keys` and `Microsoft.Extensions.Http.Resilience`, both from 13.3. No
    Octokit: the REST client is hand-written on `HttpClient`. `...Extensions.DurableTask` is not referenced
    (item 89). The tests stub `HttpMessageHandler` instead of adding WireMock.
93. **A stand-in for GitHub ships in `KanbanHub.GitHub`** (`InMemoryGitHub`) for the dev host and the tests. The
    dev host restores linked repositories and issues into it at startup, delivers webhooks in-process, and has
    `/dev/github` doors to look at it and to deliver unsigned payloads. Its consumers log and skip a failure
    instead of holding up the local outbox.
94. **Known gaps:** an issue deleted or transferred on GitHub makes the writer fail for that card until it is
    unlinked; installation events are ignored (a repository is resolved by name when it is linked, and there is
    no `kb.GitHubInstallation` table); an import racing a webhook for the same new issue can leave an unlinked
    card; a waiting caller gets a 502 when GitHub doesn't answer; unlinking the last repository leaves the app's
    role on the board.
95. **Schema beyond section 14:** `kb.GitHubRepoLink` (its shape was only sketched), `kb.GitHubPullRequest`,
    `kb.WebhookDelivery`, three columns on `kb.GitHubIssueLink` (`LinkedAtUtc`, `SyncProblem`, `ClosedInGitHub`)
    and `ExternalAuthor` on `kb.CardComment`.
96. **What ran and what didn't.** Run: the sync's rules and use cases against the real database with the
    stand-in (including both GitHub scenarios of 19.2), the REST client and token provider against a stubbed
    api.github.com, the signature check against the example in GitHub's documentation, and the whole flow in a
    browser against the dev host. Never run: anything against GitHub itself, signing in Key Vault, the Workers
    functions, Service Bus sessions and the rate-limit rescheduling.

## Agents (Phase 6)

97. **Everything that knows GitHub's preview agent APIs sits behind `IAgentDispatcher`** (`KanbanHub.Agents`,
    `CopilotDispatcher`), and the `Agents:Enabled` flag is off unless an environment sets it. With it off the API
    refuses dispatch and the card panel links to the issue on GitHub instead.
98. **Copilot only, in the issue's own repository.** The target repository is always the one the card's issue
    lives in (it is linked to the board, which is the allowlist of 10.3); pull requests are matched to cards by
    "Fixes #n" in the same repository. Not built: Claude and Codex, custom agents from the repository, the agent
    tasks API (10.5, R3), AI-drafted instructions (Phase 7), auto-move on merge, bulk dispatch, and the
    "agent PR opened" notification.
99. **Dispatch order:** permission, placement and guardrails, connected account, the "WIP first" pull through
    `MoveCardHandler` as the person, then `kb.usp_StartAgentDispatch` takes a slot under a lock on the board row,
    then GitHub is asked. A refusal or an outage marks the session failed, so a slot is never left taken.
100. **A session is open while it is working or in review;** merging or closing its pull request, ending it here,
     or unassigning the agent on GitHub (while it has no pull request yet) closes it. A draft pull request already
     counts as "in review". Merging moves nothing: moving the card stays a human action.
101. **"Connect GitHub" is the app's OAuth web flow with the callback on a web page** (`/github/callback`), because
     GitHub's redirect can't carry the Entra token; `GET /github/connect` returns the address as JSON. The state is
     random, stored per user, good once and for ten minutes. Only the refresh token is stored, encrypted with a
     Key Vault key (`GitHub:TokenKeyId`, RSA-OAEP-256); access tokens are kept in memory until five minutes before
     they expire. The GitHub App must keep "Expire user authorization tokens" on. Disconnecting forgets the token
     here and does not revoke the authorization on GitHub. Two instances renewing the same token at the same
     moment can make one of them ask the person to connect again.
102. **The `GraphQL-Features` header is the one section 10.4 gives.** GitHub's page today lists only
     `coding_agent_model_selection`; both values are sent.
103. **GitHub ignores assignees it can't assign without an error.** The adapter follows 10.4 (422 means "not
     enabled") and does not inspect the response, so a silently ignored assignment shows as "Agent working" until
     someone ends the session.
104. **Schema and settings beyond section 14:** `kb.GitHubAuthState`; `State` and `IsDraft` on
     `kb.GitHubPullRequest`; `BoardId`, `InstructionsHash`, `DispatchedBy` and timestamps on `kb.AgentDispatch`.
     New settings: `Agents:Enabled`, `GitHub:ClientSecret` (Key Vault reference), `GitHub:TokenKeyId`.
105. **Also changed:** the workflow editor now keeps and edits "open to agents" per step (it used to drop the
     flag on save), and the open card panel follows live changes to its card.
106. **What ran and what didn't.** Run: the rules, both adapters against recorded answers in GitHub's documented
     shape, every endpoint against the real database with stand-ins (including the 19.2 scenario "Send a card to
     Copilot"), and the whole flow in a browser against the dev host. Never run: anything against GitHub itself,
     Key Vault encryption, the Workers functions.

## AI (Phase 7)

107. **The features, their prompts and their grounding checks live in the Domain (`Domain/Ai`); `KanbanHub.AI`
     holds only the model adapter.** Section 13.2 puts all of it in `KanbanHub.AI`; as with the GitHub sync (item
     80), keeping the rules in the Domain makes them testable without a model. The Domain has no dependency on
     `Microsoft.Extensions.AI`: it asks through its own port, `IAiModel`, and hands the data over as an object.
108. **Packages:** `Microsoft.Extensions.AI` and `Azure.AI.OpenAI` (section 13.3), plus the bridge between them,
     `Microsoft.Extensions.AI.OpenAI`, which the product owner approved on 2026-11-16. The latest stable
     `Azure.AI.OpenAI` (2.1.0) resolves next to a much newer `OpenAI` library (2.14.0); the combination passes the
     adapter's contract tests against a stubbed endpoint and has not run against Azure.
109. **Screening is the deployment's own content filter** (decided with the product owner on 2026-11-16, instead
     of adding `Azure.AI.ContentSafety`). A prompt the filter stops counts as no answer. Answers are not screened
     separately. This is less than section 11.2 asks for.
110. **Built:** features 1 to 10 of section 11.1: standup brief, constraint finder, WIP advisor, aging and SLE
     alerts, forecast narrative, breakdown assistant, done-rule checker, duplicate finder, triage, agent
     instructions. **Not built:** the R3 features 11 (kaizen review) and 12 (ask the board).
111. **Suggestions fill forms; they are not stored and not applied.** The breakdown's acceptance criteria are
     shown but not saved on the child cards. The done-rule check is a button and always advisory: it does not run
     by itself before a move, and a board can't make it required. Triage is a button on a card, not automatic for
     new issues. Agent instructions are drafted on request, not pre-filled.
112. **Grounding:** the model answers with a C# record; cards and steps it cites must be in the data it was
     given; a number stated in free text must be one of the numbers supplied (rounding allowed); the constraint is
     found by rule and the model only words it; insights take card titles from the board. What the check can't
     catch: numbers written as words and claims without numbers. What the data lacks: similar past cards for the
     breakdown; check runs and comments for the done-rule check.
113. **Per-board opt-in, off by default; a monthly budget of 2,000,000 tokens by default** with a soft stop,
     counted per month and feature in `kb.AiUsage`. Not built: the alert at 80%, a usage dashboard beyond the
     settings tab, and a tenant-wide AI policy screen.
114. **Background work waits on one queue, `ai-jobs`** (section 13.1), run by one function, not on a topic with a
     subscription per feature as the sample in 11.2 has it. Timers queue jobs only for boards that opted in:
     briefs every five minutes for boards whose standup is half an hour away, the constraint check hourly, WIP
     advice on Mondays at 06:00 UTC.
115. **Insights have one shape for every kind,** a new one replaces the open one of its kind, and dismissing or
     accepting needs `wip.set`. Accepting exists only for WIP proposals and refuses one whose limit changed
     since. Schema beyond section 14: `State`, `ResolvedBy` and `ResolvedAtUtc` on `kb.AiInsight` (instead of
     `DismissedBy`), `kb.AiUsage`, and `AiEnabled` and `AiMonthlyTokenBudget` on `kb.Board`. Not built: a live
     push when an insight appears, and the standup-brief notification and Teams post of section 11.3.
116. **The AI has no principal of its own yet.** Nothing it does changes a board, so it leaves no audit entries;
     accepting or dismissing an insight is audited as the person who did it.
117. **No golden set.** Section 11.2 asks for 50 board snapshots per feature, scored for groundedness and
     usefulness. What exists instead: unit and API tests of the grounding rules with a scripted model, including
     answers that invent cards and numbers. `Microsoft.Extensions.AI.Evaluation` is not referenced.
118. **What ran and what didn't.** Run: the rules; every endpoint against the real database with the stand-in
     model (including the 19.2 scenario "AI suggests, never acts"); the adapter with the real client libraries
     against a stubbed Azure OpenAI endpoint; the features in a browser against the dev host. Never run: a real
     model, Azure OpenAI itself, managed-identity sign-in, the Workers functions and timers, the `ai-jobs` queue.
     The prompts are therefore untested for quality.
119. **Aging and SLE alerts (feature 4).** The expectation is the 85th percentile of cycle time of the cards of
     the same type that finished on the board in the last 90 days, and needs at least five of them. A card past
     it is alerted **once**, to its creator and the board's Owners and Flow Managers (there are no assignees
     yet), in the app by default and switchable per person. Section 11.3 says "daily digest": there is no digest
     and no repeat. The rule runs for every board without opt-in, because it is not AI; the model only words the
     alert on boards that opted in, and the rule's own sentence is used when its wording fails the grounding
     check. Not built: an aging badge on the card. The rule scans every board's cards on each run, which is
     fine for now and will want an index or a snapshot table at scale. Schema: `kb.CardAgingAlert`.
120. **Duplicate finder (feature 8).** A card is embedded when it is created or its title or description changes
     (a hash of the text avoids embedding it again for other edits), on boards that opted in and are under
     budget. Cards of the same board within a cosine distance of 0.15 (85% alike), at most three, are suggested
     on both cards of the pair. "Not a duplicate" needs `card.edit` and keeps the pair from being suggested
     again. **"Link" is not built:** cards have no links or dependencies yet. Cards that existed before a board
     opted in are compared once they are next edited; there is no backfill. Issues mirrored from GitHub are
     compared like any other card. Vectors are written as a JSON array cast to `vector(1536)` inside the
     procedure and compared with `VECTOR_DISTANCE`; nothing reads them back, so `SqlVector<float>` is not used.
     There is no vector index: the search is exact, over one board's cards. Schema: `TextHash` on
     `kb.CardEmbedding`, and `kb.CardDuplicate`. The embedding deployment is its own setting
     (`Ai:EmbeddingDeployment`) and must return 1536 dimensions; another size is refused and logged.
121. **What ran for items 119 and 120.** Run: both rules against the real database (LocalDB runs `vector` and
     `VECTOR_DISTANCE`), the embedding adapter with the real client libraries against a stubbed endpoint, and
     the duplicate finder in a browser against the dev host. The aging rule ran against the dev database and
     found nothing to alert there, so its notification was seen only in tests. Never run: a real embedding
     model, so the 0.15 threshold is a starting point, not a tuned value.

## Infrastructure and pipelines (Phase 8)

122. **Cost first; this system is not run in production** (decided with the product owner on 2026-11-17, who
     first chose SignalR Free for dev and Standard above, and a small serverless database, and then asked for
     the lowest cost on every SKU and serverless throughout). So all three environments, prod included, default
     to the cheapest tier that runs the whole system, and each choice is a parameter of `infra/main.bicep`.
     Against section 13.1 and the listing in 18.2: Static Web Apps Free instead of Standard; SignalR Free;
     SQL serverless with the free offer, local backups and no zone redundancy everywhere; Service Bus Standard
     everywhere, never Premium; Flex Consumption at 512 MB, at most 1 instance and none kept warm (18.2 shows
     2048 MB, 100 and 1). `main.prod.bicepparam` lists what to raise before real use.
123. **Service Bus Standard is a paid tier, and the one fixed monthly charge.** The design needs topics,
     sessions and duplicate detection, which Basic lacks, so there is no cheaper tier to choose. Section 21 asks
     for a question before a paid SKU. **The product owner's answer on 2026-11-17: stay local only.** KanbanHub
     is run on a developer's machine (`run-local.ps1`) and is not deployed to Azure, so no paid tier is in use.
     The templates and workflows stay in the repository as written and unverified, and nothing deploys unless
     someone starts it by hand (item 126). The tier stands as the template's default for the day that changes.
124. **No private network.** No virtual network, private endpoints, Front Door or WAF (sections 13.1 and 16):
     each has a fixed cost. In their place: Entra-only SQL, with a firewall rule that admits Azure-hosted
     callers (the function apps and GitHub's runners); key and connection-string sign-in switched off on Storage,
     Service Bus, SignalR and Azure OpenAI; TLS 1.2 everywhere.
125. **The frequent timers are off by default** (`frequentTimers = false` sets `AzureWebJobs.<name>.Disabled` on
     the workers): `SweepOutbox` (every minute), `QueueStandupBriefs` (every 5 minutes), `RaiseAgingAlerts`
     (every 15) and `QueueBottleneckChecks` (hourly). Each run queries the database, which would keep a
     serverless database from ever pausing and use up the free allowance in days. The price: an event the API
     could not publish right after its commit is not retried, and scheduled briefs, aging alerts and constraint
     checks do not happen in Azure until the parameter is set. The nightly reconcile and the weekly WIP advice
     stay on. A test fails if a name in the template is not a timer of the workers.
126. **Every environment is deployed on demand only,** not on a merge and not one after the other (18.1, stages
     2 to 4): with the project run locally (item 123), a push to `main` builds and tests and never touches
     Azure. "Run workflow" on `main` deploys the environment you pick, prod behind the reviewers of its GitHub
     environment. An environment nobody asks for is never created. Stage 3's end-to-end, accessibility
     and load tests do not exist, so nothing of the kind runs. There is no App Configuration: the two switches
     that exist are an app setting (`Agents__Enabled`) and the per-board AI opt-in.
127. **Pipeline choices.** One reusable workflow deploys any environment. Actions are pinned to the commits of
     their latest releases on 2026-11-17, with the inputs used checked against each action's own definition.
     The Static Web Apps deployment token is read from Azure during the run, so not even the one secret that
     section 18 allows is stored. `sqlpackage` is installed as a .NET tool. Function packages are zipped before
     upload, because artifacts drop the `.azurefunctions` folder. The web app is built once and its
     `appsettings.json` is written per environment at deployment. CodeQL reads C# without a build and also scans
     the workflows; it lives in its own workflow with dependency review. GitHub offers both free of charge on
     public repositories only, so both jobs are skipped while the repository is private (item 133). No
     Dependabot configuration.
128. **The apps' identity gets its database access from the dacpac's post-deployment script,** through two
     SQLCMD variables the pipeline passes (`AppIdentityName`, `AppIdentityClientId`). The user is created
     `WITH SID` from the client id instead of `FROM EXTERNAL PROVIDER`, so the SQL server needs no permission to
     read the directory. With the variables empty, the default, the script grants nothing; that is what LocalDB
     and the tests get.
129. **Settings and names against the 18.2 listing.** No `SignalR__credential`, `SignalR__clientId` or
     `KeyVault__Uri`: the code builds its SignalR client from `SignalR__serviceUri` with its own credential and
     is given whole key identifiers. One user-assigned identity serves both apps. Resource names that must be
     unique worldwide end in a short hash of the resource group. The GitHub App's private key and its two
     secrets are put into Key Vault by hand, once; the template only refers to them, and only when the `gitHub`
     parameter is set. `appsettings.Development.json` no longer ships in Release builds of the web app.
130. **Telemetry.** The Functions host sends requests, traces and logs to Application Insights through the
     connection string the template sets; the daily cap is 1 GB. The worker-side OpenTelemetry of section 15.5
     (`UseAzureMonitorExporter`) needs `Azure.Monitor.OpenTelemetry.Exporter`, a package outside section 13.3,
     so it is not added: an open question. The custom metrics and alerts of section 17 are not built; the one
     alert there is, is the optional budget.
131. **Not built:** `azure.yaml` for azd; a custom domain; Teams webhooks (18.4); a full Content Security Policy.
     The web app is served with `frame-ancestors`, `base-uri` and `object-src` only, plus `nosniff` and a
     referrer policy: a stricter policy has to be tried against MSAL, SignalR and card images in a deployed
     app, and none exists.
132. **What ran and what didn't.** Run: `az bicep build` and `az bicep lint` without a warning (Bicep 0.46.1),
     and the three parameter files; the workflows parsed as YAML and checked for pinned actions and for
     expressions inside shell scripts; the dacpac with the grant on LocalDB with empty variables, and the SQL
     the grant builds for a sample name, printed; a Release publish of both function apps and the web app, with
     the packages inspected; `run-local.ps1`, which started both apps and ended when they stopped (stopping it
     with Ctrl+C was not tried). **Never run: any deployment or what-if, any workflow, `sqlpackage`, the grant
     against Azure SQL, a build on Linux, the function apps in the Functions host.** The first deployment will
     find what a compiler cannot.
133. **The first runs on GitHub failed in two places** (the project was pushed to a private repository on
     2026-11-23). **The build:** the Functions SDK prepares its extension project in a step after a function
     project's own restore. Restoring the solution, or a project that references a function app, never runs
     that step, so on a clean checkout the SDK does it during the first build and reports `AZFW0108`, which
     `TreatWarningsAsErrors` turned into an error. It never showed on the development machine, where the
     folders of an earlier build were always there. `AZFW0108` is now in `NoWarn` for every project; a fresh
     clone builds with the pipeline's own command, and `run-local.ps1` works on one too. **CodeQL:** the
     analysis itself ran, for C# without a build and for the workflows. The action then failed for want of the
     `actions: read` permission that private repositories need, and GitHub answers that code scanning and
     dependency review are not available on this private repository. Both jobs now run only while the
     repository is public. Checked in a fresh clone on Windows: the pipeline's build, all tests and the three
     publishes. Not known when this was written: whether the tests pass on the Linux runner, where they start
     SQL Server in a container; the first run stopped before them.

## Not built yet, by design

43. **OpenAPI.** The Functions OpenAPI extension is a dependency outside section 13.3, so it needs a decision.
44. **Attachments** are still not built: Phase 8 created the storage account, but there is no container,
    endpoint or screen for them. **Assignees, watchers, tags and dependencies** have no endpoint
    in 15.1 and their tables are only sketched in 14.2; they come with the card panel in Phase 3.
45. **Agents and AI** endpoints, and /github/connect, were built in phases 6 and 7; their own gaps are in items
    97 to 121.
46. **OpenTelemetry in the workers** is still open; item 130 says what Phase 8 wired and what it did not.

## Build and test tooling

47. **Tests run on Microsoft.Testing.Platform** (xUnit v3 4.x). On the .NET 10+ SDK that requires the opt-in in
    `global.json`, and the CI command becomes `dotnet test --solution KanbanHub.slnx` instead of the form in 18.3.
48. **Tests pick their SQL Server:** `KANBANHUB_TEST_SQL` if set, LocalDB on Windows, otherwise a Testcontainers
    SQL Server 2025. The container path has not been run on the development machine (no Docker).
49. **API tests call the endpoint classes through the real composition root,** without the Functions host, which
    is not installed on the development machine. Not exercised by any test: the two worker middlewares
    (`EntraJwtMiddleware`, `UnhandledExceptionMiddleware`), `AddKanbanHubHost` and `EntraSqlConnectionFactory`.
    The token validation the middleware delegates to is tested on its own.
50. **Test-only packages beyond 13.3:** `Microsoft.SqlServer.DacFx`, `Testcontainers.MsSql`, `CsCheck`,
    `Microsoft.Testing.Extensions.CodeCoverage`, `xunit.v3.extensibility.core`.
51. **`CS1591` is off;** XML docs are written where they add information. **`CA1711` is suppressed on
    `Permission`,** the name the specification gives the type.
