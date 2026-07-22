namespace ClaudeShell.Launcher;

// SAMPLE: a minimal WinForms session manager for ClaudeWorkbench --Basic.
// Shows how to run several independent front ends side by side: each session is
// one host process with its own port pair and its own workspace folder. Sessions
// on a launcher-created temp workspace are deleted from disk when they stop, so
// scratch sessions cost no disk space after use.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
