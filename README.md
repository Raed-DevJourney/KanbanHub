# KanbanHub

Kanban project management on Azure, following Eric Brechner's *Agile Project Management with Kanban*:
an ordered backlog, an Active and a Done sub-column per step, WIP limits enforced on the server, and explicit done rules.

This repository is built phase by phase from the specification's build prompt (section 21).

| Phase | Scope | Status |
| --- | --- | --- |
| 0 | Solution scaffold, build rules, central package versions | Done |
| 1 | Domain model, SQL schema, `kb.usp_MoveCard`, integration and race tests | Done |
| 2 | API and auth: Azure Functions, Entra token validation, RBAC, MVP endpoints | Done, with the gaps listed in DECISIONS |
| 3 | Blazor UI: board, drag and keyboard moves, card panel, backlog, settings | Done, with the gaps listed in DECISIONS |
| 4 | Real-time board, outbox dispatcher, in-app inbox, email, standup mode | Done, with the gaps listed in DECISIONS |
| 5 | GitHub App: webhook receiver, two-way issue sync, status comment, labels, slash commands, check run, backfill | Done, with the gaps listed in DECISIONS |
| 6 | Agents: Copilot dispatch behind `IAgentDispatcher` and the `Agents:Enabled` flag, "Connect GitHub", agent limit, pull request tracking | Done, with the gaps listed in DECISIONS |
| 7 | AI: breakdown assistant, standup brief, constraint finder, WIP advisor, aging alerts, duplicate finder, triage, done-rule check, agent instructions, forecast narrative; all suggest-only and grounding-checked | Done, with the gaps listed in DECISIONS |
| 8 | Infrastructure and pipelines: Bicep for dev, test and prod at the lowest-cost tiers, GitHub Actions with OpenID Connect, a one-command local run | Written and checked locally; never deployed, and the deployment workflow has never run. The project is run locally only, so deployment is on demand. Gaps in DECISIONS |

Choices that go beyond or differ from the specification, and everything deliberately not built yet,
are recorded in [DECISIONS.md](DECISIONS.md).

## Try it locally

The whole system runs on one machine. No Azure, Entra or Functions tools are needed and nothing costs money:
a local dev host serves the real API against LocalDB and stands in for SignalR, Service Bus, GitHub, the
agents, the AI model and email.

```powershell
./run-local.ps1
```

The script builds and starts the dev host and the web app, waits until both answer, opens
<http://localhost:5173>, and stops both on Ctrl+C. `-NoBrowser` leaves the browser closed, and
`-SqlConnectionString "<connection string>"` uses another SQL Server 2025 instead of LocalDB.

Or start the two yourself, each in its own terminal, and open <http://localhost:5173>:

```bash
dotnet run --project tools/KanbanHub.DevHost
```

```bash
dotnet run --project src/KanbanHub.Web
```

The first start creates the `kanbanhub_dev` database and a sample board.
You are signed in as the board's owner; the selector in the header switches to a Flow Manager, Member,
Contributor, Viewer or someone with no role, so every permission level can be tried.
On another SQL Server, start the dev host with `-- --Sql:ConnectionString "<connection string>"` after the project path.

To watch a change travel, open the board in a second tab as someone else (add `?as=manager` to the address) and
move or block a card: the first tab follows within a moment, and the people the change concerns get it in their
Inbox. The dev host serves its own SignalR hub and writes emails to its log instead of sending them.

### Trying the GitHub sync without GitHub

The dev host stands in for GitHub too: it keeps issues, comments and check runs in memory and forgets them when
it stops. As the owner, open Settings, GitHub and link any `owner/name`; then open a card and choose "Create an
issue from this card". To see what the pretend GitHub holds, and to act as someone there:

```bash
curl http://localhost:7071/dev/github
```

`POST http://localhost:7071/dev/github/issues` (or `/issue_comment`, `/pull_request`) takes a webhook payload in
GitHub's shape and delivers it without a signature. Signed deliveries go to `/api/github/webhook` and need
`--GitHub:WebhookSecret` on the dev host's command line.

### Trying agents without GitHub

The dev host stands in for GitHub's agents as well: it remembers what an agent was asked instead of starting one.

1. Open Profile and choose "Connect GitHub". The pretend GitHub approves at once and you come back connected as `octocat`.
2. Open a card that has an issue and sits in Implement (or waits in Specify Done) and choose "Send to agent".
   The card shows "Agent working"; `GET http://localhost:7071/dev/github` lists the assignment and the
   instructions under `agentAssignments`.
