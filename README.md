# ClaudeShell

A **minimal desktop shell for Claude**: a Blazor web UI plus a small Node sidecar that
drives Claude through the **Claude Agent SDK**. No system prompt, no governance, no
integrations — plain Claude with all of its native tools, where **anything that writes,
runs a command, or reaches the network pauses for your Allow/Deny** before it runs.
Read-only inspection (`Read`/`Grep`/`Glob`) is allowed without a prompt, matching the
real Claude Code client; [that line is one edit away](#make-it-yours) if you want it
stricter or looser.

Use it as-is for a local Claude console, or fork it as the starting shell for your own
workflow: the sidecar is the single place where prompt, tools, and permissions are
decided.

If you're looking for a working example of **browser-based tool approval with the
Agent SDK** — a `canUseTool` hook that forwards each approval request to the browser
over SSE and blocks until the user answers, plus streaming responses and session
resume behind an HTTP API — that is exactly what this repo implements
(single-operator, local; see [`sidecar/basic/index.ts`](sidecar/basic/index.ts)).

```
Blazor host (:5000)  ── spawns ──►  BasicSidecar (:6110, Node + Claude Agent SDK)
   chat UI, permission dialog,        drives Claude (empty prompt, all tools)
   questions dialog, usage meters     gates writes/commands/egress on the operator
   IOperatorConsole seam              streams events back over SSE
```

## What you get

- **Assistant tab** — transcript with markdown, composer with file attachments,
  per-message copy, pop-out chat history, activity log modal.
- **Permission gate** — writes, commands (`Bash`/`PowerShell`), and network calls pop
  an Allow/Deny dialog with the actual command/URL/skill shown. Read-only tools
  (`Read`/`Grep`/`Glob`, plus `TodoWrite`/`ToolSearch`) run without a prompt; change
  the `AUTO_ALLOWED` set in `sidecar/basic/index.ts` to gate everything (or less).
- **Inline images** *(moderately tested)* — when the agent writes a file and references
  it as `![](path)` — or just `Read`s a local image — it renders inline in chat, served
  through a workspace-scoped `/local-file` endpoint. The endpoint's security (only files
  under the workspace or ones the agent read/wrote this thread) is unit-verified; the
  rendering works in normal use but hasn't been hammered. Known gaps: base64 image blocks
  straight off the tool stream aren't rendered, and git-bash `/tmp/...` paths that Windows
  can't resolve fall back to a plain tool line.
- **Questions dialog** — when Claude asks a clarifying question
  (`AskUserQuestion`), you answer in a card UI with an always-available free-text.
- **Usage meters** — live context fill, weekly/5-hour subscription utilization,
  read straight off the Agent SDK query handle.
- **Model & reasoning settings** — pick model and effort per thread.
- **Session continuity** — threads resume across restarts; New Thread starts clean.
- **Diagrams & code** *(moderately tested)* — a ```mermaid fence renders inline as an
  SVG (mermaid vendored locally, no CDN, `securityLevel:'strict'`); other code fences get
  lightweight in-house syntax highlighting (no CDN). The agent must actually emit the
  fence — there's no auto-diagram fallback the way there is for images.
- **Security** — model output is untrusted (it can launder file/web content via prompt
  injection), so the renderer escapes raw HTML (`<script>`/`<iframe>`/`<img onerror>` become
  text) and downgrades external `<img>` URLs to click-through links; a CDN-free
  Content-Security-Policy backs it up.
- **Almost nothing injected** — `settingSources: []` (no CLAUDE.md, no personal settings,
  no coding persona). The *only* injection is a short **display hint** (`DISPLAY_NUDGE` in
  `sidecar/basic/index.ts`) telling Claude it's in a chat UI that renders images and
  mermaid diagrams inline — nothing about tools, workflow, or persona. Set it to `""` for
  a truly empty prompt.

## Requirements

| Requirement | Why |
|---|---|
| **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** | the Blazor host |
| **[Node.js](https://nodejs.org/) (LTS)** | the sidecar runs the Claude Agent SDK (Node-only); [npm](https://www.npmjs.com/) ships with it and installs the sidecar's dependencies |
| **A Claude login** | a subscription login cached by the [`claude` CLI (Claude Code)](https://docs.claude.com/en/docs/claude-code/overview) is enough — the CLI ships inside the Agent SDK package, no API key needed. **Not signed in anywhere yet?** The Launcher's **Claude sign-in** button is one login path: it opens the CLI's interactive sign-in (using the bundled CLI if none is installed). Installing the [**Claude Code extension for VS Code**](https://docs.claude.com/en/docs/claude-code/vs-code) and signing in there works too — the login is cached per machine in `~\.claude` and shared by every session. Skip both if Claude Code or the `claude` CLI is already signed in on this machine. |

## Run it

```powershell
cd sidecar/basic
npm install
npm run build        # -> dist/

