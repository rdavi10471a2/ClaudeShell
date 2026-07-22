import { randomUUID } from "node:crypto";
import type { GateDecision } from "./events.js";

// Strip the mcp__server__ prefix off a tool name so gates/labels read cleanly.
export function baseName(toolName: string): string {
  const idx = toolName.lastIndexOf("__");
  return idx >= 0 ? toolName.slice(idx + 2) : toolName;
}

export interface GateResolution {
  decision: GateDecision;
  reason?: string;
}

interface PendingGate extends GateResolution {
  gateId: string;
  tool: string;
  input: unknown;
  filePath?: string;
  resolve: (resolution: GateResolution) => void;
}

// Holds tool calls paused at the operator's Allow/Deny gate. request() returns a
// promise the canUseTool hook awaits; resolve() is driven by the host UI.
export class OperatorGate {
  private readonly pending = new Map<string, Omit<PendingGate, keyof GateResolution>>();

  request(
    tool: string,
    input: unknown,
    filePath?: string,
  ): { gateId: string; decided: Promise<GateResolution> } {
    const gateId = randomUUID();
    let resolve!: (resolution: GateResolution) => void;
    const decided = new Promise<GateResolution>((r) => {
      resolve = r;
    });
    this.pending.set(gateId, { gateId, tool, input, filePath, resolve });
    return { gateId, decided };
  }

  resolve(gateId: string, decision: GateDecision, reason?: string): boolean {
    const gate = this.pending.get(gateId);
    if (!gate) {
      return false;
    }
    this.pending.delete(gateId);
    gate.resolve({ decision, reason });
    return true;
  }

  list(): { gateId: string; tool: string; input: unknown; filePath?: string }[] {
    return [...this.pending.values()].map(({ gateId, tool, input, filePath }) => ({
      gateId,
      tool,
      input,
      filePath,
    }));
  }
}
