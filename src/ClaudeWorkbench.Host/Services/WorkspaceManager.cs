namespace ClaudeWorkbench.Host.Services;

// Where the agent works: a base folder (its cwd) with a files/ subfolder for composer
// attachments. No indexing, no watched project, no governance runtime.
//
// The default is an APP-OWNED, provisioned folder under %LOCALAPPDATA%\ClaudeShell\
// workspace — deliberately NOT the OS temp dir. Temp is shared with every other app and,
// worse, git-bash maps /tmp to the temp ROOT, so an agent that downloads to /tmp would
// land outside a temp-based workspace. A dedicated provisioned space is stable, private
// to this app, and the folder the agent's cwd points at. Override with the "Workspace"
// config key or the WORKSPACE environment variable (the Launcher sets it per session).
public sealed class WorkspaceManager
{
    private readonly object sync = new();
    private string basePath;

    public WorkspaceManager(IConfiguration configuration)
    {
        string? configured = configuration["Workspace"]
            ?? Environment.GetEnvironmentVariable("WORKSPACE");
        basePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeShell",
                "workspace")
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