dotnet run --project src/ClaudeWorkbench.Host    # UI on http://localhost:5000
```

The host launches and supervises the sidecar itself (skips if one is already on the
port, kills it on shutdown). The agent works in `%LOCALAPPDATA%\ClaudeShell\workspace` by default —
override with the `WORKSPACE` environment variable. Composer attachments land in a
`files/` subfolder there.

### From VS Code

The repo ships a `.vscode/` config so you can build and debug with **F5**. You need the
[**C# Dev Kit**](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit)
extension (it pulls in the .NET debugger); Node just needs to be on `PATH`.

1. Open the repo folder in VS Code (`code .` from the repo root).
2. Press **F5** (or Run ▸ *Run ClaudeShell (host + sidecar)*).

The launch runs a **`build all`** task first — it does `npm install` + `npm run build`
in `sidecar/basic`, then `dotnet build` on the host — so a fresh clone works on the first
F5 with no manual setup. When the host reports *"Now listening on…"*, VS Code opens
http://localhost:5000 in your browser; the host spawns and supervises the sidecar as usual.

What the config provides:

- **`.vscode/launch.json`** — the *Run ClaudeShell (host + sidecar)* launch profile
  (`ASPNETCORE_URLS=http://localhost:5000`; edit `env` there to change the port).
- **`.vscode/tasks.json`** — `sidecar: npm install`, `sidecar: build`, `host: build`, and
  the composite `build all`. Run any of them standalone via *Terminal ▸ Run Task…*.

Subsequent F5s reuse the installed `node_modules`, so they only rebuild what changed.

Not signed in to Claude yet? If you install the
[**Claude Code extension for VS Code**](https://docs.claude.com/en/docs/claude-code/vs-code)
and sign in, that caches a machine-wide login in `~\.claude` that ClaudeShell picks up — no
separate sign-in needed.

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

## Publish a live install

`dotnet run` is for development. To get an **installed, double-clickable ClaudeShell** —
the host, sidecar, and Launcher side by side with a desktop shortcut — run the publish
script from the repo root:

```powershell
.\scripts\publish-live.ps1                          # -> C:\ClaudeShellLive
.\scripts\publish-live.ps1 -Destination D:\ClaudeShell -Clean
```

It publishes the host and Launcher (`dotnet publish -c Release`), builds the sidecar with
[npm](https://www.npmjs.com/), and mirrors the sidecar's `node_modules` into the output.
The result is a self-contained **install root** that works wherever you move the folder:

```
<Destination>\            (default C:\ClaudeShellLive)
    host\        ClaudeWorkbench.Host.exe   — the Blazor app
    sidecar\     dist\index.js + node_modules — the Claude Agent SDK driver
    launcher\    ClaudeShell.Launcher.exe   — the multi-session manager
    scripts\     launch-shell.ps1           — single session, no Launcher
    ClaudeShell Launcher.lnk                — shortcut (also placed on the Desktop)
```

The Launcher finds the host at `<root>\host` and the sidecar at `<root>\sidecar`.

**Quick start after publishing:** double-click **ClaudeShell Launcher** (on the Desktop) →
*New Session* → *Open*. For a single session without the Launcher, run
`scripts\launch-shell.ps1`. Sessions started on a Launcher-created temp workspace have their
folder deleted when they stop.

Useful flags: `-Configuration Debug`, `-NoShortcut` (skip the Desktop shortcut; one is still
written into the install folder), `-Clean` (remove `host\`/`sidecar\`/`launcher\` first).

**Target-machine requirements** (no SDK needed to *run* a published install):

- **[.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** (or SDK)
- **[Node.js](https://nodejs.org/)** on `PATH` — the [`claude` CLI](https://docs.claude.com/en/docs/claude-code/overview)
  ships inside the sidecar's `node_modules`, so no separate CLI install is needed
- **A Claude login** in `~\.claude` (use the Launcher's **Claude sign-in** button if not signed in yet)

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

- `DISPLAY_NUDGE` / `systemPrompt` — the injected prompt. Ships as a display-only hint
  (images/diagrams inline); replace it with your role card, or set `""` for none.
- `AUTO_ALLOWED` — the set of tools that skip the gate. It ships with the read-only
  and bookkeeping tools (`Read`/`Grep`/`Glob`/`TodoWrite`/`ToolSearch`); empty it to
  gate literally everything, or add tools (e.g. `Write`) to prompt less.
- `canUseTool` — the gate itself: decide what pauses, auto-allows, or is denied.
- SDK options — register MCP servers, change `settingSources`, restrict tools.

The host UI binds only to `IOperatorConsole`/`IApprovalQueue`
(`src/ClaudeWorkbench.Host/Console`), so the whole backend can be swapped without
touching the UI.

For a **more detailed, fully-worked example** of building a real workflow on this
shell — governed edits, staging, review gates, a Roslyn code index — see the
**[ClaudeWorkbench repository](https://github.com/rdavi10471a2/ClaudeWorkbench)**.
