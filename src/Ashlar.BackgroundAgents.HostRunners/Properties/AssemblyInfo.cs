// Assembly metadata for Ashlar.BackgroundAgents.HostRunners.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Ashlar.Tests.BackgroundAgents")]
// The cert-gated write-edge floor tests (Tests/Certification) drive the factory's policy chain
// and the write-path harvester directly; the permissive tests that pin the old edge all live in
// namespaces the cert-gate filter does not match.
[assembly: InternalsVisibleTo("Ashlar.Tests.Infrastructure")]
