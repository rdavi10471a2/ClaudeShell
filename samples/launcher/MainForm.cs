using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ClaudeShell.Launcher;

// One row per session: a ClaudeWorkbench --Basic host process with its own port
// pair and workspace. Sessions on a launcher-created temp workspace have their
// folder deleted when they stop (and when the launcher closes).
public sealed class MainForm : Form
{
    private readonly ListView list = new();
    private readonly Button newButton = new() { Text = "New Session" };
    private readonly Button startButton = new() { Text = "Start" };
    private readonly Button stopButton = new() { Text = "Stop" };
    private readonly Button openButton = new() { Text = "Open" };
    private readonly Button removeButton = new() { Text = "Remove" };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private readonly List<Session> sessions = new();
    private int nameCounter = 1;

    private sealed class Session
    {
        public required string Name { get; init; }
        public required string Workspace { get; init; }
        public required bool TempOwned { get; init; }
        public required int HostPort { get; init; }
        public required int SidecarPort { get; init; }
        public Process? Process { get; set; }
        public string Url => $"http://localhost:{HostPort}";
        public bool Running => Process is { HasExited: false };
    }

    public MainForm()
    {
        Text = "ClaudeShell Launcher (sample)";
        Width = 860;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.Columns.Add("Session", 140);
        list.Columns.Add("Workspace", 330);
        list.Columns.Add("URL", 170);
        list.Columns.Add("Status", 120);
        list.DoubleClick += (_, _) => OpenSelected();

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 44,
            Padding = new Padding(6),
        };
        buttons.Controls.AddRange([newButton, startButton, stopButton, openButton, removeButton]);
        newButton.Click += (_, _) => NewSession();
        startButton.Click += (_, _) => StartSelected();
        stopButton.Click += (_, _) => StopSelected();
        openButton.Click += (_, _) => OpenSelected();
        removeButton.Click += (_, _) => RemoveSelected();

        Controls.Add(list);
        Controls.Add(buttons);

        refresh.Tick += (_, _) => RefreshRows();
        refresh.Start();

