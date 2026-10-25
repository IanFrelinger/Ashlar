namespace Ashlar.Core.Domain.Bricks;

/// <summary>
/// A brick type registered for a host's brick registry by code that does not reference the host.
/// <c>AddAshlarBrick&lt;T&gt;()</c> in <c>Ashlar.Authoring</c> adds one per call as a singleton, and the
/// brick registry that <c>AddAshlar()</c> builds creates an instance of every registered type, after the
/// types configured through <c>AdaptationBrickOptions</c>.
/// </summary>
public sealed class BrickTypeRegistration
{
    /// <summary>Creates a registration for <paramref name="brickType"/>.</summary>
    /// <param name="brickType">The brick type; the registry creates it with <c>ActivatorUtilities</c>.</param>
    public BrickTypeRegistration(Type brickType)
    {
        BrickType = brickType ?? throw new ArgumentNullException(nameof(brickType));
    }

    /// <summary>The registered brick type.</summary>
    public Type BrickType { get; }
}
