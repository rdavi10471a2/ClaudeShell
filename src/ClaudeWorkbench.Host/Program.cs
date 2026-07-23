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

// Files the agent has read/written this thread, so /local-file can serve them back
// to chat even when they live outside the workspace.
builder.Services.AddSingleton<AgentFileAccess>();

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

// Content-Security-Policy — defense-in-depth behind MarkdownRenderer (which escapes raw
// HTML and neutralizes external images in untrusted model output). Tighter than the
// governed app's: ClaudeShell vendors mermaid locally and has no Monaco/CDN, so nothing
// off-origin is trusted. 'unsafe-inline'/'unsafe-eval' are for Blazor/Radzen and mermaid;
// ws: is the SignalR circuit; blob: workers are mermaid's; images are same-origin + data:.
app.Use(async (context, next) =>
{
    context.Response.Headers.Append(
        "Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss:; " +
        "worker-src 'self' blob:; " +
        "child-src 'self' blob:; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'");
    await next();
});

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
