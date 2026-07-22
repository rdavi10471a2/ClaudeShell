# ClaudeShell Sidecars

This folder contains Claude Agent SDK drivers for ClaudeShell. Each sidecar is a Node/TypeScript process that spawns Claude via the Agent SDK and streams events back to the Blazor host over SSE.

## Folder Structure

### `/basic` — **Default: BasicSidecar**
The clean Claude shell sidecar. **All native tools allowed.** No governance, no gates, no workflow orchestration. Stock Claude with optional MCP server registration.

**Use this for:** The base ClaudeShell experience.

### `/samples/ai-monitor-workflow` — **Example: AIMonitor Governance**
The original ClaudeWorkbench sidecar preserved as a **reference implementation**. Shows how to layer back:
- File-mutation gating via `OperatorGate`
- Governed role card injection
- Deny-by-default tool policy
- Staged review gates

**Use this for:** Understanding how to add governance, staging, and workflow orchestration to ClaudeShell (e.g., if you're building a fork with operator oversight).

## Building

Each sidecar directory has its own `package.json` and `tsconfig.json`.

```bash
cd basic
npm install
npm run build
npm start
```

## Next Steps

- **To customize BasicSidecar:** Edit `basic/index.ts`. The `canUseTool` hook is where tool permissions live (currently: allow all).
- **To rebuild governance:** Copy `samples/ai-monitor-workflow/src/` patterns (gate.ts, events.ts) and adapt them to your needs.
- **To add MCP servers:** Update `canUseTool` or the SDK `mcpServers` config in `index.ts`.

---

**ClaudeShell philosophy:** The sidecar is just a Claude wrapper. Governance, workflows, and domain-specific logic belong *outside* this shell — in MCP servers, fork-specific configs, or the Blazor host. Keep it simple, keep it extensible.
