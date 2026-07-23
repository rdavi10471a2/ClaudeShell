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

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif",
    };

    public static void MapLocalFiles(this WebApplication app)
    {
        app.MapGet("/local-file", (string path, WorkspaceManager workspace, AgentFileAccess fileAccess, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.BadRequest();
            }

            string full;
            try
            {
                // Accept git-bash POSIX paths too (/tmp/x, /c/Users/x) — Path.GetFullPath
                // would otherwise resolve /tmp/x to <cwd-drive>:\tmp\x.
                full = Path.GetFullPath(LocalPaths.ToWindows(path));
            }
            catch (Exception)
            {
                return Results.BadRequest();
            }

            // Serve a file if it is under the workspace, the agent read/wrote it this
            // thread (surfaced to — and for writes gated by — the operator), OR it is an
            // IMAGE under the OS temp dir. That last case covers the common flow where the
            // agent downloads a picture via curl/Bash to /tmp (= %TEMP%) — no file_path to
            // track — then embeds it; limiting it to image extensions keeps the rest of
            // temp (other apps' scratch files) unreadable.
            if (!IsUnderAllowedRoot(full, workspace, config)
                && !fileAccess.Contains(full)
                && !IsImageUnderTemp(full))
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

    // An image file sitting under the OS temp directory (where git-bash /tmp resolves).
    private static bool IsImageUnderTemp(string fullPath)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(fullPath)))
        {
            return false;
        }

        try
        {
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            return fullPath.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
