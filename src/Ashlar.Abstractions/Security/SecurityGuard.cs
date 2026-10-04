namespace Ashlar.Abstractions.Security;

/// <summary>Argument guards shared by the security types; works on every target framework.</summary>
internal static class SecurityGuard
{
    internal static void ThrowIfNull(object? value, string paramName)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(value, paramName);
#else
        if (value is null)
            throw new ArgumentNullException(paramName);
#endif
    }
}
