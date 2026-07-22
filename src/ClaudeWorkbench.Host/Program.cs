using ClaudeWorkbench.Host.Components;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Radzen;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

// --- ClaudeWorkbench --Basic: Blazor UI + BasicSidecar relay ---------------
// No governance, no indexing, no MCP. The UI binds to IOperatorConsole/IApprovalQueue;
// SidecarOperatorConsole relays those to the Node sidecar over HTTP/SSE.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.AddHttpClient();

// The agent's working folder (base under %TEMP%\ClaudeShell + a files/ subfolder).
builder.Services.AddSingleton<WorkspaceManager>();

// Launch options first, so the sidecar port drives every URL below (a second
// instance shifts Sidecar:Port + ASPNETCORE_URLS + WORKSPACE and stays isolated).
var sidecarOptions = new SidecarLaunchOptions();
builder.Configuration.GetSection("Sidecar").Bind(sidecarOptions);
if (string.IsNullOrWhiteSpace(sidecarOptions.SidecarDirectory))
{
    sidecarOptions.SidecarDirectory = ResolveSidecarDirectory(builder.Environment.ContentRootPath);
}
builder.Services.AddSingleton(sidecarOptions);

// Sidecar control surface (base URL for the typed client + the stream/auth probes).
string sidecarBaseUrl = $"http://localhost:{sidecarOptions.Port}";
builder.Services.AddSingleton(new SidecarOptions { BaseUrl = sidecarBaseUrl });
builder.Services.AddHttpClient<SidecarClient>(client => client.BaseAddress = new Uri(sidecarBaseUrl));

// Long-lived SSE reader + auth poller (both BackgroundServices, shared as singletons).
builder.Services.AddSingleton<SidecarEventStream>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SidecarEventStream>());
builder.Services.AddSingleton<AuthStatusProbe>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuthStatusProbe>());

builder.Services.AddSingleton<AgentSettingsService>();
builder.Services.AddSingleton<UploadService>();

// The one adapter, surfaced under both UI seams.
builder.Services.AddSingleton<SidecarOperatorConsole>();
builder.Services.AddSingleton<IOperatorConsole>(sp => sp.GetRequiredService<SidecarOperatorConsole>());
builder.Services.AddSingleton<IApprovalQueue>(sp => sp.GetRequiredService<SidecarOperatorConsole>());

// Launch + supervise the Node sidecar as a child process (single start).
builder.Services.AddHostedService<SidecarProcessHost>();

// Optional: stop the app when the last browser tab closes (launcher-owned instances).
if (string.Equals(Environment.GetEnvironmentVariable("CWB_EXIT_WITH_BROWSER"), "1", StringComparison.Ordinal))
{
    builder.Services.AddSingleton<BrowserPresenceTracker>();
    builder.Services.AddScoped<CircuitHandler, BrowserLifetimeCircuitHandler>();
}

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapLocalFiles();   // serves chat-referenced local files (see LocalFileEndpoints)

app.MapGet("/health", (WorkspaceManager workspace) => Results.Json(new
{
    status = "ok",
    workspace = workspace.BasePath,
}));

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

// Resolve the sidecar folder (holding dist/index.js) relative to the host: dev runs
// from src/ClaudeWorkbench.Host (sidecar at ../../sidecar/basic); a published install
// puts host/ and sidecar/ side by side (../sidecar).
static string ResolveSidecarDirectory(string contentRoot)
{
    string[] candidates =
    {
        Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "sidecar", "basic")),
        Path.GetFullPath(Path.Combine(contentRoot, "..", "sidecar")),
        Path.GetFullPath(Path.Combine(contentRoot, "sidecar")),
    };
    foreach (string candidate in candidates)
    {
        if (File.Exists(Path.Combine(candidate, "dist", "index.js")))
        {
            return candidate;
        }
    }

    return candidates[0];
}
