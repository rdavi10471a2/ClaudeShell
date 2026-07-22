import express from "express";
import { randomUUID } from "node:crypto";
import { exec } from "node:child_process";
import { promisify } from "node:util";
import {
  query,
  type CanUseTool,
  type Options,
  type PermissionResult,
  type Query,
  type SDKMessage,
  type SDKUserMessage,
} from "@anthropic-ai/claude-agent-sdk";
import { EventBus } from "./bus.js";
import { OperatorGate, baseName } from "./gate.js";
import type { SidecarEvent } from "./events.js";

// --- config -------------------------------------------------------------
// ClaudeShell BasicSidecar: stock Claude with all native tools available. No
// governance, no MCP, no deny-by-default. Every tool call is surfaced to the
// operator's Allow/Deny gate (the real client always asks) except the two
// agent-bookkeeping tools below.
const SIDECAR_PORT = Number(process.env.SIDECAR_PORT ?? 6110);

// The agent's working directory (where Read/Write/Bash etc. operate). Provided
// by the host at launch (WORKSPACE); falls back to the SDK default if unset.
const workspaceCwd: string | undefined = process.env.WORKSPACE || undefined;
// Optional extra read-only directory (operator uploads) granted to the agent.
const uploadsDir: string | undefined = process.env.UPLOADS_DIR || undefined;

// Tools that never prompt: agent bookkeeping plus read-only local inspection
// (Read/Grep/Glob don't mutate anything or reach the network — like the real
// Claude Code client, only writes, commands, and egress pause at the gate).
const AUTO_ALLOWED = new Set<string>([
  "TodoWrite",
  "ToolSearch",
  "Read",
  "Grep",
  "Glob",
]);

// --- minimal content-block shapes we read off SDK messages --------------
interface TextBlock {
  type: "text";
  text: string;
}
interface ToolUseBlock {
  type: "tool_use";
  id: string;
  name: string;
  input: unknown;
}
interface ToolResultBlock {
  type: "tool_result";
  tool_use_id: string;
  is_error?: boolean;
}
type ContentBlock =
  | TextBlock
  | ToolUseBlock
  | ToolResultBlock
  | { type: string; [key: string]: unknown };

function filePathOf(input: unknown): string | undefined {
  if (input && typeof input === "object") {
    const record = input as Record<string, unknown>;
    for (const key of ["path", "file_path", "sourceFilePath", "filePath"]) {
      const value = record[key];
      if (typeof value === "string" && value.length > 0) {
        return value;
      }
    }
  }
  return undefined;
}

// --- wiring -------------------------------------------------------------
const bus = new EventBus();
const gate = new OperatorGate();
let activeTurn: string | null = null;
// Current thread's SDK session id, captured from the message stream and passed as
// `resume` on the next turn so the agent remembers the conversation. Null = fresh thread.
let currentSessionId: string | null = null;
// AskUserQuestion elicitations awaiting operator answers (mirrors the gate registry).
const elicitations = new Map<string, { resolve: (answers: Record<string, unknown>) => void; questions: unknown }>();

// Per-turn options from the operator's settings. Only model + effort are honored;
// there is no tool policy (all tools available, every call gated).
interface ToolPolicy {
  model: string;
  effort: string;
}

const EFFORT_LEVELS = new Set(["low", "medium", "high", "xhigh", "max"]);

// The long-lived streaming query for the current thread + its input stream.
let activeQuery: Query | null = null;
let activeInput: InputStream | null = null;

// Async-iterable input backed by a queue we push operator turns into. Ending it
// completes the query (New Thread). This is what makes it streaming-input mode,
// which is the only mode that exposes the Query control handle (interrupt /
// getContextUsage / getUsage).
class InputStream {
  private readonly queue: SDKUserMessage[] = [];
  private waiter: ((r: IteratorResult<SDKUserMessage>) => void) | null = null;
  private ended = false;

  push(text: string): void {
    const msg = {
      type: "user",
      message: { role: "user", content: text },
      parent_tool_use_id: null,
    } as unknown as SDKUserMessage;
    if (this.waiter) {
      const w = this.waiter;
      this.waiter = null;
      w({ value: msg, done: false });
    } else {
      this.queue.push(msg);
    }
  }

  end(): void {
    this.ended = true;
    if (this.waiter) {
      const w = this.waiter;
      this.waiter = null;
      w({ value: undefined as unknown as SDKUserMessage, done: true });
    }
  }

