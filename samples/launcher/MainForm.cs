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
    private readonly Button claudeButton = new() { Text = "Claude sign-in", Width = 110 };
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
        public Process? Browser { get; set; }
        public string Url => $"http://localhost:{HostPort}";
        public bool Running => Process is { HasExited: false };
        // Per-session Chrome/Edge profile, so each --app window is its own instance
        // (own history/cookies) and can be closed independently. Under temp so it's cleanable.
        public string BrowserProfile => Path.Combine(Path.GetTempPath(), "ClaudeShell", "browser-profiles", Name);
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
        buttons.Controls.AddRange([newButton, startButton, stopButton, openButton, removeButton, claudeButton]);
        newButton.Click += (_, _) => NewSession();
        startButton.Click += (_, _) => StartSelected();
        stopButton.Click += (_, _) => StopSelected();
        openButton.Click += (_, _) => OpenSelected();
        removeButton.Click += (_, _) => RemoveSelected();

        // The Claude button drops a small menu: sign in, check status, sign out. Each
        // opens a terminal on the CLI's own interactive flow - see AuthLauncher for why
        // a terminal. This is THE login path when no other Claude tool on the machine
        // is signed in yet; skip it if Claude Code / the claude CLI already is.
        ContextMenuStrip claudeMenu = new();
        claudeMenu.Items.Add("Sign in to Claude…", null, (_, _) => RunAuth(AuthLauncher.LaunchLogin));
        claudeMenu.Items.Add("Check Claude status", null, (_, _) => RunAuth(AuthLauncher.LaunchStatus));
        // Sign-out first is how you force a genuinely fresh login: `login` on an
        // already-authenticated CLI can short-circuit.
        claudeMenu.Items.Add("Sign out of Claude", null, (_, _) => RunAuth(AuthLauncher.LaunchLogout));
        claudeButton.Click += (_, _) => claudeMenu.Show(claudeButton, new Point(0, claudeButton.Height));
        new ToolTip().SetToolTip(claudeButton,
            "Sign in / status / sign out for the Claude CLI. The login is cached per machine " +
            "and shared by every session - skip if another Claude tool is already signed in.");

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
        (string? exe, string? project, string sidecarDir, string workingDir) = ResolveInstall();

        ProcessStartInfo info = exe is not null
            ? new ProcessStartInfo(exe) { WorkingDirectory = workingDir }
            : new ProcessStartInfo("dotnet", $"run --project \"{project}\" --no-launch-profile") { WorkingDirectory = workingDir };
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
        // Close the session's --app browser window first (Chromium keeps running after the
        // host dies; the operator would otherwise be left with a dead window).
        try
        {
            if (session.Browser is { HasExited: false } browser)
            {
                browser.CloseMainWindow();
                if (!browser.WaitForExit(1500))
                {
                    browser.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception)
        {
            // best effort
        }
        finally
        {
            session.Browser = null;
        }

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
            DeleteBrowserProfile(session);
        }

        RefreshRows();
    }

    private static void DeleteBrowserProfile(Session session)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(session.BrowserProfile))
                {
                    Directory.Delete(session.BrowserProfile, recursive: true);
                }

                return;
            }
            catch (Exception)
            {
                Thread.Sleep(400); // the browser may still be releasing profile locks
            }
        }
    }

    private void OpenSelected()
    {
        if (Selected is not { } session)
        {
            return;
        }

        // Already have a live app window for this session — don't spawn a second.
        if (session.Browser is { HasExited: false })
        {
            return;
        }

        // Prefer a Chromium browser in --app mode: a distinct, chrome-less window per
        // session (own profile), opened truly maximized. Falls back to the default
        // browser (a normal tab) when neither Chrome nor Edge is installed.
        string? chromium = FindChromium();
        if (chromium is null)
        {
            Process.Start(new ProcessStartInfo(session.Url) { UseShellExecute = true });
            return;
        }

        Directory.CreateDirectory(session.BrowserProfile);
        ProcessStartInfo info = new() { FileName = chromium, UseShellExecute = false };
        info.ArgumentList.Add($"--app={session.Url}");
        info.ArgumentList.Add($"--user-data-dir={session.BrowserProfile}");
        info.ArgumentList.Add("--no-first-run");
        info.ArgumentList.Add("--no-default-browser-check");
        // Without this the --app window opens at a default size whose bottom spills behind
        // the taskbar. Open it truly maximized, respecting the work area.
        info.ArgumentList.Add("--start-maximized");

        try
        {
            session.Browser = Process.Start(info);
        }
        catch (Exception)
        {
            Process.Start(new ProcessStartInfo(session.Url) { UseShellExecute = true });
        }
    }

    // First installed Chrome, then Edge, from the standard per-user/machine locations.
    private static string? FindChromium()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
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

    // Confirm a Claude CLI is reachable (PATH, or the copy bundled inside the
    // sidecar's node_modules), then run the chosen auth flow in its own terminal.
    private void RunAuth(Action<string?> launch)
    {
        string? bundledCliJs = BundledClaudeCli();
        if (!AuthLauncher.IsAvailable(bundledCliJs))
        {
            MessageBox.Show(this, AuthLauncher.InstallHint, "Claude sign-in");
            return;
        }

        try
        {
            launch(bundledCliJs);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not start the Claude CLI:\n{exception.Message}", "Claude sign-in");
        }
    }

    // The Agent SDK vendors the claude CLI; a publish-live install has it under
    // <root>\sidecar\node_modules even when nothing is installed globally.
    private static string? BundledClaudeCli()
    {
        try
        {
            string sidecarDir = ResolveInstall().SidecarDir;
            string cli = Path.Combine(sidecarDir, "node_modules", "@anthropic-ai", "claude-agent-sdk", "cli.js");
            return File.Exists(cli) ? cli : null;
        }
        catch (InvalidOperationException)
        {
            return null;
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

    // Works from both layouts, probed by walking up from the launcher's own folder:
    //   published install:  <root>\host\ClaudeWorkbench.Host.exe + <root>\sidecar\dist\index.js
    //   repo checkout:      <root>\src\ClaudeWorkbench.Host\... + <root>\sidecar\basic
    private static (string? Exe, string? Project, string SidecarDir, string WorkingDir) ResolveInstall()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string installedExe = Path.Combine(dir.FullName, "host", "ClaudeWorkbench.Host.exe");
            string installedSidecar = Path.Combine(dir.FullName, "sidecar");
            if (File.Exists(installedExe) && File.Exists(Path.Combine(installedSidecar, "dist", "index.js")))
            {
                return (installedExe, null, installedSidecar, Path.GetDirectoryName(installedExe)!);
            }

            string project = Path.Combine(dir.FullName, "src", "ClaudeWorkbench.Host", "ClaudeWorkbench.Host.csproj");
            if (File.Exists(project))
            {
                string sidecarDir = Path.Combine(dir.FullName, "sidecar", "basic");
                string exeDebug = Path.Combine(dir.FullName, "src", "ClaudeWorkbench.Host", "bin", "Debug", "net10.0", "ClaudeWorkbench.Host.exe");
                string exeRelease = Path.Combine(dir.FullName, "src", "ClaudeWorkbench.Host", "bin", "Release", "net10.0", "ClaudeWorkbench.Host.exe");
                string? exe = File.Exists(exeDebug) ? exeDebug : File.Exists(exeRelease) ? exeRelease : null;
                return exe is not null
                    ? (exe, null, sidecarDir, Path.GetDirectoryName(exe)!)
                    : (null, project, sidecarDir, dir.FullName);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate a ClaudeShell install (host + sidecar) or repo checkout above the launcher.");
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
