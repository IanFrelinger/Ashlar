using System.Text.Json;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The determinism leg compares two serialized brick outputs for equality. This pins the guard
/// that stops that comparison from agreeing about nothing.
/// </summary>
/// <remarks>
/// <para><b>Why an equality check needs a shape guard.</b> <c>BrickOutput</c> holds arbitrary
/// <c>object</c> values set by brick code, so serializing it needs reflection and no source
/// generator can cover it. Reflection-based serialization does not fail loudly under a trimmed or
/// ahead-of-time publish — the payload can come back empty or short a property with no exception
/// (#584 pinned exactly that for the canonical signing payload, in the same serializer). Two
/// <em>different</em> outputs that both degrade to <c>{}</c> then compare equal, the determinism
/// leg reports deterministic, and the certificate records a check that never ran.</para>
///
/// <para><b>Why the guard is tested directly.</b> A test host cannot disable reflection-based
/// serialization, so the degenerate payloads below are handed to the guard rather than produced
/// by it. That is the same shape <c>CanonicalPayloadGuardTests</c> uses for the record payload,
/// and for the same reason — the publish modes that produce them are not reproducible in-process.
/// The end-to-end half is the trim/AOT lane in <c>runtime-portability-gate.yml</c>.</para>
///
/// <para>The guard counts top-level properties and nothing else. The values are open-world, so
/// nothing about them can be asserted; the <em>number</em> of keys is fixed by the dictionary the
/// serializer was handed, which makes it a value-independent invariant of the payload.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class CanonicalOutputShapeGuardTests
{
    [Fact]
    public void A_well_formed_payload_passes_through_unchanged()
    {
        const string payload = """{"answer":42,"summary":"ok"}""";

        BrickOutputSerializer.EnsureCanonicalShape(payload, 2).Should().BeSameAs(payload);
    }

    /// <summary>The exact degradation #584 observed: the serializer emitted the empty object.</summary>
    [Fact]
    public void An_emptied_payload_is_refused()
    {
        var act = () => BrickOutputSerializer.EnsureCanonicalShape("{}", 2);

        act.Should().Throw<CanonicalOutputException>()
            .WithMessage("*declares 2 properties and serialized 0*");
    }

    /// <summary>
    /// Partial degradation is the dangerous one: it still looks like an output, and an equality
    /// comparison over two payloads that each lost the SAME property agrees.
    /// </summary>
    [Fact]
    public void A_payload_missing_one_property_is_refused()
    {
        var act = () => BrickOutputSerializer.EnsureCanonicalShape("""{"summary":"ok"}""", 2);

        act.Should().Throw<CanonicalOutputException>()
            .WithMessage("*declares 2 properties and serialized 1*");
    }

    [Fact]
    public void A_payload_that_is_not_an_object_is_refused()
    {
        var act = () => BrickOutputSerializer.EnsureCanonicalShape("null", 1);

        act.Should().Throw<CanonicalOutputException>()
            .WithMessage("*not its declared shape*");
    }

    [Fact]
    public void A_payload_that_is_not_json_is_refused_rather_than_thrown_through()
    {
        var act = () => BrickOutputSerializer.EnsureCanonicalShape("not json at all", 1);

        act.Should().Throw<CanonicalOutputException>()
            .WithMessage("*could not be re-read as JSON*");
    }

    /// <summary>
    /// The control. Every other case here is a refusal, so without one payload that is accepted
    /// a guard that refused everything would pass the whole class — and it would also break every
    /// determinism check in the product.
    /// </summary>
    [Fact]
    public void A_real_brick_output_serializes_and_survives_the_guard()
    {
        var output = new BrickOutput { Summary = "ran" };
        output.Set("slug", "hello-world");
        output.Set("length", 11);

        var json = BrickOutputSerializer.ToCanonicalJson(output);

        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Should().HaveCount(3);
        document.RootElement.GetProperty("slug").GetString().Should().Be("hello-world");
    }

    /// <summary>
    /// A brick whose own output key is <c>summary</c> collapses onto the summary slot, so the
    /// expected count has to come from the composed payload rather than from the output's
    /// dictionary. Pinned because getting it from the dictionary would refuse a legitimate output.
    /// </summary>
    [Fact]
    public void An_output_key_named_summary_does_not_trip_the_guard()
    {
        var output = new BrickOutput { Summary = "from the property" };
        output.Set("summary", "from the dictionary");

        var act = () => BrickOutputSerializer.ToCanonicalJson(output);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Two different outputs must not compare equal. This is the property the determinism leg
    /// actually relies on, asserted here rather than assumed.
    /// </summary>
    [Fact]
    public void Two_different_outputs_do_not_serialize_to_the_same_bytes()
    {
        var first = new BrickOutput { Summary = "ran" };
        first.Set("slug", "a");
        var second = new BrickOutput { Summary = "ran" };
        second.Set("slug", "b");

        BrickOutputSerializer.ToCanonicalJson(first)
            .Should().NotBe(BrickOutputSerializer.ToCanonicalJson(second));
    }

    /// <summary>
    /// A witness the serializer refuses must stop the mint, not quietly drop the witness input.
    /// </summary>
    /// <remarks>
    /// <para>The gate caught <see cref="NotSupportedException"/> around
    /// <c>JsonSerializer.Serialize(request.Witness, ...)</c>, logged a warning, and returned only
    /// the additional inputs — minting a certificate with <b>no witness input at all</b>. The
    /// witness input is the record of what was judged, and neither <c>Default</c> nor
    /// <c>Strict</c> requires it (they require <c>gate-emitted-artifact</c> and
    /// <c>certifier-identity</c>), so such a record verifies cleanly downstream and no consumer
    /// can tell it binds nothing about the specification.</para>
    ///
    /// <para>The trigger is not exotic: that exception is what the serializer raises when
    /// reflection-based serialization is disabled, which is every trimmed and ahead-of-time
    /// publish. A delegate reproduces it in-process, because <c>WitnessCase.Input</c> is an
    /// open <c>IReadOnlyDictionary&lt;string, object&gt;</c>.</para>
    /// </remarks>
    [Fact]
    public async Task A_witness_that_cannot_be_hashed_refuses_the_mint_instead_of_dropping_the_input()
    {
        var gate = new CertificationGate(new CertificationRecordSigner());
        var unserializable = new WitnessSpec(
            "unserializable-witness-brick",
            [
                new WitnessCase(
                    new Dictionary<string, object> { ["callback"] = new Action(() => { }) },
                    new Dictionary<string, object> { ["ok"] = true })
            ]);

        var projectPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        try
        {
            var act = async () => await gate.CertifyAsync(new CertificationRequest
            {
                Brick = new MutationProbeBrick(),
                Witness = unserializable,
                SourceCode = "public sealed class Probe { }",
                ProjectPath = projectPath,
            });

            var thrown = await act.Should().ThrowAsync<CanonicalOutputException>();
            thrown.WithMessage("*cannot record what it judged*");
            thrown.And.InnerException.Should().BeOfType<NotSupportedException>(
                "the refusal has to carry the serializer failure that caused it, or an operator "
                + "cannot tell a publish-mode problem from a malformed witness");
        }
        finally
        {
            File.Delete(projectPath);
        }
    }
}