  async *stream(): AsyncGenerator<SDKUserMessage> {
    while (true) {
      const next = this.queue.shift();
      if (next !== undefined) {
        yield next;
        continue;
      }
      if (this.ended) {
        return;
      }
      const r = await new Promise<IteratorResult<SDKUserMessage>>((res) => {
        this.waiter = res;
      });
      if (r.done) {
        return;
      }
      yield r.value;
    }
  }
}

const canUseTool: CanUseTool = async (toolName, input, { signal }) => {
  const turnId = activeTurn ?? "unknown";

  // AskUserQuestion is the agent asking the operator a clarifying question. Route it
  // to the elicitation dialog and return the operator's answers as updatedInput
  // (per the Agent SDK contract: { questions, answers }).
  if (toolName === "AskUserQuestion") {
    const elicitationId = randomUUID();
    const questions = (input as { questions?: unknown }).questions ?? [];
    const answers = await new Promise<Record<string, unknown>>((resolve) => {
      elicitations.set(elicitationId, { resolve, questions });
      bus.emit({ type: "elicitation_request", turnId, elicitationId, questions });
      const onAbort = () => {
        if (elicitations.delete(elicitationId)) {
          resolve({});
        }
      };
      signal.addEventListener("abort", onAbort, { once: true });
    });
    bus.emit({ type: "elicitation_resolved", turnId, elicitationId });
    return { behavior: "allow", updatedInput: { ...(input as object), questions, answers } };
  }

  // Agent bookkeeping tools proceed silently.
  if (AUTO_ALLOWED.has(baseName(toolName))) {
    return { behavior: "allow", updatedInput: input };
  }

  // Everything else pauses at the operator's Allow/Deny gate.
  const tool = baseName(toolName);
  const { gateId, decided } = gate.request(tool, input, filePathOf(input));
  bus.emit({
    type: "gate_request",
    turnId,
    gateId,
    tool,
    input,
    filePath: filePathOf(input),
  });

  const onAbort = () => gate.resolve(gateId, "deny", "aborted");
  signal.addEventListener("abort", onAbort, { once: true });
  const resolution = await decided;
  signal.removeEventListener("abort", onAbort);

  bus.emit({
    type: "gate_resolved",
    turnId,
    gateId,
    decision: resolution.decision,
    reason: resolution.reason,
  });

  const result: PermissionResult =
    resolution.decision === "allow"
      ? { behavior: "allow", updatedInput: input }
      : { behavior: "deny", message: resolution.reason ?? "Operator rejected" };
  return result;
};

// Lazily create the long-lived streaming query for the current thread. Options are
// set ONCE here (per session): cwd, tool surface, resume. Only the message content
// is per-turn (see submitTurn).
async function ensureSession(policy: ToolPolicy): Promise<void> {
  if (activeQuery) {
    return;
  }

  const input = new InputStream();
  activeInput = input;

  const options: Options = {
    canUseTool,
    permissionMode: "default",
    // Empty ON PURPOSE — the shell injects nothing. The SDK spawns the Claude Code
    // CLI, whose coding-agent prompt is built in; omitting systemPrompt would fall
    // back to that. An explicit string REPLACES it, and the empty string is the
    // closest the SDK offers to "no prompt at all". Forks put their role card here.
    systemPrompt: "",
    // Operator-selected model + reasoning effort (empty => inherit the default).
    ...(policy.model ? { model: policy.model } : {}),
    ...(EFFORT_LEVELS.has(policy.effort) ? { effort: policy.effort as Options["effort"] } : {}),
    ...(workspaceCwd ? { cwd: workspaceCwd } : {}),
    // Operator uploads sit outside cwd; grant read there so the agent can Read them.
    ...(uploadsDir ? { additionalDirectories: [uploadsDir] } : {}),
    // All native tools available — nothing denied.
    disallowedTools: [],
    // SDK isolation mode: load NO filesystem settings (no personal ~/.claude leak,
    // no CLAUDE.md injection). A fork can flip this to ["user"] to inherit settings.
    settingSources: [],
    // Resume the thread's session (restore after a process restart). Within a live
    // process the session persists in the query handle itself.
    ...(currentSessionId ? { resume: currentSessionId } : {}),
  };

  const q = query({ prompt: input.stream(), options }) as unknown as Query;
  activeQuery = q;

  // Background read loop: drain the query's output for the life of the thread.
  void (async () => {
    try {
      for await (const message of q as AsyncIterable<SDKMessage>) {
        handleMessage(message);
      }
    } catch (error) {
      const detail = error instanceof Error ? error.message : String(error);
      if (!/abort|interrupt/i.test(detail)) {
        bus.emit({ type: "error", message: detail });
      }
    } finally {
      // Only clear the shared thread state if it STILL belongs to this query. A new
      // thread may have already replaced activeQuery before this loop's finally runs.
      if (activeQuery === q) {
        activeQuery = null;
        activeInput = null;
        activeTurn = null;
      }
    }
  })();
}

