using System.Net.Http.Json;
using System.Text.Json;
using ClaudeWorkbench.Host.Console;
using Microsoft.Extensions.Hosting;

namespace ClaudeWorkbench.Host.Services;

// Polls Claude auth on an interval and caches the result so the command-bar dot can
// render every frame without shelling a CLI. The probe lives behind the sidecar's
// /auth endpoint (the sidecar owns the Claude CLI relationship).
//
// Tri-state (null = unknown): a failed sidecar fetch or a missing CLI leaves the flag
// null rather than flipping it to a false "signed out", so the dot degrades to a
// neutral "checking" state instead of lying.
public sealed class AuthStatusProbe : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly IHttpClientFactory httpClientFactory;
    private readonly SidecarOptions options;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    public AuthStatusProbe(IHttpClientFactory httpClientFactory, SidecarOptions options)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options;
    }

    public event Action? Changed;

    public AuthStatus Current { get; private set; } = AuthStatus.Unknown;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        // Basic shell: only Claude auth is tracked (GitHub integration is gone). The
        // GitHub flag stays null so the AuthStatus shape is unchanged for the UI.
        AuthStatus next = new(await ProbeClaudeAsync(cancellationToken).ConfigureAwait(false), null);

        if (next != Current)
        {
            Current = next;
            Changed?.Invoke();
        }
    }

    private async Task<bool?> ProbeClaudeAsync(CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            ClaudeAuthResponse? payload = await client
                .GetFromJsonAsync<ClaudeAuthResponse>(options.BaseUrl + "/auth", json, cancellationToken)
                .ConfigureAwait(false);
            return payload?.LoggedIn;
        }
        catch (Exception)
        {
            // Sidecar down / not yet up — unknown, not signed out.
            return null;
        }
    }

    private sealed record ClaudeAuthResponse(bool? LoggedIn);
}
