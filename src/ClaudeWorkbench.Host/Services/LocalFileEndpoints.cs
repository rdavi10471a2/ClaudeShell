using Microsoft.AspNetCore.StaticFiles;

namespace ClaudeWorkbench.Host.Services;

// Serves local files referenced in chat markdown (links/images the agent produced).
// The browser cannot load file:// URIs from a localhost-served page, so MarkdownRenderer
// rewrites them to /local-file?path=... and this endpoint streams the bytes.
//
// Security: only paths under the workspace base folder (plus any configured
// "LocalFiles:AllowedRoots") are served; everything else is 403. Without this check
// the endpoint would be an arbitrary-file-read hole.
public static class LocalFileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapLocalFiles(this WebApplication app)
    {
        app.MapGet("/local-file", (string path, WorkspaceManager workspace, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.BadRequest();
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return Results.BadRequest();
            }

            if (!IsUnderAllowedRoot(full, workspace, config))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!File.Exists(full))
            {
                return Results.NotFound();
            }

            if (!ContentTypes.TryGetContentType(full, out string? contentType))
            {
                contentType = "application/octet-stream";
            }

            return Results.File(full, contentType, enableRangeProcessing: true);
        });
    }

    private static bool IsUnderAllowedRoot(string fullPath, WorkspaceManager workspace, IConfiguration config)
    {
        List<string> roots = new() { workspace.BasePath };
        string[]? extra = config.GetSection("LocalFiles:AllowedRoots").Get<string[]>();
        if (extra is not null)
        {
            roots.AddRange(extra);
        }

        foreach (string root in roots)
        {
            string normalizedRoot;
            try
            {
                normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            }
            catch (Exception)
            {
                continue;
            }

            if (fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
