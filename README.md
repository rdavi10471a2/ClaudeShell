# ClaudeShell

A **minimal desktop shell for Claude**: a Blazor web UI plus a small Node sidecar that
drives Claude through the **Claude Agent SDK**. No system prompt, no governance, no
integrations — plain Claude with all of its native tools, where **every tool call
pauses for your Allow/Deny** before it runs.

Use it as-is for a local Claude console, or fork it as the starting shell for your own
workflow: the sidecar is the single place where prompt, tools, and permissions are
decided.

```
Blazor host (:5000)  ── spawns ──►  BasicSidecar (:6110, Node + Claude Agent SDK)
   chat UI, permission dialog,        drives Claude (empty prompt, all tools)
   questions dialog, usage meters     gates every tool call on the operator
   IOperatorConsole seam              streams events back over SSE
```

## What you get

- **Assistant tab** — transcript with markdown, composer with file attachments,
  per-message copy, pop-out chat history, activity log modal.
- **Permission gate** — each tool call (edit a file, run a command, fetch a page)
  pops an Allow/Deny dialog. Nothing runs silently.
- **Questions dialog** — when Claude asks a clarifying question
  (`AskUserQuestion`), you answer in a card UI with an always-available free-text.
- **Usage meters** — live context fill, weekly/5-hour subscription utilization,
  read straight off the Agent SDK query handle.
- **Model & reasoning settings** — pick model and effort per thread.
- **Session continuity** — threads resume across restarts; New Thread starts clean.
- **Nothing injected** — `systemPrompt: ""`, `settingSources: []`: no CLAUDE.md, no
  personal settings, no persona. What Claude knows about its environment comes only
  from its own tool schemas.

## Requirements

| Requirement | Why |
|---|---|
| **.NET 10 SDK** | the Blazor host |
| **Node.js** (LTS) | the sidecar runs the Claude Agent SDK (Node-only) |
| **A Claude login** | a subscription login cached by the `claude` CLI is enough — the CLI ships inside the Agent SDK package, no API key needed. **Not signed in anywhere yet?** The Launcher's **Claude sign-in** button is the login path: it opens the CLI's interactive sign-in (using the bundled CLI if none is installed). Skip it if Claude Code or the `claude` CLI is already signed in on this machine. |

## Run it

```powershell
cd sidecar/basic
npm install
npm run build        # -> dist/

dotnet run --project src/ClaudeWorkbench.Host    # UI on http://localhost:5000
```

The host launches and supervises the sidecar itself (skips if one is already on the
port, kills it on shutdown). The agent works in `%TEMP%\ClaudeShell` by default —
override with the `WORKSPACE` environment variable. Composer attachments land in a
`files/` subfolder there.

## Multiple sessions

Each session is one host process with its own port pair and workspace:

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5001"
$env:Sidecar__Port   = "6111"
$env:WORKSPACE       = "C:\somewhere\else"
dotnet run --project src/ClaudeWorkbench.Host
```

Or use the **WinForms launcher sample**:

```powershell
dotnet run --project samples/launcher
```

Create/start/stop named sessions with auto-assigned ports; a session started on a
launcher-created temp workspace has its folder **deleted when it stops**, so scratch
sessions leave nothing on disk. The Launcher's **Claude sign-in** button drops a menu
(sign in / check status / sign out) that runs the CLI's interactive auth in its own
console — the login is cached per machine and shared by every session.

## Layout

```
src/ClaudeWorkbench.Host/   Blazor host — assistant tab, gate + questions dialogs, settings
sidecar/basic/              BasicSidecar — Node driver on the Claude Agent SDK
samples/launcher/           WinForms multi-session launcher sample
scripts/                    publish-live.ps1 (install + Launcher shortcut) · launch-shell.ps1
```

## Docs & tests

This repo is deliberately small, and so is its documentation surface:

- **This README** — setup, running, multi-session, fork points.
- **[`sidecar/README.md`](sidecar/README.md)** — the sidecar's HTTP/SSE contract and env vars.
- **In-app Help** (the Help button in the UI) — the user guide: the permission gate,
  the questions dialog, composer controls, workspace and sessions.
- The **About page** (`/about`) shows the live install's version, ports, and paths.

There are **no automated tests here** — the governed test suites belonged to the
engine this shell was factored out of and left with it. The sidecar has
`npm run typecheck`; the solution builds with `dotnet build ClaudeWorkbench.slnx`.

## Make it yours

`sidecar/basic/index.ts` is the whole policy surface:

- `systemPrompt` — put your role card here (empty on purpose).
- `canUseTool` — decide what gets gated, auto-allowed, or denied.
- SDK options — register MCP servers, change `settingSources`, restrict tools.

The host UI binds only to `IOperatorConsole`/`IApprovalQueue`
(`src/ClaudeWorkbench.Host/Console`), so the whole backend can be swapped without
touching the UI.

For a **more detailed, fully-worked example** of building a real workflow on this
shell — governed edits, staging, review gates, a Roslyn code index — see the
**[ClaudeWorkbench repository](https://github.com/rdavi10471a2/ClaudeWorkbench)**.