3. Play the agent's pull request. Save this as `pr.json`, with the `repositoryId` and issue number that
   `/dev/github` shows (the pretend installation id is always 1), and post it:

```json
{
  "action": "opened",
  "pull_request": {
    "number": 77, "body": "Fixes #2", "state": "open", "merged": false, "draft": true,
    "user": { "login": "Copilot" }, "head": { "sha": "abc123" }
  },
  "repository": { "id": 602399844670, "full_name": "contoso/checkout" },
  "installation": { "id": 1 },
  "sender": { "login": "Copilot" }
}
```

```bash
curl -X POST http://localhost:7071/dev/github/pull_request -H "Content-Type: application/json" -d @pr.json
```

The card changes to "Agent PR #77" by itself. Post it again with `"action": "closed"`, `"state": "closed"` and
`"merged": true` to see the session end and the agent slot come free. To see the app with the feature switched
off, start the dev host with `-- --Agents:Enabled false` after the project path.

### Trying the AI features without Azure

The dev host has a stand-in model too: it answers from the data it is handed, by simple rules, so every feature
can be tried and nothing leaves the machine.

1. As the owner, open Settings, AI and tick "Use AI on this board". Until then nothing is ever sent to a model.
2. Open a card: "Suggest type and priority" in its details, "Check with AI" under its done rule, "Draft with AI"
   in the agent form, and "Suggest tasks" in the break-down form of a card in the breakdown step.
