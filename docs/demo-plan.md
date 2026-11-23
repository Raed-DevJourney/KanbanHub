# KanbanHub demo plan

A plan for a recorded walkthrough in two parts. Part 1 describes what KanbanHub can do. Part 2 then uses it:
a small team goes from an empty board to a shipped application, and each step of that story shows one of the
capabilities from Part 1 at work. Nothing here has been rehearsed yet; the list "To confirm in a dry run" names
what I expect to adjust.

## The idea

**Part 1 says it, Part 2 proves it.** The opening names six groups of capabilities on one slide and points at
each on a board that is already full of work. The viewer then knows what to look for when the team starts
from nothing.

**The application being "built" in Part 2: Tip Splitter,** a one-page web app that splits a restaurant bill.
It is small enough that everyone understands every card at a glance, so the viewer watches the process, not
the product.

**No application code is written.** The demo simulates the process: cards move, and the dev host's stand-ins
play GitHub, the coding agent and the AI model. Everything runs on one machine with `./run-local.ps1`.

**Length:** about 15 minutes: 2 for Part 1 and 13 for the nine acts of Part 2. Part 1 with acts 4, 7 and 8
makes a 6-minute short version.

## Cast

The dev host's personas, switched with the selector in the header or `?as=<key>` in the address.

| Who | Role on the board | Part in the story |
| --- | --- | --- |
| Olivia Owner (`owner`) | Owner, may create boards | Product owner: sets the board up, fills the backlog, sends work to the agent |
| Marco Flow Manager (`manager`) | Flow Manager | Runs the standup, overrides a WIP limit, accepts the AI's WIP proposal |
| Mia Member (`member`) | Member | Developer: pulls, blocks, finishes and comments on cards |
| Carl Contributor (`contributor`) | Contributor | Shows what a narrower role may do |
| Vera Viewer (`viewer`) | Viewer | Stakeholder: sees everything, moves nothing |
| Sam Stranger (`stranger`) | none | Shows that a board is invisible without a role |
| `octocat`, Copilot | on the pretend GitHub | The connected GitHub account and the coding agent |

## Part 1. What KanbanHub can do (about 2:00)

### The six capability groups

This is the slide, and the outline of what the narrator says. Each group names the acts of Part 2 that show it.

1. **Flow by the book.** An ordered backlog, an Active and a Done column per step, and work that is pulled, not
   pushed. WIP limits are enforced on the server, a done rule guards every step, blocked work waits in its own
   column outside the limit, and going backward or over a limit needs a reason. *(Acts 2, 3, 4)*
2. **A board the team shapes.** Workflow, WIP limits with the book's calculator, done rules, colors and members
   are settings of each board, and six roles from Owner to Viewer decide who may do what. *(Acts 1, 5)*
3. **Everyone sees the same board.** A change reaches every open board at once; the people it concerns get it
   in their inbox or by email; standup mode walks the board from the last step to the first. *(Acts 5, 8)*
4. **GitHub, both ways.** Cards and issues stay in step: text, labels, comments, a status comment, commands in
   issue comments, and a done-rule check on pull requests. GitHub can ask; the board's rules decide. *(Act 6)*
5. **Coding agents.** A card's issue is sent to GitHub Copilot in your name, within a limit on open sessions,
   with instructions that treat card text as data; the board tracks the agent's pull request. *(Act 7)*
6. **AI that suggests while people decide.** Breakdown, triage, done-rule check, agent instructions, the
   forecast in words, the standup brief, the constraint finder, the WIP advisor, aging alerts and the duplicate
   finder. Opt-in per board, within a token budget, and it never moves a card or changes a limit. *(Acts 2, 3, 4, 7, 8)*

### How it is shown

On "Checkout redesign", the sample board the dev host creates: work in every column, an Implement step that is
nearly full, a blocked card and a done rule. Nothing is changed on it; the narrator points.

| Step | On screen | Says |
| --- | --- | --- |
| 0.1 | Title, then the slide with the six groups | What KanbanHub is, and the map of the next 15 minutes |
| 0.2 | The sample board from left to right: backlog, each step's Active, Blocked and Done columns with their WIP counts, the parking lot | Group 1 |
| 0.3 | One card's panel: details, done-rule checklist, comments, and its GitHub, Agent and AI sections | Everything about a card is in one place; groups 4, 5 and 6 each have a home here |
| 0.4 | The Settings tabs, then the role selector in the header | Group 2 |
| 0.5 | The Inbox badge, the Standup page, the Insights panel above the board | Groups 3 and 6 |
| 0.6 | Back to the slide | "Now watch a team use all of it, from an empty board." |

## Part 2. The Tip Splitter simulation (about 13:00)

### Act 1. Set up the board (Olivia, about 1:30)