// Push one operator turn into the live session's input stream.
async function submitTurn(prompt: string, turnId: string, policy: ToolPolicy): Promise<void> {
  await ensureSession(policy);
  activeTurn = turnId;
  bus.emit({ type: "turn_started", turnId });
  bus.emit({ type: "user_prompt", turnId, text: prompt });
  activeInput?.push(prompt);
}

function handleMessage(message: SDKMessage): void {
  // Every SDK message carries the session id; capture it so the thread can resume.
  const sessionId = (message as { session_id?: string }).session_id;
  if (typeof sessionId === "string" && sessionId.length > 0) {
    currentSessionId = sessionId;
  }

  const turnId = activeTurn ?? "";

  switch (message.type) {
    case "assistant": {
      const content = message.message.content as ContentBlock[];
      const usage = message.message.usage;
      for (const block of content) {
        if (block.type === "text") {
          bus.emit({ type: "assistant_text", turnId, text: (block as TextBlock).text });
        } else if (block.type === "tool_use") {
          const toolBlock = block as ToolUseBlock;
          bus.emit({
            type: "tool_call_started",
            turnId,
            callId: toolBlock.id,
            tool: baseName(toolBlock.name),
            input: toolBlock.input,
          });
        }
      }
      if (usage) {
        bus.emit({
          type: "usage",
          turnId,
          inputTokens: usage.input_tokens,
          outputTokens: usage.output_tokens,
        });
      }
      break;
    }
    case "user": {
      const content = message.message.content;
      if (Array.isArray(content)) {
        for (const block of content as ContentBlock[]) {
          if (block.type === "tool_result") {
            const resultBlock = block as ToolResultBlock;
            bus.emit({
              type: "tool_call_finished",
              turnId,
              callId: resultBlock.tool_use_id,
              tool: "",
              ok: resultBlock.is_error !== true,
            });
          }
        }
      }
      break;
    }
    case "result": {
      if (message.subtype === "success") {
        bus.emit({
          type: "usage",
          turnId,
          inputTokens: message.usage.input_tokens,
          outputTokens: message.usage.output_tokens,
        });
      }
      bus.emit({
        type: "turn_finished",
        turnId,
        stopReason: message.subtype,
      });
      activeTurn = null;
      break;
    }
    default:
      break;
  }
}

// --- HTTP surface for the Blazor host -----------------------------------
const app = express();
app.use(express.json({ limit: "2mb" }));

// The control surface is localhost-only. Bind to loopback (app.listen below) AND
// reject any request whose Host header isn't localhost, plus any browser request
// carrying a non-local Origin (DNS-rebinding defense). The Blazor host talks over
// 127.0.0.1 and sends no Origin, so it is unaffected.
app.use((req, res, next) => {
  const host = (req.headers.host ?? "").split(":")[0];
  if (host !== "localhost" && host !== "127.0.0.1" && host !== "[::1]" && host !== "::1") {
    res.status(403).json({ error: "forbidden host" });
    return;
  }
  const origin = req.headers.origin;
  if (origin && !/^https?:\/\/(localhost|127\.0\.0\.1|\[::1\])(:\d+)?$/.test(origin)) {
    res.status(403).json({ error: "forbidden origin" });
    return;
  }
  next();
});

app.get("/health", (_req, res) => {
  res.json({
    status: "ok",
    activeTurn,
    pendingGates: gate.list().length,
  });
});

// Claude login state, so the host's command-bar dot reflects authentication rather
// than mere sidecar liveness. The sidecar owns the Claude CLI relationship.
//   null  = unknown (CLI missing, timed out, or output unparseable) — NOT "signed out"
//   true  = loggedIn: true
//   false = loggedIn: false
const execAsync = promisify(exec);
const AUTH_TTL_MS = 20_000;
let claudeAuthCache: { loggedIn: boolean | null; at: number } = { loggedIn: null, at: 0 };

