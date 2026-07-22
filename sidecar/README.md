# ClaudeShell Sidecar

`basic/` is the **BasicSidecar** — the Node/TypeScript process that drives Claude via
the **Claude Agent SDK** and streams events back to the Blazor host over SSE.

## What it does (and deliberately doesn't)

- **Plain Claude.** `systemPrompt: ""` — the shell injects nothing. (Omitting the
  option would fall back to the CLI's built-in Claude-Code prompt; an explicit string
  replaces it.) `settingSources: []` — no CLAUDE.md, no `~/.claude` settings leak.
- **All native tools available** — `disallowedTools: []`, no MCP servers, no deny lists.
- **Writes, commands, and egress ask the operator.** `canUseTool` pauses those at an
  Allow/Deny gate (`gate_request` → operator decision → `gate_resolved`). The
  read-only/bookkeeping tools in `AUTO_ALLOWED` (`Read`/`Grep`/`Glob`/`TodoWrite`/
  `ToolSearch`) and `AskUserQuestion` (routed to the questions dialog) skip the gate —
  matching the real Claude Code client. Edit `AUTO_ALLOWED` to make it stricter/looser.
- **Session continuity** — streaming-input query, `resume` across restarts,
  interrupt, live context/subscription usage off the Query handle.

## HTTP surface (what the host binds to)

| Endpoint | Purpose |
|---|---|
| `GET /events` | SSE stream of sidecar events (with bounded replay) |
| `POST /prompt` | submit a turn `{ prompt, toolPolicy: { model, effort } }` |
| `GET/POST /gates[/:id]` | pending permission gates / resolve one (`allow`/`deny`) |
| `GET/POST /elicitations[/:id]` | pending AskUserQuestion prompts / answer one |
| `POST /stop` · `POST /new-thread` | interrupt · fresh thread |
| `GET /usage` · `GET /auth` · `GET /health` | usage meters · Claude login state · liveness |

Environment: `SIDECAR_PORT` (default 6110), `WORKSPACE` (agent cwd), `UPLOADS_DIR`
(extra read directory for composer attachments). The host sets all three when it
launches the sidecar.

## Build

```bash
cd basic
npm install
npm run build   # -> dist/  (the host launches dist/index.js)
```

## Forking

`basic/index.ts` is the whole policy surface: put a role card in `systemPrompt`,
register domain MCP servers in the SDK options, narrow or widen the gate in
`canUseTool`. The event contract (`events.ts`) is what the host UI binds to — keep it
stable and the UI keeps working.