| Step | On screen | Shows |
| --- | --- | --- |
| 1.1 | Boards, "New board": name "Tip Splitter", new workspace "Tip Splitter team", quick-start template | Boards, workspaces, the template (Specify, Implement, Validate) |
| 1.2 | Settings, Workflow: the steps, the Blocked sub-column per step, "Open to agents" on Implement | Workflow editor |
| 1.3 | Settings, WIP limits: run the calculator, then set Implement to 3 and Validate to 2 so limits are reached quickly | WIP limits and the book's calculator |
| 1.4 | Settings, Done rules: Implement gets "Code reviewed" (required), "Unit tests pass" (required), "Docs updated" (optional) | Done rules |
| 1.5 | Settings, Palette: change one step's color; Settings, General | Palette, general settings |
| 1.6 | Settings, Members: add Marco, Mia, Carl and Vera with their roles | Members and the built-in roles |
| 1.7 | Settings, AI: tick "Use AI on this board", show the monthly token budget | AI is opt-in per board, with a budget |

### Act 2. Fill and order the backlog (Olivia, about 1:30)

| Step | On screen | Shows |
| --- | --- | --- |
| 2.1 | Add cards: "Split a bill evenly", "Tip percentage", "Round up to whole amounts", "Share the result as a link", "Dark mode", "Spike: currency formats" | Backlog, card details (type, priority, origin, due date) |
| 2.2 | Open "Dark mode", choose "Suggest type and priority", accept the suggestion, save | AI triage: fills the form, saves nothing by itself |
| 2.3 | Add "Crash when the number of people is zero" and then "Crash when number of people is zero"; open the second: it lists the first under "Possible duplicates" | Duplicate finder |
| 2.4 | Cancel the duplicate with a reason | Cancel, the reason dialog |
| 2.5 | Drag the bug to the top of the backlog | Ordering the backlog |

### Act 3. Specify and break down (Olivia, about 1:00)

| Step | On screen | Shows |
| --- | --- | --- |
| 3.1 | Pull "Split a bill evenly" into Specify | Pull from the backlog |
| 3.2 | Card panel, "Break down", "Suggest tasks": three tasks arrive with acceptance criteria; edit one, save | Breakdown and the AI breakdown assistant |
| 3.3 | Move the parent to Specify Done; the tasks wait for Implement | Active and Done sub-columns; Specify counts parents only |

### Act 4. Implement under WIP limits (Mia and Marco, about 2:30)

| Step | On screen | Shows |
| --- | --- | --- |
| 4.1 | Mia drags two tasks into Implement, then moves the bug with the keyboard (Space, arrow, Space) | Drag and keyboard moves |
| 4.2 | Implement is at 3 of 3. Mia drags a fourth card: it snaps back and the board says which step to help finish | The server decides; WIP limit enforced |
| 4.3 | Marco overrides the limit for the bug and gives a reason | WIP override, permission by role |
| 4.4 | Mia drags a task to Implement Done: refused, the done rule is unmet. She opens the card, chooses "Check with AI", ticks the required items, moves it | Done rules as a gate; AI done-rule check |
| 4.5 | Mia blocks a task ("Waiting for the currency list", external): it moves to Blocked and frees a WIP slot. Later she unblocks it | Blocked sub-column outside WIP |
| 4.6 | Mia moves a card back from Implement to Specify: a reason is asked for | Backward moves need a reason |
| 4.7 | Mia comments on a card; Olivia parks "Spike: currency formats" in the parking lot | Comments, parking lot |

### Act 5. The team sees it live (two windows side by side, about 1:30)

| Step | On screen | Shows |
| --- | --- | --- |
| 5.1 | Left window Olivia, right window Mia. Mia moves and blocks a card; Olivia's board follows without a reload, and so does an open card panel | Live updates over SignalR |
| 5.2 | Olivia's Inbox: the block, the comment, Marco's override. Mark read, snooze one, mute a card, change a preference to email | Inbox and notification preferences |
| 5.3 | The dev host's log shows the email it would have sent | Email notifications (written to the log locally) |
| 5.4 | Vera opens the board: no drag, no edit buttons. Carl: what a Contributor may do. Sam: no board at all | Roles and permissions |
| 5.5 | Use the filter bar on the board | Filters |

### Act 6. GitHub, both ways (Olivia, about 1:30)

| Step | On screen | Shows |
| --- | --- | --- |
| 6.1 | Settings, GitHub: link `contoso/tip-splitter`. The app appears under Members as its own member | Linking a repository |
| 6.2 | Card "Tip percentage": "Create an issue from this card". `/dev/github` shows the issue, its `kanban:` and `type:` labels and the status comment | Card to issue, labels, the status comment |
| 6.3 | Move the card on the board; the labels and the status comment on the pretend GitHub change | Board to GitHub |
| 6.4 | Post an issue comment `/kanban block waiting for design`: the card blocks on the board | Commands from GitHub |
| 6.5 | Post `/kanban move next` while the done rule is unmet: the card stays, shows "Sync issue", and the status comment says why | Nothing from GitHub bypasses the rules |
| 6.6 | Open a new issue on the pretend GitHub: it appears in the backlog | Mirroring issues into the backlog |

