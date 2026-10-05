namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// One attempt to send something out of the process: which family of path, which inventoried site, and where to.
/// </summary>
/// <remarks>
/// <para>A request names its destination either as a <see cref="Uri"/> or as a name. Only the scheme, host and
/// port of a URI are ever recorded; userinfo, path, query and fragment never reach a decision record.</para>
/// <para>A name that starts with <c>host:</c> (for example <c>host:dotnet</c>) declares a destination inside
/// the host boundary. A name that starts with <c>file:</c>, in any case, is a file path: it is recorded as written and
/// is never inside the host boundary, and neither is a <c>file</c> URI. Any other name shaped like a URL
/// (<c>scheme://…</c>) is read as a URI and redacted the same way; when its authority cannot be read without guessing,
/// it is recorded as <c>scheme://&lt;unparsed&gt;</c>.</para>
/// </remarks>
public sealed class EgressRequest
{
    /// <summary>Creates a request whose destination is a URI.</summary>
    /// <param name="family">The path family, one of <see cref="EgressFamilies"/>.</param>
    /// <param name="site">The inventoried site: an <c>EG-…</c> id from <c>docs/EgressInventory.md</c>, or
    /// <c>factory:&lt;client&gt;</c> or <c>meai:&lt;key&gt;</c>.</param>
    /// <param name="destination">Where the request goes. Only its scheme, host and port are recorded.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public EgressRequest(string family, string site, Uri destination)
    {
        SecurityGuard.ThrowIfNull(family, nameof(family));
        SecurityGuard.ThrowIfNull(site, nameof(site));
        SecurityGuard.ThrowIfNull(destination, nameof(destination));
        Family = family;
        Site = site;
        Destination = destination;
    }

    /// <summary>Creates a request whose destination is a name rather than a URI.</summary>
    /// <param name="family">The path family, one of <see cref="EgressFamilies"/>.</param>
    /// <param name="site">The inventoried site: an <c>EG-…</c> id from <c>docs/EgressInventory.md</c>, or
    /// <c>factory:&lt;client&gt;</c> or <c>meai:&lt;key&gt;</c>.</param>
    /// <param name="destinationName">A name for the destination, such as <c>aws-bedrock</c>. A name starting with
    /// <c>host:</c> declares a destination inside the host boundary.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public EgressRequest(string family, string site, string destinationName)
    {
        SecurityGuard.ThrowIfNull(family, nameof(family));
        SecurityGuard.ThrowIfNull(site, nameof(site));
        SecurityGuard.ThrowIfNull(destinationName, nameof(destinationName));
        Family = family;
        Site = site;
        DestinationName = destinationName;
    }

    /// <summary>The path family, one of <see cref="EgressFamilies"/>. The family decides the destination class.</summary>
    public string Family { get; }

    /// <summary>The inventoried site id (<c>EG-…</c>, <c>factory:&lt;client&gt;</c> or <c>meai:&lt;key&gt;</c>).</summary>
    public string Site { get; }

    /// <summary>The destination URI, or <see langword="null"/> when the request names its destination instead.</summary>
    public Uri? Destination { get; }

    /// <summary>The destination name, or <see langword="null"/> when the request carries a URI instead.</summary>
    public string? DestinationName { get; }
}
