using System.Text.RegularExpressions;

namespace ClaudeWorkbench.Host.Services;

// The agent runs under git-bash, so it emits POSIX-style paths — /tmp/x, /c/Users/x —
// even for files on a Windows disk. The chat renderer and /local-file both need the
// real Windows path, so translate the two forms git-bash actually produces.
public static partial class LocalPaths
{
    // /c/Users/... or /d/foo (a drive letter, then end or a slash).
    [GeneratedRegex(@"^/([A-Za-z])(?:/(.*))?$")]
    private static partial Regex DriveMountRegex();

    // The Windows path a POSIX path refers to, or null when it is not one we translate
    // (so callers can leave real app routes like /about alone).
    public static string? FromPosix(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return null;
        }

        // git-bash maps /tmp to the OS temp directory.
        if (path == "/tmp" || path.StartsWith("/tmp/", StringComparison.Ordinal))
        {
            string rest = path.Length > 5 ? path[5..] : string.Empty;
            return Path.Combine(Path.GetTempPath(), rest.Replace('/', Path.DirectorySeparatorChar));
        }

        // /<drive>/rest -> <DRIVE>:\rest
        Match match = DriveMountRegex().Match(path);
        if (match.Success)
        {
            string drive = match.Groups[1].Value.ToUpperInvariant();
            string rest = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
            return drive + ":\\" + rest.Replace('/', '\\');
        }

        return null;
    }

    // The path as Windows sees it: a translated POSIX path, or the input unchanged.
    public static string ToWindows(string path) => FromPosix(path) ?? path;
}
