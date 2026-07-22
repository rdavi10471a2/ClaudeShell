namespace ClaudeWorkbench.Host.Console;

// Claude login state, surfaced to the command-bar dot. Distinct from ConsoleStatus
// (turn/session state): auth is orthogonal to whether a turn is running. Tri-state —
// null means "not yet known" (the probe has not answered, the sidecar is down, or the
// CLI is missing), which the UI renders as a neutral "checking" dot rather than a
// false "signed out". Probed via the sidecar's /auth (`claude auth status`).
// GitHub is a Basic-era vestige kept for record-shape stability; it stays null.
public sealed record AuthStatus(bool? Claude, bool? GitHub)
{
    public static AuthStatus Unknown { get; } = new(null, null);
}