3. The background analyses wait for their timers in Azure. Here, run one now (the board's id is in its address):

```bash
curl -X POST http://localhost:7071/dev/ai/StandupBrief/<board id>
```

   `Bottleneck` and `WipAdvice` work the same way. What they find appears in the Insights panel above the board,
   and the brief also opens standup mode. `GET http://localhost:7071/dev/ai` shows what the model was asked:
   the fixed instructions, and the board's data next to them.
4. Add two cards that say nearly the same ("Checkout crashes on empty cart", "Checkout crashes on an empty
   cart") and open the second: it lists the first under "Possible duplicates". The stand-in compares words, not
   meaning.
5. `POST http://localhost:7071/dev/ai/aging` runs the aging rule now and answers how many cards it alerted. The
   sample board has too little finished work to set an expectation, so expect 0 until five cards of a type finished.

## Layout

```text
KanbanHub.slnx
  src/
    KanbanHub.Domain      Pure C# 15: flow rules, WIP math, roles, use cases, forecast. No I/O.
    KanbanHub.Contracts   DTOs shared by the API and the web app, with source-generated JSON.
    KanbanHub.Data        ADO.NET: stores that call the write procedures, read queries, the SQL authorizer.
    KanbanHub.Database    SDK-style SQL project (tables, functions, procedures, view, role seed) -> .dacpac
    KanbanHub.Messaging   Azure adapters: SignalR Service, the Service Bus topic and queues, Communication Services email.
    KanbanHub.GitHub      GitHub App adapters: webhook signature and parser, app token provider, REST client, "Connect GitHub".
    KanbanHub.Agents      The adapter in front of GitHub's preview agent APIs (Copilot dispatch), and its feature flag.
    KanbanHub.AI          The model behind the AI features: an IChatClient over Azure OpenAI, and a stand-in for local use.
    KanbanHub.Api         Function app (isolated worker): endpoints, auth middleware, problem details, the webhook.
    KanbanHub.Workers     Function app: notifications, the outbox sweeper, the GitHub sync in both directions.
    KanbanHub.Web         Blazor WebAssembly app: board, card panel, backlog, inbox, standup, profile, board settings.
  tests/
    KanbanHub.Domain.Tests            Unit and property tests, no database.
    KanbanHub.Data.IntegrationTests   The real schema on a real SQL Server, including the race test.
    KanbanHub.Api.Tests               Every endpoint against the real database, including the role matrix.
    KanbanHub.Web.Tests               The web app's logic against a stubbed server.
    KanbanHub.TestSupport             The shared database fixture.
  tools/
    KanbanHub.DevHost     Local stand-in for the Functions host, with made-up sign-ins. Never deployed.
  infra/
    main.bicep            One environment in one resource group; every cost choice is a parameter.
    main.<env>.bicepparam The parameters of dev, test and prod. Ids come from environment variables.
    modules/              Identity, logs and storage; SQL; Service Bus and SignalR; Key Vault; the function
                          apps; the static web app; email; Azure OpenAI; the budget.
  .github/workflows/
    ci-cd.yml             Build, test, package; deploy main.
    deploy.yml            The deployment of one environment, called by ci-cd.
    security.yml          CodeQL and dependency review.
  run-local.ps1           Runs everything on this machine.
```

## Prerequisites

- **.NET 11 SDK** (11.0.100 or later; pinned in `global.json`).
- **SQL Server 2025** for the tests and the dev host. The schema uses the `vector` type, so older versions won't deploy it.
  - Windows: SQL Server 2025 LocalDB (`(localdb)\MSSQLLocalDB`) is used automatically.
  - Linux, macOS and CI: Docker, which Testcontainers uses to start `mcr.microsoft.com/mssql/server:2025-latest`.
  - Any other server: set `KANBANHUB_TEST_SQL` to its connection string. Each test run creates and drops its own database.
- **Azure Functions Core Tools v4** and **Azurite** only to run the API in the real Functions host.

## Build and test

```bash
dotnet build KanbanHub.slnx -warnaserror
```

```bash
dotnet test --solution KanbanHub.slnx
```

The tests run on Microsoft.Testing.Platform (xUnit v3), so `dotnet test` takes `--solution` or `--project`:

```bash
dotnet test --project tests/KanbanHub.Api.Tests
```

```bash
dotnet test --solution KanbanHub.slnx --coverage --coverage-output-format cobertura
```

## Database

`dotnet build src/KanbanHub.Database` produces `src/KanbanHub.Database/bin/<Configuration>/KanbanHub.Database.dacpac`.
Publish it with `sqlpackage /Action:Publish`; the post-deployment script seeds the six built-in roles.
The tests and the dev host deploy the same file with DacFx.

The script also lets the function apps' managed identity into the database (read, write, and execute on schema
`kb`) when the publish names it: `/v:AppIdentityName=<identity name> /v:AppIdentityClientId=<its client id>`.
Without the two variables, as on a developer's machine, it grants nothing.

## The web app

| Screen | What it does |
| --- | --- |
| Boards | Lists the boards you hold a role on; board creators can add one from the quick-start template. |
| Board | Backlog, each step's Active, Blocked and Done columns with WIP against the limit, and the parking lot. |
| Card panel | Details, the step's done-rule checklist, block and unblock, break down, cancel, comments. |
| Settings | Workflow, WIP limits with the book's calculator, done rules, palette, members, general. Each tab appears only with its permission. |
| Settings, GitHub | Link repositories to the board: mirror every open issue into the backlog, or link issues to cards on demand. |
| Card panel, GitHub | The card's issue, what GitHub asked for that could not be applied, create or link an issue, sync now, unlink. |
| Card panel, Agent | The pull requests that close the card's issue and how the last agent session went. With `agent.dispatch`: send the card to an agent, or stop tracking a session. |
| Settings, Agents | How many agent sessions the board allows at once. Which steps are open to agents is a column under Settings, Workflow. |
| Profile | Connect or disconnect your GitHub account; agents work in your name and on your Copilot plan. |
| Board, Insights | What the AI noticed: the standup brief, the constraint, proposed WIP changes, each with the cards it cites. Flow managers dismiss insights and accept a proposed limit; "Explain the forecast" tells the forecast in words. |
| Card panel, AI buttons | With `ai.use` on a board that opted in: suggest type and priority, check the done rule, draft agent instructions, suggest tasks when breaking down. Each fills a form or shows advice; nothing is saved until you save it. |
| Card panel, Possible duplicates | Cards whose text is nearly the same, on both cards of a pair. Open the other card, or say "Not a duplicate" and the pair isn't suggested again. |
| Settings, AI | The board's opt-in, its monthly token budget and what it used this month. |
| Inbox | Your notifications: mark read, snooze for an hour or a day, mute a card, and choose per kind whether you are told in the app, by email or both. |
| Standup | The board walked from the last step to the first, blocked and aging cards first, the room each step has, a 15-minute timer, and the closing question: is anyone blocked? |

- **Moving cards.** Drag with the mouse, or use the keyboard: Space picks a card up, the left and right arrows
  choose a column, Space drops, Escape cancels. A screen reader hears where the card is and whether it can land.
- **The server decides.** The card moves at once and snaps back with the server's explanation if the move is
  refused. A full column stays visible, shows a no-drop cursor and says which step to help finish.
- **Blocked cards** get their own sub-column. Blocking a card in Active moves it to its step's Blocked sub-column
  (the book's Track), where it no longer counts toward the WIP limit; unblocking brings it back if the step has
  room, and otherwise it waits there. Board owners switch the sub-column on or off per step under Settings, Workflow.
- **Colors** come per board from the palette as CSS custom properties, and every status also has a text label.
- **Live.** A change anyone makes reaches every open copy of the board through SignalR. If the connection drops,
  the board says "Reconnecting" and refetches when it is back; if it stays down, it says so and offers to reconnect.
  An open card panel follows changes to its card too.
- **Agents.** A card with an open agent session carries an "Agent working" badge, then "Agent PR #n" once the
  agent's pull request waits for review. The "Agent working" filter chip shows only those cards.

For Entra sign-in, set the tenant, the two app registrations and the API scope in
`src/KanbanHub.Web/wwwroot/appsettings.json`. Debug builds use the dev host's personas when
`appsettings.Development.json` says `"Auth": { "Mode": "Dev" }`; Release builds contain MSAL sign-in only.

## Running the API in the Functions host

Create `src/KanbanHub.Api/local.settings.json` (git-ignored). It holds no secrets:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AZURE_FUNCTIONS_ENVIRONMENT": "Development",
    "Entra__TenantId": "<your tenant id>",
    "Entra__ApiClientId": "<client id of the KanbanHub.Api app registration>",
    "Sql__ConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=kanbanhub;Integrated Security=true",
    "Sql__Authentication": "ConnectionString"
  }
}
```

Leave `Sql__Authentication` out to connect with an Entra token instead (Azure CLI sign-in locally, managed identity in Azure).
Then start Azurite and run `func start` in `src/KanbanHub.Api`.

Since Phase 4 the function apps also need a SignalR Service (serverless mode) and a Service Bus namespace with a
topic `card-events` and a subscription `notifications`, reached with your Azure CLI sign-in (no keys):

| Setting | API | Workers | Value |
| --- | --- | --- | --- |
| `SignalR__serviceUri` | yes | yes | `https://<name>.service.signalr.net` |
| `ServiceBus__fullyQualifiedNamespace` | yes | yes | `<name>.servicebus.windows.net` |
| `Sql__ConnectionString`, `Sql__Authentication` | yes | yes | as above |
| `Email__Endpoint`, `Email__SenderAddress` | no | yes | the Communication Services endpoint and a verified sender |

