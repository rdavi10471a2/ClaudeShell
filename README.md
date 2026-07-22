# ClaudeShell

A **minimal Claude coding shell** — Blazor UI + Node sidecar running Claude with all native tools enabled. No governance, no indexing, no workflow. Just Claude.

**This is a blank canvas.** Forks add domain-specific logic via MCP servers and Agent SDK patterns.

---

## Documentation

Full docs live in **[`docs/`](docs/)** — start at **[docs/README.md](docs/README.md)** for a
guided reading path and a system diagram. Highlights:
[Architecture](docs/architecture/Architecture.md) ·
[Components](docs/components/) (one page per module, with Mermaid diagrams) ·
[User Guide](docs/guide/) ·
[Decisions](docs/decisions/).

## Why this exists

ClaudeShell extracts the **shell concept** from ClaudeWorkbench: a simple UI for Claude with optional MCP servers, but **no built-in governance or workflow**. 

**This implementation currently includes AIMonitor** (indexing, staging, review gates, etc.) — that's what the host does. But it's **not a requirement**. You can:
- Strip the host down to pure chat UI (fork and simplify)
- Replace it entirely with your own Blazor/web app
- Use it as-is if you want the indexing + governance

The key: **BasicSidecar is governance-free.** The host/governance layer is optional, replaceable, or completely removable.

The move to Claude is deliberate: **real skills, hooks, and a programmatic operator gate** instead of policy prose you fight every turn.

## The governed loop

```
choose workspace → discover (index) → refresh_file / new_file → governed edit
   → stage session → operator review → accept / reject → post-accept reindex
```

