import express from "express";
import { randomUUID } from "node:crypto";
import {
  query,
  type CanUseTool,
  type Options,
  type Query,
  type SDKMessage,
  type SDKUserMessage,
} from "@anthropic-ai/claude-agent-sdk";
import { EventBus } from "./bus.js";
import type { SidecarEvent } from "./events.js";

// --- config -------------------------------------------------------
const SIDECAR_PORT = Number(process.env.SIDECAR_PORT ?? 6110);
const WORKBENCH_MCP_URL =
  process.env.WORKBENCH_MCP_URL ?? "http://localhost:6100/mcp";
const MCP_SERVER_NAME = "claude-workbench";
const HOST_BASE = WORKBENCH_MCP_URL.replace(/\/mcp\/?$/, "");

// Agent's working directory (optional, auto-derived from host /health).
let workspaceCwd: string | undefined;
// Operator-uploaded files directory.
let uploadsDir: string | undefined;

async function resolveWorkspaceCwd(): Promise<void> {
  const timeoutMs = 30000;
  const startTime = Date.now();

  while (Date.now() - startTime < timeoutMs) {
    try {
      const response = await fetch(`${HOST_BASE}/health`);
      if (response.ok) {
        const info = (await response.json()) as {
          watchedSolutionPath?: string;
          uploadsPath?: string;
        };
        if (info.watchedSolutionPath) {
          const { dirname } = await import("node:path");
          workspaceCwd = dirname(info.watchedSolutionPath);
        }
        uploadsDir = info.uploadsPath ?? undefined;
        console.log("[sidecar] workspace resolved:", workspaceCwd);
        return;
      }
    } catch {
      // host not listening yet
    }

    await new Promise((resolve) => setTimeout(resolve, 1000));
  }

  console.warn(`[sidecar] workspace not resolved after ${timeoutMs / 1000}s`);
}

// --- minimal content-block shapes --------------------------------
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
type ContentBlock =
  | TextBlock
  | ToolUseBlock
  | { type: string; [key: string]: unknown };

// --- wiring --------------------------------------------------------
const bus = new EventBus();

// Async-iterable input stream for the query.
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

let activeTurn: string | null = null;
let activeQuery: Query | null = null;
let activeInput: InputStream | null = null;
let currentSessionId: string | null = null;

// AskUserQuestion elicitations awaiting operator answers.
const elicitations = new Map<string, { resolve: (answers: Record<string, unknown>) => void; questions: unknown }>();

// BasicSidecar: all tools allowed. No governance, no deny-by-default, no gates.
const canUseTool: CanUseTool = async (toolName, input, { signal }) => {
  // AskUserQuestion: route to elicitation dialog.
  if (toolName === "AskUserQuestion") {
    const elicitationId = randomUUID();
    const questions = (input as { questions?: unknown }).questions ?? [];
    const answers = await new Promise<Record<string, unknown>>((resolve) => {
      elicitations.set(elicitationId, { resolve, questions });
      bus.emit({ type: "elicitation_request", turnId: activeTurn ?? "", elicitationId, questions });
      const onAbort = () => {
        if (elicitations.delete(elicitationId)) {
          resolve({});
        }
      };
      signal.addEventListener("abort", onAbort, { once: true });
    });
    bus.emit({ type: "elicitation_resolved", turnId: activeTurn ?? "", elicitationId });
    return { behavior: "allow", updatedInput: { ...(input as object), questions, answers } };
  }

  // Allow everything by default.
  return { behavior: "allow", updatedInput: input };
};

async function ensureSession(model: string = "", effort: string = ""): Promise<void> {
  if (activeQuery !== null) {
    return; // Session already running.
  }

  await resolveWorkspaceCwd();

  const input = new InputStream();
  activeInput = input;

  const options: Options = {
    // Register MCP servers if needed (optional).
    ...(WORKBENCH_MCP_URL
      ? {
          mcpServers: {
            [MCP_SERVER_NAME]: { type: "http", url: WORKBENCH_MCP_URL },
          },
        }
      : {}),
    canUseTool,
    permissionMode: "default",
    // Stock Claude prompt, no governance card.
    systemPrompt: { type: "preset", preset: "claude_code" },
    // Optional model + effort overrides.
    ...(model ? { model } : {}),
    ...(effort && ["low", "medium", "high", "xhigh", "max"].includes(effort) 
      ? { effort: effort as Options["effort"] } 
      : {}),
    cwd: workspaceCwd,
    // Operator uploads accessible.
    ...(uploadsDir ? { additionalDirectories: [uploadsDir] } : {}),
    // No tool restrictions, no isolation.
    disallowedTools: [],
    strictMcpConfig: false,
    // Allow filesystem settings (Claude settings work normally).
    settingSources: [],
    // Resume thread if we have a session id.
    ...(currentSessionId ? { resume: currentSessionId } : {}),
  };

  const q = query({ prompt: input.stream(), options }) as unknown as Query;
  activeQuery = q;

  // Background read loop: drain the query's output.
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
      // Clear thread state if this query is still active.
      if (activeQuery === q) {
        activeQuery = null;
        activeInput = null;
        activeTurn = null;
      }
    }
  })();
}