Without those services, use the dev host: it does all three jobs in one process.

The GitHub sync (Phase 5) adds two session-enabled queues, `github-inbound` (with duplicate detection) and
`github-outbound`, fed by a `github` subscription of `card-events` that forwards to it, and these settings on both apps:

| Setting | Value |
| --- | --- |
| `GitHub__ClientId` | The GitHub App's client id |
| `GitHub__KeyId` | The Key Vault key that holds the app's private key (a key identifier URI); the app signs inside the vault |
| `GitHub__WebhookSecret`, `GitHub__PreviousWebhookSecret` | Key Vault references; the previous one is accepted during a rotation |
| `GitHub__BotLogin` | The login the app acts under, for example `kanbanhub[bot]` |
| `GitHub__PrincipalId` | The Entra object id the app acts as inside KanbanHub (it gets the Member role on a board when a repository is linked) |
| `GitHub__WebBaseUrl` | Where the web app lives, for the link in the status comment and the "Connect GitHub" callback |

The webhook URL to give the GitHub App is `https://<api host>/api/github/webhook`.

Agent dispatch (Phase 6) adds these settings on both apps:

| Setting | Value |
| --- | --- |
| `Agents__Enabled` | `true` to allow dispatch. GitHub's agent APIs are in public preview, so the default is off; with it off the app points people to the issue on GitHub instead |
| `GitHub__ClientSecret` | The GitHub App's client secret, as a Key Vault reference; used only to trade "Connect GitHub" codes for tokens |
| `GitHub__TokenKeyId` | A Key Vault RSA key (a key identifier URI) that encrypts people's refresh tokens; the apps' identity needs to encrypt and decrypt with it |

On the GitHub App itself: set the user authorization callback URL to `<GitHub__WebBaseUrl>github/callback`, keep
"Expire user authorization tokens" switched on (the stored refresh token depends on it), and subscribe to the
Issues and Pull request events.

The AI features (Phase 7) add a queue `ai-jobs`, a `duplicates` subscription of `card-events`, and these settings
on both apps. There is no key: give the apps' identity the "Cognitive Services OpenAI User" role on the resource.

| Setting | Value |
| --- | --- |
| `Ai__Endpoint` | The Azure OpenAI endpoint of the Foundry resource, for example `https://<name>.openai.azure.com/` |
| `Ai__ChatDeployment` | The name of the chat deployment; swap the model by changing this |
| `Ai__EmbeddingDeployment` | The name of an embedding deployment whose vectors have 1536 dimensions (the size of the database column). Leave it out and only the duplicate finder stays off |