        FormClosing += (_, _) => StopAll();
    }

    private Session? Selected =>
        list.SelectedItems.Count > 0 ? (Session)list.SelectedItems[0].Tag! : null;

    private void NewSession()
    {
        using NewSessionDialog dialog = new($"session-{nameCounter}");
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        nameCounter++;
        bool temp = string.IsNullOrWhiteSpace(dialog.WorkspacePath);
        string workspace = temp
            ? Path.Combine(Path.GetTempPath(), "ClaudeShell", "sessions", dialog.SessionName)
            : dialog.WorkspacePath;
        Session session = new()
        {
            Name = dialog.SessionName,
            Workspace = workspace,
            TempOwned = temp,
            HostPort = NextFreePort(5000),
            SidecarPort = NextFreePort(6110),
        };
        sessions.Add(session);

        ListViewItem item = new(session.Name) { Tag = session };
        item.SubItems.Add(session.Workspace + (session.TempOwned ? "  (temp — deleted on stop)" : ""));
        item.SubItems.Add(session.Url);
        item.SubItems.Add("Stopped");
        list.Items.Add(item);
        item.Selected = true;

        Start(session);
    }

    private void StartSelected()
    {
        if (Selected is { } session)
        {
            Start(session);
        }
    }

    private void Start(Session session)
    {
        if (session.Running)
        {
            return;
        }

        Directory.CreateDirectory(session.Workspace);
        string repoRoot = FindRepoRoot();
        string project = Path.Combine(repoRoot, "src", "ClaudeWorkbench.Host", "ClaudeWorkbench.Host.csproj");
        string sidecarDir = Path.Combine(repoRoot, "sidecar", "basic");

        // Prefer the built exe (fast start); fall back to `dotnet run`.
        string exeDebug = Path.Combine(repoRoot, "src", "ClaudeWorkbench.Host", "bin", "Debug", "net10.0", "ClaudeWorkbench.Host.exe");
        string exeRelease = Path.Combine(repoRoot, "src", "ClaudeWorkbench.Host", "bin", "Release", "net10.0", "ClaudeWorkbench.Host.exe");
        string? exe = File.Exists(exeDebug) ? exeDebug : File.Exists(exeRelease) ? exeRelease : null;

        ProcessStartInfo info = exe is not null
            ? new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)! }
            : new ProcessStartInfo("dotnet", $"run --project \"{project}\" --no-launch-profile") { WorkingDirectory = repoRoot };
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.Environment["ASPNETCORE_URLS"] = session.Url;
        info.Environment["Sidecar__Port"] = session.SidecarPort.ToString();
        info.Environment["Sidecar__SidecarDirectory"] = sidecarDir;
        info.Environment["WORKSPACE"] = session.Workspace;

        try
        {
            session.Process = Process.Start(info);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Failed to start the session:\n{exception.Message}", "ClaudeShell Launcher");
        }

        RefreshRows();
    }

    private void StopSelected()
    {
        if (Selected is { } session)
        {
            Stop(session);
        }
    }

    private void Stop(Session session)
    {
        try
        {
            if (session.Process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // best effort — the refresh timer will show the real state
        }
        finally
        {
            session.Process = null;
            DeleteTempWorkspace(session);
        }

        RefreshRows();
    }

    private void OpenSelected()
    {
        if (Selected is { } session)
        {
            Process.Start(new ProcessStartInfo(session.Url) { UseShellExecute = true });
        }
    }

    private void RemoveSelected()
    {
        if (list.SelectedItems.Count == 0)
        {
            return;
        }

        ListViewItem item = list.SelectedItems[0];
        Session session = (Session)item.Tag!;
        Stop(session);
        sessions.Remove(session);
        list.Items.Remove(item);
    }

    private void StopAll()
    {
        foreach (Session session in sessions)
        {
            Stop(session);
        }
    }

    // Disk-friendly: a temp workspace the launcher created is removed once its
    // session stops. Never deletes a user-picked folder (TempOwned is false there).
    private static void DeleteTempWorkspace(Session session)
    {
        if (!session.TempOwned || !Directory.Exists(session.Workspace))
        {
            return;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(session.Workspace, recursive: true);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(500); // file handles from the dying process may linger briefly
            }
        }
    }

    private void RefreshRows()
    {
        foreach (ListViewItem item in list.Items)
        {
            Session session = (Session)item.Tag!;
            item.SubItems[3].Text = session.Running ? "Running" : "Stopped";
        }
    }

    private int NextFreePort(int from)
    {
        int port = from;
        while (sessions.Any(s => s.HostPort == port || s.SidecarPort == port) || !IsPortFree(port))
        {
            port++;
        }

        return port;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using TcpListener probe = new(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // The launcher is a repo sample: walk up from its own folder until the repo
    // layout (src/ClaudeWorkbench.Host) is found.
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "ClaudeWorkbench.Host", "ClaudeWorkbench.Host.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the ClaudeShell repo root above the launcher.");
    }
}

// Name + optional workspace folder; empty folder = a temp workspace that is
// deleted when the session stops.
public sealed class NewSessionDialog : Form
{
    private readonly TextBox nameBox = new();
    private readonly TextBox pathBox = new();

    public string SessionName => nameBox.Text.Trim();

    public string WorkspacePath => pathBox.Text.Trim();

    public NewSessionDialog(string defaultName)
    {
        Text = "New Session";
        Width = 520;
        Height = 210;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        Label nameLabel = new() { Text = "Name", Left = 12, Top = 15, Width = 70 };
        nameBox.Left = 90; nameBox.Top = 12; nameBox.Width = 390; nameBox.Text = defaultName;

        Label pathLabel = new() { Text = "Folder", Left = 12, Top = 47, Width = 70 };
        pathBox.Left = 90; pathBox.Top = 44; pathBox.Width = 310;
        Button browse = new() { Text = "Browse...", Left = 405, Top = 43, Width = 75 };
        browse.Click += (_, _) =>
        {
            using FolderBrowserDialog picker = new();
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                pathBox.Text = picker.SelectedPath;
            }
        };

        Label hint = new()
        {
            Text = "Leave the folder empty for a temporary workspace — it is deleted when the session stops.",
            Left = 90, Top = 74, Width = 390, Height = 30,
        };

        Button ok = new() { Text = "Create", DialogResult = DialogResult.OK, Left = 315, Top = 120, Width = 80 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 400, Top = 120, Width = 80 };
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange([nameLabel, nameBox, pathLabel, pathBox, browse, hint, ok, cancel]);
    }
}
