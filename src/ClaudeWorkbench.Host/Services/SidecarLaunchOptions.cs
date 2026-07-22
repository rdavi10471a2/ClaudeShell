namespace ClaudeWorkbench.Host.Services;

// How the host launches the Node BasicSidecar as a managed child process, so an
// installed app is a single start. Overridable via the Sidecar:* config section.
public sealed class SidecarLaunchOptions
{
    public bool AutoStart { get; set; } = true;

    public string NodeExecutable { get; set; } = "node";

    // Folder containing dist/index.js. Empty => resolved relative to the host at
    // startup (dev: ../../sidecar/basic; published: ../sidecar).
    public string SidecarDirectory { get; set; } = string.Empty;

    public int Port { get; set; } = 6110;
}