Without the first two, the AI features answer "AI isn't set up in this environment" and nothing is sent
anywhere. With them, a board still gets no AI until an owner opts it in under Settings, AI. The aging rule is
not AI and runs for every board; only the wording of its alert comes from the model, on boards that opted in.

## Deploying to Azure

**No deployment has been run.** The templates compile and lint clean with the Bicep CLI, and the build
workflow runs on GitHub, but no environment was ever created and the deployment workflow has never run.
Expect to fix things on the first deployment.

**KanbanHub is run locally only** (decided on 2026-11-17, DECISIONS item 123), so this section describes what
exists for the day that changes. Nothing is deployed unless someone asks for it.

`infra/main.bicep` creates one environment in one resource group. `.github/workflows/ci-cd.yml` builds and tests
every pull request and every push to `main`, and never deploys on its own. To deploy, choose Actions, ci-cd,
"Run workflow" on `main` and pick dev, test or prod, so an environment nobody asks for is never created and
costs nothing.

### What is created, at the lowest cost

KanbanHub is not run in production, so every default is the cheapest tier that still runs the whole system.
Prices were not looked up for this table; check the Azure pricing pages before you rely on it.

| Resource | Tier | Cost when nobody uses it |
| --- | --- | --- |
| Two function apps (API, workers) | Flex Consumption, 512 MB, at most one instance, none kept warm | None: billed per execution. The first request after a quiet spell is slow |
| Azure SQL Database | General Purpose serverless, 0.5 to 1 vCore, pauses after 60 minutes, free offer on | None while Azure's free monthly allowance lasts; when it is used up the database pauses until next month |
| Service Bus | Standard, the smallest tier with topics, sessions and duplicate detection | **A fixed monthly base charge. This is the one fixed cost** |
| SignalR Service | Free: 20 connections, no SLA | None |
| Static Web Apps | Free: no SLA | None |
| Key Vault, Storage | Standard | Cents: a few key operations and two deployment packages |
| Log Analytics and Application Insights | Pay as you go, capped at 1 GB a day | None for light use |
| Communication Services (email) | Azure-managed sender domain | None: billed per mail |
| Azure OpenAI | Not created until you name a model | None |

To keep the database able to pause, the timers that fire every few minutes are **switched off** by default
(`frequentTimers = false`): the outbox sweeper, the standup-brief scheduler, the aging rule and the hourly
constraint check. Each of their runs queries the database, so with them on it would never pause. What that
costs you in behavior: an event the API failed to publish right after its commit is not retried, and scheduled
standup briefs, aging alerts and constraint checks do not happen. The nightly GitHub reconcile and the weekly
WIP advice stay on; each wakes the database once.

Left out against the specification's production design (section 13.1): the virtual network and private
endpoints, Front Door with a WAF, App Configuration, zone redundancy, Service Bus Premium and Content Safety.
`infra/main.prod.bicepparam` lists what to raise before real use.

### One-time setup per environment

