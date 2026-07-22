using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Dialogs;

public partial class AgentSettingsDialog
{
    [Inject]
    private AgentSettingsService Settings { get; set; } = default!;

    [Parameter]
    public bool Visible { get; set; }

    [Parameter]
    public EventCallback OnClose { get; set; }

    private AgentToolPolicy draft = new();
    private bool wasVisible;

    protected override void OnParametersSet()
    {
        if (Visible && !wasVisible)
        {
            draft = Settings.Current;
        }

        wasVisible = Visible;
    }

    private async Task Save()
    {
        Settings.Update(draft);
        await OnClose.InvokeAsync();
    }

    private async Task Cancel()
    {
        await OnClose.InvokeAsync();
    }
}
