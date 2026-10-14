namespace Ashlar.Abstractions.Security;

/// <summary>
/// Diagnostic id and help link shared by every <c>[Experimental]</c> type of the security and deployment-profile
/// surface of <c>Ashlar.Abstractions</c>: every public type in <c>Ashlar.Abstractions.Security</c> and
/// <c>Ashlar.Abstractions.Security.Egress</c>, and <c>Ashlar.Abstractions.AshlarResolvedDeploymentProfileOptions</c>
/// (SPEC-007, decisions log 2026-10-13). Consumers that opt in suppress <c>ASHLAREXP002</c>.
/// See docs/SdkCompatibilityPolicy.md ("ASHLAREXP002").
/// </summary>
/// <remarks>
/// <para>Deliberately NOT experimental itself, like <c>Ashlar.Core.Application.Autonomy.AutonomyExperimental</c>:
/// an attribute binds its arguments in the scope of the declaration it is on, so an experimental holder would trip
/// the diagnostic it names wherever a member-level attribute used it.</para>
/// <para>Deliberately <c>internal</c>, unlike that holder. The attribute's arguments are constants, compiled into
/// each attribute's metadata, so no consumer ever needs this type. A public holder would add a non-experimental
/// line to <c>PublicAPI.Unshipped.txt</c> that the release promotion ships as stable: a promise of two constant
/// names that nobody outside this assembly uses. The friend assemblies listed in the project file can see it, but
/// the id names this assembly's surface and nothing else: <c>DiagnosticSuppressionConventionTests</c> (cert-gate)
/// fails an <c>ASHLAREXP002</c> declaration anywhere outside <c>src/Ashlar.Abstractions/</c>.</para>
/// </remarks>
internal static class SecurityExperimental
{
    /// <summary>The compiler diagnostic raised when an experimental security or deployment-profile API is used.</summary>
    internal const string DiagnosticId = "ASHLAREXP002";

    /// <summary>Help link attached to the diagnostic (the policy section that explains it).</summary>
    internal const string UrlFormat = "https://github.com/IanFrelinger/Ashlar/blob/master/docs/SdkCompatibilityPolicy.md#ashlarexp002";
}
