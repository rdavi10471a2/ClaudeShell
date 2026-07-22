// Neutral sidecar -> host event contract (BasicSidecar version).
// Simplified: no governance, no gating events.

export type SidecarEvent =
  | { type: "turn_started"; turnId: string }
  | { type: "user_prompt"; turnId: string; text: string }
  | { type: "assistant_text"; turnId: string; text: string }
  | {
      type: "tool_call_started";
      turnId: string;
      callId: string;
      tool: string;
      input: unknown;
    }
  | {
      type: "tool_result";
      turnId: string;
      callId: string;
      result: unknown;
      isError?: boolean;
    }
  | {
      // The agent called AskUserQuestion; awaiting the operator's answers.
      type: "elicitation_request";
      turnId: string;
      elicitationId: string;
      questions: unknown;
    }
  | { type: "elicitation_resolved"; turnId: string; elicitationId: string }
  | {
      type: "usage";
      turnId: string;
      usage?: unknown;
    }
  | { type: "error"; message: string };

export type SidecarEventType = SidecarEvent["type"];