function parseLoggedIn(output: string): boolean | null {
  try {
    const parsed = JSON.parse(output) as { loggedIn?: unknown };
    if (typeof parsed.loggedIn === "boolean") {
      return parsed.loggedIn;
    }
  } catch {
    // Not JSON — fall through to a textual sniff for forward-compatibility.
  }
  if (/loggedIn["']?\s*[:=]\s*true/i.test(output)) return true;
  if (/loggedIn["']?\s*[:=]\s*false/i.test(output)) return false;
  return null;
}

async function probeClaudeAuth(): Promise<boolean | null> {
  try {
    const { stdout } = await execAsync("claude auth status", {
      windowsHide: true,
      timeout: 10_000,
    });
    return parseLoggedIn(stdout);
  } catch (err) {
    const out = (err as { stdout?: unknown })?.stdout;
    return typeof out === "string" && out.length > 0 ? parseLoggedIn(out) : null;
  }
}

app.get("/auth", async (_req, res) => {
  const now = Date.now();
  if (now - claudeAuthCache.at > AUTH_TTL_MS) {
    claudeAuthCache = { loggedIn: await probeClaudeAuth(), at: now };
  }
  res.json({ loggedIn: claudeAuthCache.loggedIn });
});

// Live token/context + subscription usage, read straight off the Query handle.
// Both methods are experimental in the SDK (guarded); null until a thread exists.
app.get("/usage", async (_req, res) => {
  const q = activeQuery as unknown as {
    getContextUsage?: () => Promise<unknown>;
    usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET?: () => Promise<unknown>;
  } | null;
  if (!q) {
    res.json({ context: null, subscription: null });
    return;
  }
  let context: unknown = null;
  let subscription: unknown = null;
  try {
    if (typeof q.getContextUsage === "function") {
      context = await q.getContextUsage();
    }
  } catch {
    context = null;
  }
  try {
    if (typeof q.usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET === "function") {
      subscription = await q.usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET();
    }
  } catch {
    subscription = null;
  }
  res.json({ context, subscription });
});

app.get("/events", (_req, res) => {
  bus.addClient(res);
});

app.post("/prompt", (req, res) => {
  if (activeTurn) {
    res.status(409).json({ error: "A turn is already active.", activeTurn });
    return;
  }
  const prompt = (req.body?.prompt ?? "").toString();
  if (!prompt.trim()) {
    res.status(400).json({ error: "prompt is required." });
    return;
  }
  const raw = (req.body?.toolPolicy ?? {}) as Partial<ToolPolicy>;
  const policy: ToolPolicy = {
    model: typeof raw.model === "string" ? raw.model : "",
    effort: typeof raw.effort === "string" ? raw.effort : "",
  };
  const turnId = randomUUID();
  activeTurn = turnId;
  void submitTurn(prompt, turnId, policy);
  res.status(202).json({ turnId });
});

app.post("/stop", (_req, res) => {
  if (activeQuery && activeTurn) {
    void activeQuery.interrupt();
    res.json({ stopped: true });
    return;
  }
  res.json({ stopped: false });
});

app.get("/gates", (_req, res) => {
  res.json(gate.list());
});

app.post("/gates/:id", (req, res) => {
  const decision = req.body?.decision;
  if (decision !== "allow" && decision !== "deny") {
    res.status(400).json({ error: "decision must be 'allow' or 'deny'." });
    return;
  }
  const ok = gate.resolve(req.params.id, decision, req.body?.reason);
  res.status(ok ? 200 : 404).json({ resolved: ok });
});

app.get("/elicitations", (_req, res) => {
  res.json(
    [...elicitations.entries()].map(([elicitationId, entry]) => ({
      elicitationId,
      questions: entry.questions,
    })),
  );
});

app.post("/elicitations/:id", (req, res) => {
  const entry = elicitations.get(req.params.id);
  if (!entry) {
    res.status(404).json({ resolved: false });
    return;
  }
  elicitations.delete(req.params.id);
  entry.resolve((req.body?.answers as Record<string, unknown>) ?? {});
  res.json({ resolved: true });
});

app.post("/new-thread", (_req, res) => {
  if (activeTurn) {
    res.status(409).json({ error: "Cannot start a new thread while a turn is active." });
    return;
  }
  activeInput?.end();
  activeQuery = null;
  activeInput = null;
  currentSessionId = null;
  elicitations.clear();
  bus.clear();
  bus.emit({ type: "thread_reset", turnId: "thread" });
  res.json({ ok: true });
});

app.listen(SIDECAR_PORT, "127.0.0.1", () => {
  const banner: SidecarEvent = {
    type: "error",
    message: `BasicSidecar listening on :${SIDECAR_PORT} (cwd: ${workspaceCwd ?? "default"})`,
  };
  // eslint-disable-next-line no-console
  console.log(banner.message);
});
