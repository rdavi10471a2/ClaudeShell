using System.Diagnostics;

namespace ClaudeShell.Launcher;

// Opens a terminal window on the Claude CLI auth flows (ported from the original
// ClaudeWorkbench launcher).
//
// WHY A TERMINAL, AND NOT A REDIRECTED PROCESS
// --------------------------------------------
// `claude auth login` is an interactive OAuth flow: it prints a URL and a one-time
// code, opens a browser, and then BLOCKS on the console waiting for the round-trip.
// That only works with a real console attached. A redirected process has no TTY: it
// cannot show the code and would hang with the launcher holding a pipe nobody reads.
// So the CLI gets its OWN visible console (cmd.exe /k) and we get out of the way;
// /k keeps the window open so the result is readable.
//
// The login is the user's, not a session's: it is cached in ~\.claude, shared by
// every ClaudeShell session (and by Claude Code, if installed), and the console is
// independent of the launcher's lifetime so closing the launcher cannot abort a
// login mid-handshake.
internal static class AuthLauncher
{
    // The Claude CLI ships as a Windows shim - claude.cmd/.exe/.ps1 - so accept any.
    private static readonly string[] Executables = ["claude.cmd", "claude.exe", "claude.ps1", "claude"];

    internal const string InstallHint =
        "The claude CLI was not found on PATH and the sidecar's bundled copy is missing.\r\n\r\n" +
        "Either run 'npm install' in the sidecar folder, or install Claude Code:\r\n\r\n" +
        "    npm install -g @anthropic-ai/claude-code\r\n\r\nthen reopen the launcher.";

    internal static void LaunchLogin(string? bundledCliJs) => LaunchInTerminal(["auth", "login"], bundledCliJs);

    internal static void LaunchLogout(string? bundledCliJs) => LaunchInTerminal(["auth", "logout"], bundledCliJs);

    internal static void LaunchStatus(string? bundledCliJs) => LaunchInTerminal(["auth", "status"], bundledCliJs);

    // True when some Claude CLI is reachable: on PATH, or bundled inside the
    // sidecar's node_modules (a fresh publish-live install has the latter only).
    internal static bool IsAvailable(string? bundledCliJs)
    {
        return ResolveOnPath() is not null
            || (bundledCliJs is not null && File.Exists(bundledCliJs));
    }

    private static void LaunchInTerminal(string[] args, string? bundledCliJs)
    {
        // Run via cmd.exe /k: the Claude CLI is a .cmd shim that needs a command
        // interpreter, and /k keeps the window open so the result is readable. The
        // resolved executable NAME is passed (not the full path) so the shim's own
        // PATH logic still runs.
        string? onPath = ResolveOnPath();

        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd.exe",
            // A visible, interactive console with its own process group, independent
            // of the launcher's lifetime.
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("/k");
        startInfo.ArgumentList.Add("title");
        startInfo.ArgumentList.Add("ClaudeShell - Claude sign-in");
        startInfo.ArgumentList.Add("&");
        if (onPath is not null)
        {
            startInfo.ArgumentList.Add(Path.GetFileName(onPath));
        }
        else
        {
            // Fall back to the CLI bundled with the sidecar's Agent SDK.
            startInfo.ArgumentList.Add("node");
            startInfo.ArgumentList.Add(bundledCliJs!);
        }

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process.Start(startInfo);
    }

    // Minimal PATH resolver: walk %PATH% for an exact filename. Candidates already
    // carry their extension, so PATHEXT expansion is unnecessary here.
    private static string? ResolveOnPath()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string candidate in Executables)
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string resolved = Path.Combine(directory.Trim(), candidate);
                    if (File.Exists(resolved))
                    {
                        return resolved;
                    }
                }
                catch
                {
                    // A malformed PATH entry must not stop the search.
                }
            }
        }

        return null;
    }
}
