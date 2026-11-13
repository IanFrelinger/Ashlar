using System.Text;
using Ashlar.Manifest.Signing;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Kernel.Signing;

/// <summary>
/// Public identity lookup uses disposable, explicit key directories. These are application
/// metadata tests, not private-key custody, process separation, or OS isolation evidence.
/// </summary>
public sealed class OperatorPublicIdentityTests : IDisposable
{
    private readonly string keyDir = Path.Combine(Path.GetTempPath(), "ashlar-public-identity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(keyDir)) Directory.Delete(keyDir, recursive: true);
    }

    [Fact]
    public void Missing_directory_returns_absent_without_creating_any_files()
    {
        OperatorKey.TryReadPublicIdentity(keyDir).Should().BeNull();
        Directory.Exists(keyDir).Should().BeFalse();
    }

    [Fact]
    public void Empty_directory_returns_absent_without_creating_any_files()
    {
        Directory.CreateDirectory(keyDir);

        OperatorKey.TryReadPublicIdentity(keyDir).Should().BeNull();

        Directory.GetFileSystemEntries(keyDir).Should().BeEmpty();
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("absent")]
    [InlineData("malformed")]
    [InlineData("mismatched")]
    public void Public_identity_does_not_depend_on_private_material(string privateState)
    {
        var generated = OperatorKey.Generate(keyDir);
        var privatePath = Path.Combine(keyDir, "operator.key");
        switch (privateState)
        {
            case "absent":
                File.Delete(privatePath);
                break;
            case "malformed":
                File.WriteAllText(privatePath, "not-a-private-key!!!");
                break;
            case "mismatched":
                var otherDir = Path.Combine(keyDir, "other");
                var other = OperatorKey.Generate(otherDir);
                other.PublicKeyBase64.Should().NotBe(generated.PublicKeyBase64);
                File.Copy(Path.Combine(otherDir, "operator.key"), privatePath, overwrite: true);
                break;
        }
        var before = Snapshot();

        var identity = OperatorKey.TryReadPublicIdentity(keyDir);

        identity.Should().NotBeNull();
        identity!.PublicKeyBase64.Should().Be(generated.PublicKeyBase64);
        identity.Fingerprint.Should().Be(generated.Fingerprint);
        Snapshot().Should().BeEquivalentTo(before, options => options.WithStrictOrdering());

        // Public metadata must not relax the separate signing loader's existing contract.
        if (privateState is "malformed" or "mismatched")
        {
            var load = () => OperatorKey.TryLoad(keyDir);
            load.Should().Throw<InvalidOperationException>();
        }
        else if (privateState == "absent")
        {
            OperatorKey.TryLoad(keyDir).Should().BeNull();
        }
        else
        {
            OperatorKey.TryLoad(keyDir)!.Fingerprint.Should().Be(generated.Fingerprint);
        }
    }