async function submitTurn(prompt: string, turnId: string, model: string = "", effort: string = ""): Promise<void> {
  await ensureSession(model, effort);
  activeTurn = turnId;
  bus.emit({ type: "turn_started", turnId });
  bus.emit({ type: "user_prompt", turnId, text: prompt });
  activeInput?.push(prompt);
}

function handleMessage(message: SDKMessage): void {
  // Capture session id for thread resumption.
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
            tool: toolBlock.name,
            input: toolBlock.input,
          });
        }
      }
      if (usage) {
        bus.emit({ type: "usage", turnId, usage });
      }
      break;
    }

    case "result": {
      const result = message as { type: string; content?: unknown; is_error?: boolean };
      // Tool result from the SDK
      bus.emit({
        type: "tool_result",
        turnId,
        callId: "",
        result: result.content,
        isError: result.is_error ?? false,
      });
      break;
    }

    default: {
      // Other message types (stream_event, tool_progress, etc.) - just log
      break;
    }
  }
}

// --- HTTP server: stream events to host ----
const app = express();

app.use(express.json());

// POST /prompt: submit a turn.
app.post("/prompt", express.json(), async (req: express.Request, res: express.Response) => {
  const { prompt, turnId, model, effort } = req.body as Record<string, unknown>;
  if (!prompt || !turnId) {
    res.status(400).json({ error: "missing prompt or turnId" });
    return;
  }

  try {
    await submitTurn(
      String(prompt),
      String(turnId),
      String(model ?? ""),
      String(effort ?? ""),
    );
    res.json({ ok: true });
  } catch (error) {
    res.status(500).json({ error: String(error) });
  }
});

// POST /gate/:gateId: resolve an elicitation (answer questions).
app.post("/elicitation/:elicitationId/resolve", express.json(), (req: express.Request, res: express.Response) => {
  const { elicitationId } = req.params;
  const { answers } = req.body as Record<string, unknown>;

  const elicitation = elicitations.get(elicitationId);
  if (!elicitation) {
    res.status(404).json({ error: "elicitation not found" });
    return;
  }

  elicitations.delete(elicitationId);
  elicitation.resolve(answers as Record<string, unknown> ?? {});
  res.json({ ok: true });
});

// GET /health: check sidecar health.
app.get("/health", (_req: express.Request, res: express.Response) => {
  res.json({ status: "ok", hasActiveQuery: activeQuery !== null });
});

// GET /events: Server-Sent Events stream.
app.get("/events", (req: express.Request, res: express.Response) => {
  res.setHeader("Content-Type", "text/event-stream");
  res.setHeader("Cache-Control", "no-cache");
  res.setHeader("Connection", "keep-alive");

  const handler = (event: SidecarEvent) => {
    res.write(`data: ${JSON.stringify(event)}\n\n`);
  };

  bus.on(handler);

  req.on("close", () => {
    bus.off(handler);
    res.end();
  });
});

// POST /interrupt: interrupt the active query.
app.post("/interrupt", (_req: express.Request, res: express.Response) => {
  if (!activeQuery) {
    res.status(400).json({ error: "no active query" });
    return;
  }

  try {
    activeQuery.interrupt();
    res.json({ ok: true });
  } catch (error) {
    res.status(500).json({ error: String(error) });
  }
});

// POST /new-thread: end the current thread and start a new one.
app.post("/new-thread", (_req: express.Request, res: express.Response) => {
  if (activeInput) {
    activeInput.end();
    currentSessionId = null; // Start fresh.
  }
  res.json({ ok: true });
});

const port = SIDECAR_PORT;
app.listen(port, () => {
  console.log(`[sidecar] BasicSidecar listening on http://localhost:${port}`);
  console.log(`[sidecar] All native tools allowed. No governance. Stock Claude.`);
});
