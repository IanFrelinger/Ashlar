using System.Reflection;
using System.Runtime.ExceptionServices;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Tests.Infrastructure.Helpers;

/// <summary>
/// Snapshots the process-wide egress state when created and restores it when disposed (SPEC-007 PR 4.6, design
/// §2.10): the deployment profile <c>AddAshlar</c> noted, and the egress-mode latch (the override variable as read
/// once, the hosting raise, the stderr announcement and the mode-resolution probe).
/// </summary>
/// <remarks>
/// <para>That state belongs to the process. The strictest profile noted wins and the override is read once, so a
/// test that composes AirGapped or SecureWorkstation, raises the mode or sets the override variable would otherwise
/// leave it to every later test. Use this in a class in the serialized <c>EnvironmentVariables</c> collection:
/// restoring says nothing about a class running beside you.</para>
/// <para>The seam is internal to <c>Ashlar.Abstractions</c>, and this assembly is not in its
/// <c>InternalsVisibleTo</c>, so it is reached by reflection.</para>
/// </remarks>
public sealed class EgressProcessStateScope : IDisposable
{
    private const string SeamTypeName = "Ashlar.Abstractions.Security.Egress.EgressProcessState";
    private const string EnforcementTypeName = "Ashlar.Abstractions.Security.Egress.EgressEnforcement";

    private static readonly Assembly Abstractions = typeof(EgressGuard).Assembly;

    private readonly object _snapshot;
    private int _disposed;

    /// <summary>Takes the snapshot; with <paramref name="reset"/>, then clears the state.</summary>
    /// <param name="reset"><see langword="true"/> to start from a process that has noted nothing.</param>
    public EgressProcessStateScope(bool reset = false)
    {
        _snapshot = Invoke(SeamTypeName, "Snapshot")!;
        if (reset)
            Reset();
    }

    /// <summary>Clears the noted profile and the latch, so the next decision reads the override variable again.</summary>
    public static void Reset() => Invoke(SeamTypeName, "Reset");

    /// <summary>The profile <c>AddAshlar</c> noted, or <see langword="null"/>.</summary>
    public static string? NotedProfile =>
        (string?)Abstractions.GetType("AshlarDeploymentProfileEnvironment", throwOnError: true)!
            .GetProperty("ResolvedRaw", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null);

    /// <summary>Notes a profile the way <c>AddAshlar</c> does, through the strictest-wins rule.</summary>
    /// <param name="canonical">The canonical profile name.</param>
    public static void NoteProfile(string canonical) =>
        Abstractions.GetType("AshlarDeploymentProfileEnvironment", throwOnError: true)!
            .GetMethod("NoteResolved", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [canonical]);

    /// <summary>
    /// Sets the mode-resolution probe, which runs at the start of every mode resolution, after the decision has read
    /// its profile; a throwing probe makes the resolution fault. The probe is an <c>AsyncLocal</c>: it reaches only
    /// decisions made on the calling flow and the tasks it starts, never a test running beside it. Disposing a scope
    /// taken on the same flow restores the previous probe.
    /// </summary>
    /// <param name="probe">The probe, or <see langword="null"/>.</param>
    public static void SetModeResolutionProbe(Action? probe) =>
        Abstractions.GetType(EnforcementTypeName, throwOnError: true)!
            .GetProperty("ModeResolutionProbe", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, probe);

    /// <summary>Restores the snapshot. Disposing twice does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Invoke(SeamTypeName, "Restore", _snapshot);
    }

    private static object? Invoke(string typeName, string method, params object[] arguments)
    {
        var type = Abstractions.GetType(typeName, throwOnError: true)!;
        var info = type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeName, method);
        try
        {
            return info.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
