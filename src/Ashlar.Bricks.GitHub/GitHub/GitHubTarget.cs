using System.Text.RegularExpressions;

namespace Ashlar.Bricks.GitHub;

internal enum GitHubTargetKind
{
    PullRequest,
    Release,
}

/// <summary>
/// A pull request or release on github.com, parsed from its web URL. Only <c>https://github.com</c> (or
/// <c>www.github.com</c>) is accepted: the client these bricks build talks to <c>api.github.com</c>, so a URL on any
/// other host would be answered about a different repository than the one it names.
/// </summary>
internal sealed record GitHubTarget(GitHubTargetKind Kind, string Owner, string Repo, int Number, string Tag)
{
    private static readonly Regex Name = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);

    public string WebUrl => Kind == GitHubTargetKind.PullRequest
        ? $"https://github.com/{Owner}/{Repo}/pull/{Number}"
        : $"https://github.com/{Owner}/{Repo}/releases/tag/{Uri.EscapeDataString(Tag)}";

    public string Label => Kind == GitHubTargetKind.PullRequest ? $"PR #{Number}" : $"release {Tag}";

    public static bool TryParse(string? url, out GitHubTarget? target, out string error)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            error = $"Not an absolute URL: '{url}'.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = $"Only https://github.com URLs are supported; got host '{uri.Host}'.";
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length >= 4 && Name.IsMatch(parts[0]) && Name.IsMatch(parts[1]))
        {
            if (parts.Length == 4 && parts[2] == "pull"
                && int.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                && number > 0)
            {
                target = new GitHubTarget(GitHubTargetKind.PullRequest, parts[0], parts[1], number, string.Empty);
                error = string.Empty;
                return true;
            }

            if (parts.Length >= 5 && parts[2] == "releases" && parts[3] == "tag")
            {
                var tag = Uri.UnescapeDataString(string.Join("/", parts.Skip(4)));
                if (tag.Length > 0)
                {
                    target = new GitHubTarget(GitHubTargetKind.Release, parts[0], parts[1], 0, tag);
                    error = string.Empty;
                    return true;
                }
            }
        }

        error = $"Expected https://github.com/<owner>/<repo>/pull/<number> or https://github.com/<owner>/<repo>/releases/tag/<tag>; got '{url}'.";
        return false;
    }
}
