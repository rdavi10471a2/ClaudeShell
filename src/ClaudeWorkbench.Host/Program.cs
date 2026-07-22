using ClaudeWorkbench.Host.Components;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

// Minimal ClaudeShell: just Blazor UI + sidecar relay (no governance, no indexing)
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    workspace = Environment.GetEnvironmentVariable("WORKSPACE") ?? Path.Combine(Path.GetTempPath(), "ClaudeShell")
}));

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
