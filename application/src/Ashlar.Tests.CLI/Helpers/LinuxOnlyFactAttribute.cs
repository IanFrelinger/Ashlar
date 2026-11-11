using Xunit;

namespace Ashlar.Tests.CLI.Helpers;

/// <summary>
/// A <see cref="FactAttribute"/> for claims whose FIXTURE only exists on Linux. Same shape as
/// <see cref="UnixOnlyFactAttribute"/>: anywhere else the test is reported as <b>Skipped</b> with the
/// reason, never as Passed, so "not applicable on this host" stays distinct from "verified here".
///
/// <para>The case that needs it: a symlink TARGET longer than 1,023 bytes. Linux allows 4,095; macOS
/// caps a target at PATH_MAX (1,024 including the terminator) and .NET throws
/// <see cref="System.IO.PathTooLongException"/> creating one. Such a test cannot be stated on
/// macOS, and there it is also not needed: what it guards against needs the longer target.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    /// <summary>Creates a fact that is skipped on every OS but Linux.</summary>
    /// <param name="reason">The Linux-only fixture the test needs.</param>
    public LinuxOnlyFactAttribute(string reason)
    {
        Reason = reason;
        if (!OperatingSystem.IsLinux())
        {
            Skip = $"Linux-only fixture ({reason}).";
        }
    }

    /// <summary>The Linux-only fixture this test needs.</summary>
    public string Reason { get; }
}
