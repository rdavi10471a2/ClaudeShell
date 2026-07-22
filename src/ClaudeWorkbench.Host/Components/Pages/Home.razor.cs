using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ClaudeWorkbench.Host.Components.Pages;

public partial class Home : IDisposable
{
    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

    [Inject]
    private IApprovalQueue Approvals { get; set; } = default!;

    [Inject]
    private WorkspaceManager Workspace { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private const string AppTitle = "ClaudeWorkbench --Basic";

    // Browser tab title: workspace folder first so each window is distinguishable.
    private string PageTitleText => !string.IsNullOrWhiteSpace(Workspace.BasePath)
        ? $"{Path.GetFileName(Workspace.BasePath.TrimEnd('\\', '/'))} — {AppTitle}"
        : AppTitle;

    // Command-bar auth cue. The Claude dot carries the sidecar-down message too,
    // because when the sidecar is down its login state is genuinely unknowable — so
    // the root cause ("Sidecar unavailable") is the honest thing to show there.
    private AuthCue ClaudeCue => !Session.Status.Connected
        ? new("down", "Sidecar unavailable", "The Claude sidecar is not reachable — no turns can run until it is back.")
        : Session.Auth.Claude switch
        {
            true => new("up", "Claude available", "Signed in to the Claude CLI."),
            false => new("warn", "Claude signed out", "The Claude CLI is signed out. Run `claude` once to sign in."),
            _ => new("unknown", "Claude — checking", "Checking Claude login state..."),
        };

    private readonly record struct AuthCue(string Css, string Label, string Title);

    private bool settingsOpen;
    private bool aboutOpen;
    private bool helpOpen;
    private IJSObjectReference? unloadModule;

    protected override void OnInitialized()
    {
        Session.Changed += OnChanged;
        Workspace.Changed += OnChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            unloadModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js");
            // When a launcher owns this instance, the tab close is intentional (it tears the
            // backend down), so the "leaving will reset your session" guard must NOT fire.
            bool launcherOwned = string.Equals(
                Environment.GetEnvironmentVariable("CWB_EXIT_WITH_BROWSER"), "1", StringComparison.Ordinal);
            if (!launcherOwned)
            {
                await unloadModule.InvokeVoidAsync(
                    "setBeforeUnloadGuard",
                    true,
                    "Leaving or refreshing will reset the current Claude session.");
            }
        }
    }

    private void OnChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Session.Changed -= OnChanged;
        Workspace.Changed -= OnChanged;
    }
}