- **Reason in the cloud, edit locally.** The model reasons from compact context; watched-source changes are composed against explicit local Working candidates and promoted only through review.
- **The gate is code, not a prompt.** Mutations (file writes, `accept_staged_review`) are intercepted by the sidecar's `PreToolUse` hook, surfaced to the Blazor UI, and applied only on operator approval.
- **Review is an in-app diff/merge — [DiffPlex](https://github.com/mmanela/diffplex).** The staged candidate vs. current watched source is rendered by DiffPlex in a resizable **Merge Review** dialog (`Components/Dialogs/MergeReviewDialog.razor`); the operator accepts/rejects there and the Accept writes watched source. There is **no external diff tool** (no WinMerge) in the path.
- **Freshness is restored at accept.** The solution index rebuilds after an accepted decision — that is the normal point where downstream truth is refreshed.

## Architecture

```
Blazor host (ClaudeShell)  ── spawns ──►  BasicSidecar (Node, Claude Agent SDK)
   │  renders UI + event stream             │  drives Claude with all native tools
   │  (no built-in governance)              │  streams tool/turn events back to the host
   │                                        │  delegates domain logic to MCP servers
   └── (optional) AIMonitor engine
        ↑ only if you fork for governance
```

**ClaudeShell is pure Claude.** No governance, no workflow, no workspace juggling. Just:
- **Blazor host** — renders the chat UI, streams events from the sidecar
- **BasicSidecar** — runs Claude with all native tools (no deny-by-default)
- **launch-shell.ps1** — starts everything (host + sidecar) in one command

**Forks add domain logic via:**
- **MCP servers** (specialized capabilities)
- **Sidecar middleware** (see `sidecar/samples/ai-monitor-workflow/` for governance patterns)
- **Custom launcher UI** (see `samples/ai-monitor-launcher/` for a workspace manager example)

Details, including exactly how the sidecar registers the MCP surface and the logging model, are in
**[docs/architecture/Architecture.md](docs/architecture/Architecture.md)** (or the guided **[docs/](docs/)** index). Short version:

- **MCP binding** — the sidecar is the MCP client; the Agent SDK connects to the engine's MCP server via its `mcpServers` option (recommended: the Blazor host serves MCP in-proc over HTTP; the sidecar registers a URL). Tools appear to the agent as `mcp__claude-workbench__*` (the HTTP host advertises `serverInfo.name` `claude-workbench` on port `6100`, distinct from the real `ai-monitor`). No proxy or bridge in the path.
- **Auth** — **subscription verified for personal use** (see [the guide](docs/guide/settings-and-usage.md#auth)). A full turn ran headless with no `ANTHROPIC_API_KEY`: the SDK inherits the local `claude` CLI's cached subscription login. `ANTHROPIC_API_KEY` (API billing) is only needed to ship to other people, not to run this yourself.
- **Logging** — the engine logs to a JSON-lines file (and raises in-proc events for a live view); MCP-call telemetry is re-emitted from the sidecar's `tool_use`/`tool_result` events and hooks, not sniffed off a pipe.
- **Single-start + role card** — the host **launches and supervises the sidecar** as a child process (`SidecarProcessHost`; skips if a sidecar is already on the port, kills it on shutdown; override via `Sidecar:AutoStart` / `Sidecar:NodeExecutable` / `Sidecar:Directory`). The agent is oriented by an injected **governed role card** (SDK `systemPrompt`, `preset: claude_code` + append) so it knows the read-only + staging contract from turn one instead of discovering it by hitting a deny. There is **no `CLAUDE.md`** (isolation via `settingSources: []`); all guidance is injected programmatically.
- **Agent workspace (CWD & tools)** — the agent's working directory is the **watched solution's folder** (auto-derived from the host's config, so it tracks whatever `WatchedSolutionPath` points at). Tool access is **deny-by-default**: the only native tools allowed are the read-only `Read`/`Grep`/`Glob` (+ `ToolSearch`/`TodoWrite`); the `claude-workbench` MCP tools are allowed (mutations paused at the operator gate); **everything else is denied** — `PowerShell`, `Bash`, `Write`/`Edit`, `Agent`, `Workflow`, `WebFetch`, and any future/unknown tool. So the watched workspace is read-only to the agent and every change must go through the governed MCP (`submit_file` → stage → operator review). Native read tools are optional **per turn** (`POST /prompt { readTools: false }`) to force *all* access — reads included — through the MCP surface. `strictMcpConfig: true` exposes only `claude-workbench`; the machine's account/user MCP connectors (e.g. claude.ai connectors) do **not** leak in.

## Repository layout

```
src/
  AIMonitor.Core/        settings, workspace paths, stable identifiers
  AIMonitor.Logging/     thin sink: IMonitorLogger + JsonLinesMonitorLogger + in-proc MonitorLogService
  AIMonitor.MSBuild/     MSBuild/Roslyn project + document loading
  AIMonitor.Data/        SQLite solution index store
  AIMonitor.Workflow/    edit sessions, staging, review gates
  AIMonitor.Indexing/    Roslyn semantic extraction → index
  AIMonitor.McpServer/   MCP tool surface (governed discovery + mutation + review) — stdio console host
  ClaudeWorkbench.Host/  in-proc ASP.NET host: same tool surface over Streamable HTTP (:6100) + /health
scripts/                 publish-live.ps1 — Release build of host+sidecar into one folder
                         launch-shell.ps1 — Direct launcher (no GUI, starts immediately)
tests/
  unit/                  xUnit per-layer tests (incl. the language corpus, Data.Tests/Corpus)
  integration/           end-to-end over the MCP surface + engine
samples/
  watched-solutions/     Test fixtures for integration tests
  ai-monitor-launcher/   EXAMPLE: workspace manager UI (WinForms, folder picker, config)
                         ↑ not part of ClaudeShell; shows how to build a fork-specific UI
docs/                    developer + user docs — start at docs/README.md
  architecture/          C4 architecture, the governed loop, the two gates
  components/            one page per module (C4 component level, Mermaid)
  guide/                 user help (getting-started, merge-review, git-panel, …)
  decisions/             ADRs (why the gate is code, two-process, argv git, …)
sidecar/
  basic/                 BasicSidecar (default: all tools allowed, no governance)
  samples/
    ai-monitor-workflow/ Example: how to add governance + staging on top of BasicSidecar
```

Project/namespace names are kept as `AIMonitor.*` from the extraction so the port stayed mechanical and the ported tests prove fidelity. Rebranding, if ever wanted, is an isolated later pass.

## Requirements

ClaudeWorkbench is a **two-process** app — a .NET Blazor host **plus a Node sidecar that runs the Claude Agent SDK** — so any machine that runs it needs *all* of:

| Requirement | Why | Notes |
|---|---|---|
| **.NET 10 SDK** | Blazor host + extracted engine + in-proc MCP server; the **SDK** specifically, because indexing goes through MSBuild/Roslyn | `net10.0`. This is what ClaudeWorkbench runs on — **not** a constraint on the solution you watch (see below) |
| **Node.js** (LTS; tested on v24) | The sidecar runs the **Claude Agent SDK, which is Node-only** — there is no .NET Agent SDK | Runtime is small (~50–90 MB) and can be **bundled as a single-file executable (Node SEA)** — no system-wide install needed |
| **`claude` CLI** | The Agent SDK **spawns the `claude` binary** | **Bundled with the SDK** — the `@anthropic-ai/claude-agent-sdk-win32-x64` package ships/extracts the CLI; **no separate install**. This is most of the ~300 MB `sidecar/node_modules`. |
| **A Claude login** | Auth | A **subscription login** (cached in `~/.claude`) runs it for yourself with **no API key**; an `ANTHROPIC_API_KEY` is only needed to ship to *other* users |
| Ports **6100** (host) + **6110** (sidecar) | The two processes talk over localhost HTTP/SSE | configurable |

> **The watched solution does not have to target `net10.0`.** The index is built via
> `MSBuildWorkspace`, so the requirement is only that the installed SDK can evaluate the project.
> Verified by indexing, with real symbol extraction: `net10.0` (Razor/Web/WinForms/console
> fixtures), `net9.0` and `net9.0-windows` (daily use), `net8.0`, and — contrary to expectation —
> a **legacy non-SDK-style `net472` `.csproj`** (`ToolsVersion`, explicit `<Compile Include>`,
> `packages.config`). Both of the last two ship as fixtures under `samples/watched-solutions/`.
> Caveat: those fixtures are simple. A real legacy solution is likelier to fail on a *dependency*
> — unrestored `packages.config`, a custom or VS-only `.targets` import — than on its project format.

> **Deployment footprint:** the agent driver is the Node-based Agent SDK, which bundles the `claude` CLI. So a target needs the **.NET runtime + a Node runtime (small, bundleable) + the sidecar folder (`dist` + `node_modules` ≈ 300 MB, self-contained CLI included) + a Claude login**. There's no pure-.NET/no-Node build, but Node itself is light and can be shipped as a single binary. WinMerge is **not** required (review/merge is in-app).

### Sidecar setup

```powershell
cd sidecar
npm install      # restores @anthropic-ai/claude-agent-sdk + express
npx tsc          # builds dist/  (host launches it via `node dist/index.js`)
```

Ship `sidecar/dist` + `node_modules` (or run `npm ci` on the target).

## Deploy — publish a live install

To run it like an app rather than out of the checkout:

```powershell
.\scripts\publish-live.ps1            # -> C:\ClaudeWorkBenchLive + a Desktop shortcut
```

That publishes the **Blazor host, the sidecar and the Launcher** side by side in one folder:

```
C:\ClaudeWorkBenchLive\
  host\       ClaudeWorkbench.Host.exe + config\
  sidecar\    dist\index.js + production node_modules
  launcher\   ClaudeWorkbench.Launcher.exe
  samples\    CalculatorSample — seeded as a workspace on first run, so there's something to Start
  runtime\    one folder per workspace, created on first Start
```

The **Launcher** runs several watched solutions side by side — each with its own port, runtime
and index, all held in one Windows Job Object so an instance's host + sidecar + browser start and
die together. Instances provision into `<install>\runtime\<workspace>`.

The install is location-independent: the Launcher exe can sit anywhere (a shortcut, a Release
build, this publish folder) and still finds its workbench, because stored paths are kept relative
to the workbench root and stale ones are re-guessed. Re-running the script updates an install
**without touching `runtime\`**, so workspaces and indexes survive — just close the Launcher
first. Switches: `-Destination`, `-Configuration`, `-Clean`, `-NoShortcut`.

Targets need the **.NET 10 SDK** (the runtime to start, plus MSBuild/Roslyn to index a solution),
**Node.js** on PATH, and a **Claude login**; the `claude` CLI ships inside the Agent SDK package. Full detail, including the path-resolution rules and the
build-machine requirements: **[docs/guide/deploying.md](docs/guide/deploying.md)**.

## Build & test

```powershell
dotnet build ClaudeWorkbench.slnx
dotnet test  ClaudeWorkbench.slnx
```

The engine builds with **0 errors** and no WinForms/proxy/bridge. Current test state:
**224 tests — 218 pass · 6 skipped · 0 failed.**

Grouped by what is covered rather than by project, because the project layout says where code
lives, not what is tested. Full breakdown, suite by suite: **[docs/guide/testing.md](docs/guide/testing.md)**.

| Capability | Tests |
|---|---|
| Semantic index & language coverage (incl. the 42-case language corpus) | 74 · 5 skipped |
| MCP tool surface (out-of-process, real JSON-RPC) | 50 |
| Edit workflow & staging | 35 |
| Host & infrastructure (git panel, settings, logging) | 25 |
| Review gates & decisions (incl. ADR-0005 session atomicity) | 22 |
| Sample-driven authoring over `samples/watched-solutions/` | 18 |
| **Total** | **224** |

- **Everything runs under `dotnet test`.** No console runners, no flags to remember. Three such
  runners and `AIMonitor.Cli` were retired ([plan](docs/plans/retire-legacy-test-harness.md)):
  none of them could fail a build, so none of them was coverage. What they genuinely covered was
  moved into the suites above *first*.
- **The MCP surface has no unit-test project on purpose.** It is a thin wrapper over engine
  services that are already unit-tested; what breaks in it — tool registration, JSON-RPC
  serialization, watched-relative path resolution, the operator gate — is only observable across
  a process boundary. So its 50 tests boot a real server and speak the real protocol.
- **Six skips, all in the index category, both deliberate and countable.** Five are language-corpus
  cases the harness cannot express (it synthesises a single project; those need more than one).
  The sixth is the `razor-generated:*` rows, which only index when the host Roslyn matches the
  SDK's Razor source generator — environment-dependent, so documented rather than pinned.
- **Integration runs ~9-13m** depending on load (real MSBuild/Roslyn loads and a real
  `dotnet build` per gate test). It degrades sharply under contention — budget accordingly.
- **Known gaps are listed** at the end of the testing guide rather than left implied — ADR-0006 is
  enforced in the Node sidecar and is not covered by `dotnet test`.

## Roadmap

- [x] Extract the AIMonitor engine, layer by layer, no WinForms/proxy/bridge, tests green
- [x] In-proc ASP.NET MCP endpoint on the engine's tool classes (`ClaudeWorkbench.Host`, Streamable HTTP on `:6100`, server name `claude-workbench`, full 60-tool surface smoke-verified)
- [x] `claude-sidecar` (Agent SDK): drives Claude, registers the `claude-workbench` MCP over HTTP, `canUseTool` operator gate (read-only auto-allow, mutations pause), neutral SSE event stream — end-to-end verified on the subscription (read-only turn)
- [x] Blazor host: workspace picker + runtime provisioning, Tasks/Workbench/Source/Activity tabs, live transcript, operator gate dialog, in-app **merge review** (DiffPlex, terminal-only build/reindex)
- [x] Session continuity (`resume`) + New Thread; agent turn survives a host rebuild mid-turn
- [x] `AskUserQuestion` → operator **questions dialog** (tabs, choice cards, always-on "Other" free-text) via `canUseTool`
- [x] Auto-approve toggle (per-thread) + Stop button
- [x] **File upload** — streaming input mode; per-workspace `uploads` folder provisioned + granted via `additionalDirectories`; composer attach/drop UI (text, code, and images/PDFs via the agent's Read)
- [x] **Context/usage meters** — dropdown off the SDK `Query` handle (`getContextUsage` + experimental `usage`): context fill + auto-compact headroom, weekly/5-hour utilization, plan
- [x] **Model + reasoning-level selector** — settings-dialog dropdowns → per-thread `model` + `effort` on the sidecar query options
- [x] **Tasks kanban board** — ported from CodexAppServerDemo (Radzen; right-click state moves; single-Active invariant), over the existing `board.sqlite`. **UI tab currently disabled** (unpublished WIP) pending the thread↔task workflow; board + MCP tools remain, role card is free-flowing (opt-in)
- [x] **Task MCP loop** — `get_current_task` (task + user/agent notes content), `list_tasks`, `update_agent_notes` (agent task-memory to `planning/task-memory`, never watched source)
- [x] **Single-start** — host launches + supervises the sidecar; injected **governed role card** as `systemPrompt` so the agent knows its read-only + staging role from turn one
- [ ] **Thread ↔ task workflow** (design pending): on New Thread, prompt *save-as-task / keep-as-discussion / discard*; auto-name threads `discussion-<datetime>`; add a `task_id` link so a task groups its threads; thread-provenance on agent notes
- [ ] Bring in AIMonitor's `docs/claude-skills/` cards as injected skill-cards (guidance is MCP-served today via `get_staging_guide` + the role card)

## Related projects

Other open-source servers also expose Roslyn / C# semantics to AI agents over MCP — worth a look for comparison:

- **[Roslyn CodeLens](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp)** (`MarcelRoozekrans/roslyn-codelens-mcp`) — a Roslyn-based MCP server providing semantic code intelligence for .NET codebases (type hierarchies, call sites, DI registrations, reflection usage) for Claude Code.
- **[RoslynMCP](https://github.com/carquiza/RoslynMCP)** (`carquiza/RoslynMCP`) — an MCP server providing C# code-analysis capabilities (wildcard symbol search, reference tracking, dependency and complexity analysis) using Microsoft Roslyn.

ClaudeWorkbench overlaps on the Roslyn-over-MCP idea but differs in intent: it is not only read/analysis but a **governed edit loop** — staged local *Working* candidates, a human accept/reject **merge gate** (DiffPlex), and post-accept reindex — with the Roslyn semantic index as one part of that workflow rather than the whole product.

## Provenance

Lineage: [AIMonitor](https://github.com/rdavi10471a2/AIMonitor) (engine) + CodexAppServerDemo (Blazor control-surface pattern) → ClaudeWorkbench (Claude backend). The engine here is a faithful extraction; identical code compiles and the ported tests pass.
