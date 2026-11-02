using Ashlar.Core.Domain.Bricks;

namespace Ashlar.Bricks.GitHub;

/// <summary>
/// Host-owned credential and repository scope for the read-only GitHub bricks. The credential is
/// never obtained from ambient configuration by the library: a host must explicitly supply this
/// policy. This limits calls through these bricks, not other code or network access in the process.
/// </summary>
public sealed class GitHubReadAccess
{
    /// <summary>Host configuration variable for the GitHub token.</summary>
    public const string TokenVariable = "ASHLAR_GITHUB_TOKEN";
    /// <summary>Host configuration variable for the single allowed owner/repository.</summary>
    public const string RepositoryVariable = "ASHLAR_GITHUB_REPOSITORY";

    private readonly string _token;
    private readonly string _owner;
    private readonly string _repository;

    /// <summary>Creates a credential scoped to exactly one github.com repository.</summary>
    /// <param name="token">Host-held token; its GitHub permissions remain the authority ceiling.</param>
    /// <param name="repository">The allowed repository in owner/name form.</param>
    public GitHubReadAccess(string token, string repository)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A host GitHub credential must be nonempty.", nameof(token));
        if (repository is null)
            throw new ArgumentException("A repository scope in owner/name form is required.", nameof(repository));
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p is "." or ".."
                || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
            throw new ArgumentException("The repository scope must be a github.com owner/name, without a URL or wildcard.", nameof(repository));
        _token = token;
        _owner = parts[0];
        _repository = parts[1];
    }

    /// <summary>
    /// Reads the host's configuration once. Both absent returns null; partial or empty settings
    /// fail startup rather than silently falling back to anonymous or caller-supplied access.
    /// </summary>
    /// <param name="read">Host configuration reader; values must never be logged.</param>
    public static GitHubReadAccess? FromConfiguration(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var token = read(TokenVariable);
        var repository = read(RepositoryVariable);
        if (token is null && repository is null)
            return null;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(repository))
            throw new InvalidOperationException($"{TokenVariable} and {RepositoryVariable} must both be supplied and nonempty for host GitHub access.");
        return new GitHubReadAccess(token, repository);
    }

    // Caller tokens cannot replace a host credential or enlarge its repository scope. Neither a
    // refusal nor ToString exposes the token. Validate BEFORE constructing a client or sending.
    internal string ResolveToken(string owner, string repository, string? callerToken)
    {
        if (!string.Equals(owner, _owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repository, _repository, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The requested repository is outside this host's configured GitHub scope.");
        return _token;
    }

    // Keep the legacy library schema available, but a host-held credential should never be
    // requested from the model. Raw callers may still send the old field; it is ignored above.
    internal static BrickInterface WithoutCallerCredential(BrickInterface contract) => new()
    {
        Inputs = contract.Inputs.Where(input => input.Name != "githubToken").ToArray(),
        Outputs = contract.Outputs,
    };
}