    [Theory]
    [InlineData("not-base64!!!")]
    [InlineData("AAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    public void Malformed_public_material_is_an_error_not_absence_or_a_trusted_key_fallback(string malformed)
    {
        OperatorKey.Generate(keyDir);
        OperatorKey.Generate(keyDir, rotate: true);
        File.WriteAllText(Path.Combine(keyDir, "operator.pub"), malformed);
        var before = Snapshot();

        var read = () => OperatorKey.TryReadPublicIdentity(keyDir);

        read.Should().Throw<InvalidOperationException>().WithMessage("*operator.pub*readable Ed25519 public key*");
        Snapshot().Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Missing_public_file_does_not_fall_back_to_private_retained_or_peer_material()
    {
        var previous = OperatorKey.Generate(keyDir);
        OperatorKey.Generate(keyDir, rotate: true);
        var peers = Path.Combine(keyDir, "peers");
        Directory.CreateDirectory(peers);
        File.WriteAllText(Path.Combine(peers, previous.Fingerprint.Replace(':', '-')), previous.PublicKeyBase64);
        File.Delete(Path.Combine(keyDir, "operator.pub"));
        var before = Snapshot();

        OperatorKey.TryReadPublicIdentity(keyDir).Should().BeNull();

        Snapshot().Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Public_metadata_does_not_parse_corrupt_private_trust_or_peer_files()
    {
        var generated = OperatorKey.Generate(keyDir);
        File.WriteAllText(Path.Combine(keyDir, "operator.key"), "not-base64!!!");
        Directory.CreateDirectory(Path.Combine(keyDir, "trusted"));
        File.WriteAllText(Path.Combine(keyDir, "trusted", "old.pub"), "not-base64!!!");
        Directory.CreateDirectory(Path.Combine(keyDir, "peers"));
        File.WriteAllText(Path.Combine(keyDir, "peers", "invalid"), "untrusted text");
        var before = Snapshot();

        var identity = OperatorKey.TryReadPublicIdentity(keyDir);

        identity!.PublicKeyBase64.Should().Be(generated.PublicKeyBase64);
        identity.Fingerprint.Should().Be(generated.Fingerprint);
        Snapshot().Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        var trust = () => OperatorKey.TrustedPublicKeysBase64(keyDir);
        trust.Should().Throw<InvalidOperationException>("trust verification must still reject corrupt retained material");
    }

    [Fact]
    public void Reading_trimmed_metadata_preserves_raw_public_file_and_all_other_bytes()
    {
        var generated = OperatorKey.Generate(keyDir);
        File.WriteAllText(Path.Combine(keyDir, "operator.pub"), " \r\n" + generated.PublicKeyBase64 + "\r\n ");
        var before = Snapshot();

        var identity = OperatorKey.TryReadPublicIdentity(keyDir);

        identity!.PublicKeyBase64.Should().Be(generated.PublicKeyBase64);
        identity.Fingerprint.Should().Be(generated.Fingerprint);
        Snapshot().Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Existing_public_path_read_failure_is_not_reported_as_absence()
    {
        OperatorKey.Generate(keyDir);
        var publicPath = Path.Combine(keyDir, "operator.pub");
        File.Delete(publicPath);
        Directory.CreateDirectory(publicPath);
        var before = Snapshot();

        var read = () => OperatorKey.TryReadPublicIdentity(keyDir);

        read.Should().Throw<Exception>().Where(ex => ex is IOException || ex is UnauthorizedAccessException);
        Directory.Exists(publicPath).Should().BeTrue();
        Snapshot().Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Public_identity_tracks_current_key_without_changing_retained_trust_or_signatures()
    {
        var previous = OperatorKey.Generate(keyDir);
        var message = Encoding.UTF8.GetBytes("historical signed record");
        var signature = previous.Sign(message);
        var current = OperatorKey.Generate(keyDir, rotate: true);
        var before = Snapshot();

        var identity = OperatorKey.TryReadPublicIdentity(keyDir);

        identity!.PublicKeyBase64.Should().Be(current.PublicKeyBase64).And.NotBe(previous.PublicKeyBase64);
        identity.Fingerprint.Should().Be(current.Fingerprint);
        Snapshot().Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        OperatorKey.TrustedPublicKeysBase64(keyDir).Should().Contain(previous.PublicKeyBase64).And.Contain(current.PublicKeyBase64);
        OperatorKey.Verify(previous.PublicKeyBase64, message, signature).Should().BeTrue();
        OperatorKey.TryLoad(keyDir)!.Fingerprint.Should().Be(current.Fingerprint);
    }

    private Dictionary<string, byte[]> Snapshot()
    {
        var snapshot = Directory.GetFiles(keyDir, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(path => "file:" + Path.GetRelativePath(keyDir, path), File.ReadAllBytes, StringComparer.Ordinal);
        foreach (var directory in Directory.GetDirectories(keyDir, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
            snapshot.Add("directory:" + Path.GetRelativePath(keyDir, directory), []);
        return snapshot;
    }
}
