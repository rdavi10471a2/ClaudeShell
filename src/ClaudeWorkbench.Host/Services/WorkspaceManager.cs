namespace ClaudeWorkbench.Host.Services;

// Where the agent works. In Basic that's just a base folder (its cwd) under
// %TEMP%\ClaudeShell, with a files/ subfolder for composer attachments. No indexing,
// no watched project, no governance runtime. Override the base with the "Workspace"
// config key or the WORKSPACE environment variable.
public sealed class WorkspaceManager
{
    private readonly object sync = new();
    private string basePath;

    public WorkspaceManager(IConfiguration configuration)
    {
        string? configured = configuration["Workspace"]
            ?? Environment.GetEnvironmentVariable("WORKSPACE");
        basePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "ClaudeShell")
            : configured;
        EnsureDirectories();
    }

    public event Action? Changed;

    // The agent's working directory (cwd for Read/Write/Bash/etc.).
    public string BasePath
    {
        get { lock (sync) { return basePath; } }
    }

    // Composer attachments land here (inside the base folder, so the agent can Read them).
    public string FilesDirectory => Path.Combine(BasePath, "files");

    // Always true — a base folder is always set (falls back under %TEMP%).
    public bool HasWorkspace => true;

    public void SwitchTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (sync) { basePath = path; }
        EnsureDirectories();
        Changed?.Invoke();
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(FilesDirectory);
    }
}
