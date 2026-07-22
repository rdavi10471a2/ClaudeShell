namespace ClaudeWorkbench.Host.Console;

// Operator-selected agent settings. In Basic there is no tool policy — all native
// tools are available and every call pauses at the operator's Allow/Deny gate — so
// this is just model + reasoning effort. Persisted host-side, sent per turn.
public sealed class AgentToolPolicy
{
    // Model id for the agent (empty => inherit the sidecar/subscription default).
    public string Model { get; set; } = string.Empty;

    // Reasoning effort: "", low, medium, high, xhigh, max (empty => default).
    public string Effort { get; set; } = string.Empty;

    public AgentToolPolicy Clone()
    {
        return new AgentToolPolicy
        {
            Model = Model,
            Effort = Effort,
        };
    }
}

// Model choices offered in the settings dialog. Empty value = inherit the default.
public sealed record AgentModelOption(string Label, string Value);

public static class AgentModelOptions
{
    public static readonly IReadOnlyList<AgentModelOption> All =
    [
        new("Default (inherit)", ""),
        new("Opus 4.8", "claude-opus-4-8"),
        new("Sonnet 5", "claude-sonnet-5"),
        new("Haiku 4.5", "claude-haiku-4-5-20251001"),
        new("Fable 5", "claude-fable-5"),
    ];
}

// Reasoning-effort choices (empty value = default). Maps to the SDK `effort` option.
public static class ReasoningLevels
{
    public static readonly IReadOnlyList<string> All = ["", "low", "medium", "high", "xhigh", "max"];
}
