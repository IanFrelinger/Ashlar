using FluentAssertions;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Packaging;
using Ashlar.Manifest.Signing;
using Xunit;

namespace Ashlar.Tests.Kernel.Packaging;

/// <summary>
/// The mesh store is the ONE door a package passes through on its way to peers — the pkg verbs
/// and a cycle's auto-share both use it. The properties under test: Resolve's precedence is
/// explicit dir over env over the user profile; Publish refuses what does not verify BEFORE
/// touching the store; and identical content republished lands on the identical name, so
/// re-sharing is idempotent rather than accretive.
/// </summary>
[Collection("EnvironmentSensitive")]
public sealed class MeshStoreTests : IDisposable
{
    /// <summary>A GateStore pins its signers from this variable; see
    /// <see cref="EnvironmentSensitiveCollection"/> for why every store-constructing fact pins it
    /// to an EMPTY directory of its own — here it is set before the origin key is generated.</summary>
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    private readonly string _dir;
    private readonly SigningIdentity _origin;
    private readonly string? _previousKeyDir;

    public MeshStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previousKeyDir = Environment.GetEnvironmentVariable(KeyDirVariable);
        Environment.SetEnvironmentVariable(KeyDirVariable, Directory.CreateDirectory(Path.Combine(_dir, "no-ambient-keys")).FullName);
        _origin = OperatorKey.Generate(Path.Combine(_dir, "keys"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyDirVariable, _previousKeyDir);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 25, 6, 0, 0, TimeSpan.Zero);

    /// <summary>A sealed package over a real signed Admitted record, exactly what production
    /// hands to Publish.</summary>
    private async Task<string> PackageJsonAsync(string id = "ext-mesh")
    {
        var store = new GateStore(Path.Combine(_dir, ".ashlar-" + id), _origin);
        await store.RecordAsync(new ExtensionProposal
        {
            Id = id,
            Kind = "brick",
            Summary = "add brick shared.classify",
            ProposedBy = "night-agent",
            ProposedAt = Now,
            Courses = [new CourseResult { Name = "sandbox", Passed = true, Detail = "confined" }],
            ForgeProposalIds = ["forge-1"],
        }, new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);
        var record = await store.DecideAsync(id, admit: true, "origin-operator", "reviewed", Now.AddMinutes(1));
        return ExtensionPackaging.Pack(record, [new PackageFile { Path = "src/Shared.cs", Content = "// admitted code" }], _origin);
    }

    [Fact]
    public void An_explicit_directory_wins_resolution()
    {
        var dir = Path.Combine(_dir, "store");
        MeshStore.Resolve(dir).Should().Be(Path.GetFullPath(dir));
    }

    [Fact]
    public void Without_an_explicit_directory_the_store_lives_under_the_user_profile()
    {
        // The env override (ASHLAR_MESH_DIR) is production surface exercised by e2e-loop.sh;
        // unit tests never mutate process env (xunit parallelism), so assert the default only
        // when the variable is genuinely absent.
        if (Environment.GetEnvironmentVariable("ASHLAR_MESH_DIR") is { Length: > 0 })
        {
            return;
        }
        MeshStore.Resolve(null).Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ashlar", "mesh", "published"));
    }

    [Fact]
    public async Task Publish_places_a_verifying_package_named_by_id_and_content()
    {
        var json = await PackageJsonAsync();
        var storeDir = Path.Combine(_dir, "published");

        var dest = MeshStore.Publish(storeDir, json);

        Path.GetFileName(dest).Should().StartWith("ext-mesh-").And.EndWith(".ashpkg");
        ExtensionPackaging.TryOpen(File.ReadAllText(dest), out _, out var reason).Should().BeTrue(reason);
    }

    [Fact]
    public async Task Republishing_identical_content_dedupes_to_one_file()
    {
        var json = await PackageJsonAsync();
        var storeDir = Path.Combine(_dir, "published");

        var first = MeshStore.Publish(storeDir, json);
        var second = MeshStore.Publish(storeDir, json);

        second.Should().Be(first, "identical bytes hash to the identical name");
        Directory.EnumerateFiles(storeDir).Should().ContainSingle();
    }

    [Fact]
    public async Task Publish_refuses_a_package_that_does_not_verify()
    {
        // Tampering with the payload breaks the seal; the mesh must refuse it at the source
        // rather than propagate it to every peer.
        var json = (await PackageJsonAsync()).Replace("admitted code", "backdoored");
        var storeDir = Path.Combine(_dir, "published");

        var act = () => MeshStore.Publish(storeDir, json);

        act.Should().Throw<InvalidOperationException>().WithMessage("REFUSED*");
        Directory.Exists(storeDir).Should().BeFalse("nothing may touch the store before verification");
    }
}
