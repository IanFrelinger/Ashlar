namespace Ashlar.Tests.Kernel;

/// <summary>
/// The serialized collection for kernel facts that mutate a PROCESS-GLOBAL environment variable —
/// restoring one in a <c>Dispose</c> does nothing about the class running beside you, so the only
/// fix is <c>DisableParallelization</c> (see
/// <c>Ashlar.Tests.Infrastructure.Tests.Certification.ProcessGlobalEnvironmentConventionTests</c>).
///
/// <para><b>Why gate-store facts belong here.</b>
/// <see cref="Ashlar.Manifest.Admission.GateStore"/>'s constructor pins its signer set from
/// <c>Ashlar.Manifest.Signing.OperatorKey.TrustedPublicKeysBase64()</c>, which resolves
/// <c>ASHLAR_KEY_DIR</c> and falls back to <c>~/.ashlar/keys</c>. So ANY fact that constructs a
/// store is reading ambient state: on a developer machine that has run <c>ashlar keys init</c>, a
/// reader the fact calls "keyless" silently holds a populated pinning set and refuses records the
/// fact expects it to accept. Such a class joins this collection and points the variable at a
/// FRESH EMPTY directory of its own — not at the key directory the fact generates into, which
/// would make the reader keyed and quietly invert what the fact's name promises.</para>
/// </summary>
[Xunit.CollectionDefinition("EnvironmentSensitive", DisableParallelization = true)]
public sealed class EnvironmentSensitiveCollection { }
