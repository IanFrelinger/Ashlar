using System.Reflection;
using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.Composition;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The composition lane's key path may not widen, and the delegation may not become decorative.
///
/// <para><b>Why this blocks a merge.</b> The fix for limitation 9 is a shape, not a value: the
/// composition signer signs under the brick lane's key BECAUSE it holds no key. Every functional
/// fact in <see cref="CompositionSignerKeyThreadingTests"/> stays green if an implementer
/// repopulates the key bytes "for safety" while still delegating — until a later edit to
/// <c>Sign</c> flips back to the local bytes and the operator key silently stops arriving. The
/// first fact below is the only one that catches that.</para>
///
/// <para><b>These are tripwires, not proofs.</b> The second fact is a text scan: an alias, a
/// <c>using static</c>, a method group taken into a local, or a renamed call would defeat it.
/// Framing a scan as a proof is how a gate goes quiet (<c>docs/HowGatesGoQuiet.md</c>).</para>
///
/// <para><b>Rule 3, stated plainly.</b> The scan is a pure file read. The reflection fact reads a
/// private field on an already-loaded type: no build, no SDK, no network, no clock, and it writes
/// no environment variable — hermetic in substance, if not literally a file read.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CompositionSignerKeyPathConventionTests
{
    /// <summary>Spelled with the open parenthesis so prose and see-cref references do not count.</summary>
    private const string OracleCall = "ComputeCanonicalHmac(";

    private static readonly string[] ExpectedSites =
    {
        "src/Ashlar.Infrastructure/Certification/Certification" + "RecordSigner.cs x1",
        "src/Ashlar.Infrastructure/Certification/Composition/CompositionCertificationRecordSigner.cs x1",
    };

    [Fact]
    public void TheCompositionSigner_HoldsNoKeyWhenItDelegates()
    {
        // An explicit key on the brick signer, so this fact reads no environment variable and needs
        // no collection.
        var composition = new CompositionCertificationRecordSigner(
            new CertificationRecordSigner(hmacKey: "operator-secret-not-committed"));

        var field = typeof(CompositionCertificationRecordSigner)
            .GetField("_keyBytes", BindingFlags.Instance | BindingFlags.NonPublic);

        field.Should().NotBeNull(
            "this fact is about the field that used to be the second resident copy of the operator "
            + "key; if it was renamed, re-point this test rather than deleting it");
        field!.GetValue(composition).Should().BeNull(
            "with a brick signer supplied, this signer must hold NO key material — delegation that "
            + "also caches the key is decorative, and the next edit to Sign silently re-opens "
            + "limitation 9");
    }

    [Fact]
    public void TheMacOracle_HasOneDeclarationAndOneCallSite()
    {
        var root = RepoPathResolver.FindRepoRoot();

        Sites(root).Should().Equal(
            ExpectedSites,
            "the composition lane's MAC oracle signs arbitrary text under the operator key. One "
            + "declaration, one call. A second caller means something else in this assembly gained "
            + "the ability to mint under the operator key; a missing site means the delegation is "
            + "gone. Both directions fail, so the inventory can only shrink honestly.");
    }

    private static List<string> Sites(string root)
    {
        var found = new List<string>();
        var start = Path.Combine(root, "src", "Ashlar.Infrastructure");
        if (Directory.Exists(start))
            Collect(root, start, found);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void Collect(string root, string directory, List<string> found)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            var count = 0;
            var i = text.IndexOf(OracleCall, StringComparison.Ordinal);
            while (i >= 0)
            {
                count++;
                i = text.IndexOf(OracleCall, i + OracleCall.Length, StringComparison.Ordinal);
            }

            if (count > 0)
                found.Add($"{Normalize(Path.GetRelativePath(root, file))} x{count}");
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            Collect(root, child, found);
        }
    }

    /// <summary>
    /// Build output and the root of any nested checkout — the same structural pruning the other
    /// convention tests use, because a nested git worktree holds a second copy of every file here
    /// and counting those turned a required check red on developers' machines while CI stayed green.
    /// </summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);

        if (string.Equals(name, "bin", StringComparison.Ordinal)
            || string.Equals(name, "obj", StringComparison.Ordinal)
            || string.Equals(name, ".claude", StringComparison.Ordinal))
        {
            return true;
        }

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