### Act 7. Send a card to a coding agent (Olivia, about 1:30)

| Step | On screen | Shows |
| --- | --- | --- |
| 7.1 | Profile, "Connect GitHub": back at once, connected as `octocat` | Connecting a GitHub account |
| 7.2 | Card "Tip percentage", "Send to agent", "Draft with AI", edit the draft, send. The card shows "Agent working"; the "Agent working" filter chip finds it | Agent dispatch; AI-drafted instructions |
| 7.3 | `/dev/github` shows the assignment and the instructions, with the card text between markers | Card text is passed as data |
| 7.4 | Settings, Agents: set the limit to 1; a second dispatch is refused | Agent concurrency limit |
| 7.5 | The agent opens pull request 77 ("Fixes #n"): the card changes to "Agent PR #77" by itself, and the "Kanban done rule" check appears on the pull request | Pull request tracking, the check run |
| 7.6 | The pull request is merged: the session ends and the slot is free. Mia ticks the done rule and moves the card herself | The agent never moves a card |

### Act 8. Flow, standup and insights (Marco, about 1:30)

| Step | On screen | Shows |
| --- | --- | --- |
| 8.1 | Cards are moved through Validate to Done until several are finished | Finishing work |
| 8.2 | Run the standup brief, open Standup: last step first, blocked and aging cards first, the timer, "is anyone blocked?" | Standup mode and the standup brief |
| 8.3 | Let cards pile up in Implement Done, run the constraint finder: the Insights panel names Validate, with the cards it cites | Constraint finder |
| 8.4 | Run the WIP advisor; Marco accepts one proposal and the limit changes | WIP advisor; accepting is the only way a suggestion becomes a change |
| 8.5 | "Explain the forecast" | Forecast and its narrative |
| 8.6 | Run the aging rule; the alert arrives in the Inbox | Aging alerts |

### Act 9. Close (about 0:30)

| Step | On screen | Shows |
| --- | --- | --- |
| 9.1 | `GET /dev/ai`: the fixed instructions, and the board's data next to them | AI gets data, not instructions |
| 9.2 | Settings, AI: tokens used this month. `GET /api/v1/boards/{id}/audit`: who did what | Budget, audit trail |
| 9.3 | The finished board, then the slide of Part 1 once more with all six groups ticked; closing line: all of this ran on one laptop, with nothing in the cloud | |

## Not in the demo

These exist in the code but cannot be shown on one machine: real GitHub and a real Copilot session, a real AI
model, delivered email, Entra sign-in, and the Azure deployment. The timers (standup brief, constraint finder,
WIP advisor, aging rule) do not wait for their schedule here; each is started through the dev host's
`/dev/ai/...` address.

## To confirm in a dry run

I have not tried these on a new board; each may change a step above or drop it.

1. **Members (1.6):** how the Members tab picks a person locally, and whether the four personas can be added there.
2. **Aging alert (8.6):** it needs five finished cards of one type and a card that has been in flow longer
   than most of them took. On a board that is minutes old that may or may not trigger.
3. **Forecast (8.5):** how much finished work it needs before it says anything useful.
4. **Constraint and WIP advice (8.3, 8.4):** which pile-up the stand-in model needs before it reports.
5. **GitHub steps 6.4 to 6.6:** the exact webhook payloads; I will prepare them as files beforehand.
6. **Carl Contributor (5.4):** what exactly that role may and may not do on screen.
7. **Audit (9.2):** it has an endpoint and no screen, so it is shown as JSON, or left out.

## Production

- **Recording:** FFmpeg, once it is installed, records the screen while I click through each act in a visible
  Chrome window. Without it, you record with Snipping Tool while I drive.
- **Narration:** one short text per step, written after the dry run. Either the Windows voice (clearly
  synthetic) generated per step, or your own voice over the finished video.
- **Takes:** one recording for Part 1 and one per act, joined afterwards, so a mistake costs one take and not
  the whole video.
- **The slide:** one HTML page with the six groups, which I prepare before recording and show in the same
  browser window.
- **Before recording:** start with `./run-local.ps1`, a fixed window size, and the webhook payloads of acts 6
  and 7 ready as files.
- **The sample board:** Part 1 needs "Checkout redesign" as the dev host first creates it. The dev database on
  this machine has been used for testing since, so it should be reset before recording. That deletes the
  local `kanbanhub_dev` database and nothing else; I will ask before doing it.