1. Create the resource group, for example `az group create --name rg-kanbanhub-dev --location westeurope`.
2. Create an identity for GitHub Actions (an app registration or a user-assigned managed identity) with a
   federated credential whose subject is `repo:<owner>/<repository>:environment:dev`. On the resource group give
   it **Contributor** and **Role Based Access Control Administrator** (the template assigns roles to the apps' identity).
3. Create an Entra group for the SQL administrators and put that identity, and yourself, in it. The server
   accepts Entra sign-ins only; there is no SQL password anywhere.
4. Create the app registrations: `KanbanHub.Api`, exposing the scope `access_as_user` under
   `api://<its client id>`, and `KanbanHub.Web`, a single-page application allowed to request that scope.
5. In the GitHub repository create the environment (`dev`, `test`, `prod`) with these **variables**. None is a
   secret, and the repository stores no secret at all. Give `prod` required reviewers.

   | Variable | Value |
   | --- | --- |
   | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | The identity of step 2 and where it deploys |
   | `AZURE_RESOURCE_GROUP` | The resource group of step 1 |
   | `KH_SQL_ADMIN_OBJECT_ID`, `KH_SQL_ADMIN_NAME` | The object id and the name of the group of step 3 |
   | `KH_API_CLIENT_ID`, `KH_WEB_CLIENT_ID` | The client ids of the two app registrations of step 4 |

6. Deploy once with "Run workflow". The run's "Deploy the infrastructure" step prints the web address; add
   `<web address>/authentication/login-callback` to `KanbanHub.Web` as a single-page application redirect URI.

The pipeline then does the rest on every run: a what-if, the Bicep deployment, the database publish with its own
Entra token (which also lets the apps' identity into the database), both function apps, the web app with its
`appsettings.json` written for that environment, and a smoke test. The Static Web Apps deployment token is read
from Azure during the run instead of being stored as a secret.

### Optional features

Each is off until its parameter is set in `infra/main.<environment>.bicepparam`; the file shows the shape.

- **GitHub sync, agents and "Connect GitHub"** (`gitHub`, `agentsEnabled`). First register the GitHub App
  (section 9.1 of the specification) and put three things into the environment's Key Vault by hand: its private
  key imported as a **key** named `github-app-key`, and the **secrets** `github-webhook-secret` and
  `github-client-secret`. You need the Key Vault Crypto Officer and Secrets Officer roles for that. The webhook
  URL is `<API address>/api/github/webhook` and the callback URL is `<web address>/github/callback`.
- **AI** (`aiChatModel`, `aiEmbeddingModel`). Names a model and creates the Azure OpenAI resource. A board still
  gets no AI until an owner opts it in.
- **A budget alert** (`budget`): an amount, who to write to, and a start date.

### From a workstation

The same deployment without the pipeline, signed in with `az login`. Set the three ids the parameter file
reads, then preview and deploy:

```powershell
$env:KH_API_CLIENT_ID = '<client id>'; $env:KH_SQL_ADMIN_OBJECT_ID = '<object id>'; $env:KH_SQL_ADMIN_NAME = '<group name>'
```

```bash
az deployment group what-if --resource-group rg-kanbanhub-dev --parameters infra/main.dev.bicepparam
```

```bash
az deployment group create --resource-group rg-kanbanhub-dev --parameters infra/main.dev.bicepparam
```

To remove an environment, delete its resource group. Key Vault keeps a deleted vault for seven days; purge it
(`az keyvault purge`) if you want the name back sooner.

## The API

All routes are under `/api/v1`, take and return JSON, and answer errors as RFC 9457 problem details.
Every request needs an Entra access token for the API (`access_as_user` scope, or the `KanbanHub.Integration` app role).

| Area | Endpoints |
| --- | --- |
| Identity | `GET /me`, `GET /boards/{id}/me` (effective permission names) |
| Boards | `GET, POST /workspaces`, `GET, POST /boards`, `GET, PATCH, DELETE /boards/{id}` |
| Board settings | `PUT /boards/{id}/workflow`, `/wip-limits`, `/palette`, `/steps/{stepId}/done-rule`; `GET, PUT /boards/{id}/members`; `POST /boards/{id}/wip-calculator` |
| Backlog | `POST /boards/{id}/cards`, `PUT /boards/{id}/backlog/order` |
| Cards | `GET, PATCH /cards/{id}`; `POST /cards/{id}/move`, `/cancel`, `/block`, `/unblock`, `/breakdown`, `/checklist/{itemId}/toggle`; `GET, POST /cards/{id}/comments` |
| Insights | `GET /boards/{id}/audit`, `GET /boards/{id}/metrics/forecast` |
| GitHub | `POST /github/webhook` (HMAC-signed, not under `/v1`); `GET, POST /boards/{id}/github/links`, `DELETE /boards/{id}/github/links/{repositoryId}`; `POST, DELETE /cards/{id}/github/link`; `POST /cards/{id}/github/sync` |
| GitHub connection | `GET /github/connection`; `GET /github/connect` (answers with the address to send the browser to); `GET /github/callback?code=&state=` (called by the web app's callback page); `DELETE /github/connection` |
| Agents | `GET /cards/{id}/agent-dispatch` (what can be sent and why not, needs `board.read`); `POST` (agent, base branch, optional model and instructions) and `DELETE /cards/{id}/agent-dispatch` (both need `agent.dispatch`) |
| AI for a card | `POST /cards/{id}/ai/breakdown`, `/ai/triage`, `/ai/done-rule-check`, `/ai/agent-instructions` (all need `ai.use`; each returns a suggestion and changes nothing); `POST /cards/{id}/ai/duplicates/{otherCardId}/dismiss` (`card.edit`). Possible duplicates come with `GET /cards/{id}` |
| AI for a board | `GET /boards/{id}/ai/status`; `POST /boards/{id}/ai/forecast-narrative` (`ai.use` and `metrics.view`); `GET /boards/{id}/insights` (`insights.view`); `POST /boards/{id}/insights/{insightId}/dismiss`, `/accept` (`wip.set`) |
| Real-time | `POST /realtime/negotiate`, `POST /realtime/boards/{id}/join` (needs `board.read`) |
| Inbox | `GET /me/notifications`; `POST /me/notifications/{id}/read`, `/{id}/snooze`, `/read-all`; `PUT, DELETE /cards/{id}/mute`; `GET, PUT /me/notification-preferences` |

Moves and cancellations need the card's ETag in `If-Match` and an `Idempotency-Key`; edits need `If-Match`.
`PATCH` bodies are JSON merge patches: send only what changes, `null` to clear a value.

## How a move is decided

Every move passes the same gates, and a failure at any of them changes nothing.

| Gate | Where | Outcome when it fails | HTTP |
| --- | --- | --- | --- |
| Permission (`card.move`, `card.move.backward`) | `MoveCardHandler` | `Forbidden` | 403 |
| Legality (flow rules, backward moves need a reason) | `Transitions`, `MovePlan` | `IllegalTransition` | 422 |
| Concurrency (row version) | `kb.usp_MoveCard` | `ConcurrencyConflict` | 412 |
| Done rule (Active to Done) | `kb.usp_MoveCard` | `DoneRuleUnmet` | 422 |
| WIP limits | `kb.usp_MoveCard` with `kb.tvf_StepWipViolations` | `WipLimitReached`, `OverrideReasonRequired` | 409, 428 |

The procedure locks the card, then the target step, so concurrent moves into one step take turns.
`MoveOutcome` and `Outcome<T>` are C# 15 unions: the API's mapping to HTTP must handle every case or the build fails.

## How a change reaches other people

1. The procedure that changes a card writes a row to `kb.Outbox` in the same transaction.
2. Right after the request, the API publishes the pending rows: it tells the board's SignalR group
   (`board:{id}`), then sends the event to the Service Bus topic `card-events`.
3. Every browser showing that board refetches its snapshot. A push says "this board changed", nothing more.
4. The workers' `notifications` subscription decides who should hear about it, stores a notification per
   person (once, however often the event arrives), pushes "your inbox changed" and sends email to those who asked.
5. A timer in the workers publishes any event the API left behind, so none is lost.

| Event | Who is told | Can be turned off? |
| --- | --- | --- |
| Card blocked or unblocked | The card's creator and the board's Owners and Flow Managers | Yes |
| New comment | The card's creator and everyone who commented before | Yes |
| WIP limit overridden | The board's Owners and Flow Managers | Email only; it always reaches the inbox |
| Card aging: in flow longer than 85% of its kind took | The card's creator and the board's Owners and Flow Managers, once per card | Yes |

Nobody is told about their own action, and muting a card silences everything about it except overrides.

## The GitHub sync

A GitHub App links boards to repositories. Changes travel both ways, and a ledger of what was last synced keeps
them from bouncing back.

| KanbanHub | GitHub | Direction |
| --- | --- | --- |
| Title, description | Issue title, body | Both; last writer wins |
| Card type, priority bucket | `type:<type>`, `priority:P0` to `priority:P3` labels | Both |
| Step and sub-column | `kanban:<step>`, `kanban:done` labels | KanbanHub writes; adding one in GitHub asks for a move |
| Blocked | `kanban:blocked` label | Both |
| Comments | Issue comments | Both, append only |
| Finished, canceled | Closed as completed, as not planned | KanbanHub closes; a close in GitHub marks the card for the team |
| Where the card is, its step's WIP, why it is blocked | One status comment, edited in place | KanbanHub writes |
| Done rule of the card's step | "Kanban done rule" check run on pull requests that say `Fixes #n` | KanbanHub writes |

- **Nothing from GitHub bypasses the board's rules.** A label or a `/kanban` command asks for a move like anyone
  else would. If WIP, the done rule or the flow says no, the card stays, shows a "Sync issue" badge, the label is
  taken off again and the status comment says why.
- **Commands in issue comments,** for people with write access to the repository: `/kanban move next`,
  `/kanban block <reason>`, `/kanban unblock`, `/kanban track`. The app answers on the issue.
- **The app is its own member of the board.** Linking a repository gives it the Member role, visible under
  Settings, Members; give it another role there to change what GitHub can do.

## Sending cards to coding agents

Someone with `agent.dispatch` (Owners, and the Agent Operator role given on top of Member) and a connected
GitHub account can send a card's issue to GitHub Copilot. Everything that knows GitHub's agent APIs sits behind
`IAgentDispatcher` in `KanbanHub.Agents`, and `Agents:Enabled` switches the feature on per environment.

1. **Eligible.** The card is linked to an issue and sits in Active of a step marked "open to agents" (Implement
   in the quick-start template), or waits in Done of the step before.
2. **WIP first.** A waiting card is pulled into the agent step as the person who sends it, through the same gates
   as any move. If the step is full, nothing is sent.
3. **A slot.** The board allows a number of open sessions (3 unless changed). The slot is taken in the database
   under a lock, so two people sending at once can't exceed it.
4. **Sent.** The issue is assigned to `copilot-swe-agent[bot]` through the issues REST API with an
   `agent_assignment` (repository, base branch, instructions, model), using the person's own GitHub token:
   GitHub refuses the app's installation token here. If GitHub refuses or can't be reached, the slot is given back.
5. **Tracked.** The card shows "Agent working" until the agent's pull request arrives ("Fixes #n", opened by the
   agent), then the pull request and its state. Merging or closing it ends the session, and so does unassigning
   the agent on GitHub before it opened one. The card is never moved for you.

| Guardrail | How |
| --- | --- |
| Card text is untrusted | Instructions follow a fixed template: the team's words, the step's done rule, then the card's title and description between markers, with the agent told to treat that part as data |
| No credentials | A scanner blocks the dispatch when the instructions or the card look like they contain a token, key, password or connection string secret |
| Only the issue's own repository | The agent works in the repository the card's issue lives in, which is linked to the board |
| Audited | Who sent which card to which agent, with the SHA-256 of the instructions rather than their text |
| Tokens | Only a person's refresh token is stored, encrypted with a Key Vault key; access tokens stay in memory |

Limits of this phase (Copilot only, no agent tasks API, no auto-move on merge and more) are in
[DECISIONS.md](DECISIONS.md), items 97 to 106.

## AI: suggestions and insights

AI reads a board's own data and proposes; people decide. It never moves a card, changes a limit or closes an issue.

| Feature | When | What comes back |
| --- | --- | --- |
| Breakdown assistant | "Suggest tasks" when breaking a card down | Child tasks of under a week each, with acceptance criteria, put into the editable list |
| Triage | "Suggest type and priority" on a card | Card type, priority bucket and origin, put into the form with the reason |
| Done-rule check | "Check with AI" under a card's done rule | The items that look unmet, with the evidence; advisory |
| Agent instructions | "Draft with AI" when sending a card to an agent | A draft to edit; it passes the same credential scan as typed instructions |
| Forecast narrative | "Explain the forecast" on the board | The book's forecast in a few sentences |
| Standup brief | Half an hour before the board's standup time | What finished, what is blocked, what is aging, where to swarm |
| Constraint finder | Hourly | Which step is the constraint, when cards pile up in the Done column before it |
| WIP advisor | Mondays | Changes of one to a step's limit, each to accept or dismiss |
| Aging alerts | Every 15 minutes, by rule, on every board | A notification when a card has been in flow longer than 85% of the finished cards of its type took (from at least five, over 90 days). On boards that use AI the model words it |
| Duplicate finder | When a card is created or its text changes | "Possible duplicates" on both cards: the board's cards whose text is at least 85% alike, by embedding distance |

Every feature runs through the same four gates.

1. **Opt-in and budget.** A board uses AI only after an owner switches it on, and only while it is under its
   monthly token budget. The call that crosses the budget finishes; the next one is refused until the month ends.
2. **Data, not instructions.** The model gets fixed instructions plus the board's data as one JSON document.
   Card and issue text travels only inside that document, and the instructions say it is data.
3. **Counted in SQL.** The model is never asked to count: steps, WIP, days waiting and the like are aggregated
   by `FlowFactQueries` first, and the constraint itself is found by rule. The model words what the numbers say.
4. **Grounding check.** The answer must be a C# record, may cite only cards and steps it was shown, and may
   state only numbers that were in the data. Anything else is dropped and logged, and nothing is shown.

An insight shows the points and cards behind it, with titles taken from the board, never from the model.
Accepting a proposed WIP limit is the only way a suggestion becomes a change: it needs `wip.set`, goes through
the same use case as setting the limit by hand, and is audited as the decision of the person who accepted.

The duplicate finder asks no chat model: card text goes to the embedding model, which answers with numbers, and
SQL Server compares the vectors (`VECTOR_DISTANCE` over a `vector(1536)` column). It passes the same opt-in and
budget gate as everything else.

What is not built (Content Safety screening, a golden evaluation set, linking duplicates and more) is in
[DECISIONS.md](DECISIONS.md), items 107 to 121.
