namespace ClaudeWorkbench.Host.Console;

// The turn/session seam: what the operator is looking at and how they start work.
// Approvals (permission gates + questions) live in IApprovalQueue.
public interface IOperatorConsole
{
    event Action? Changed;

    string WorkspacePath { get; }

    ConsoleStatus Status { get; }

    // Login state of the Claude and GitHub CLIs, for the command-bar dots. Orthogonal
    // to Status (turn/session), and probed out-of-band, so it lives on its own seam.
    AuthStatus Auth { get; }

    IReadOnlyList<TranscriptEntry> Transcript { get; }

    IReadOnlyList<ActivityEntry> Activity { get; }

    // Submit one operator turn. Every tool call the turn makes pauses at the
    // operator's Allow/Deny gate (no auto-approve in Basic).
    Task SendAsync(string prompt);

    // Interrupt the in-flight turn.
    Task StopAsync();

    // Live token/context + subscription usage off the agent's Query handle.
    Task<UsageSnapshot> GetUsageAsync();

    // Start a fresh conversation thread (drops resumed context, clears the transcript).
    Task NewThreadAsync();
}
