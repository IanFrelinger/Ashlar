using Ashlar.Abstractions.Security.Egress;
using Octokit;
using Octokit.Internal;

namespace Ashlar.Bricks.GitHub;

/// <summary>
/// The one place the GitHub bricks build an Octokit client. Every request goes through the egress guard
/// (inventory row EG-HTTP-08), and only to <c>api.github.com</c>.
/// </summary>
internal static class GitHubClientFactory
{
    public const string EgressSite = "EG-HTTP-08";

    private static readonly ProductHeaderValue Product = new("Ashlar-GitHub-Bricks");

    /// <param name="token">A token, or <see langword="null"/>/empty for anonymous access (60 requests an hour).</param>
    /// <param name="primary">The handler that sends; <see langword="null"/> for Octokit's default. Tests pass a stub.</param>
    /// <param name="guard">The guard; <see langword="null"/> for <c>EgressGuard.ProcessDefault</c>.</param>
#pragma warning disable ASHLAREXP002 // EgressHttp, EgressFamilies and IEgressGuard are the experimental egress surface, used here by design (docs/EgressInventory.md EG-HTTP-08).
    public static GitHubClient Create(string? token, Func<HttpMessageHandler>? primary = null, IEgressGuard? guard = null) =>
        new GitHubClient(new Connection(
            Product,
            GitHubClient.GitHubApiUrl,
            new InMemoryCredentialStore(string.IsNullOrEmpty(token) ? Credentials.Anonymous : new Credentials(token)),
            new HttpClientAdapter(() => EgressHttp.Wrap(
                primary?.Invoke() ?? HttpMessageHandlerFactory.CreateDefault(), EgressFamilies.Http, EgressSite, guard)),
            new SimpleJsonSerializer()));
#pragma warning restore ASHLAREXP002
}
