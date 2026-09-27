using System.Text.Json.Nodes;
using FluentAssertions;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Packaging;
using Ashlar.Manifest.Signing;
using Xunit;

namespace Ashlar.Tests.Kernel.Packaging;

/// <summary>
/// The certified extension package: a portable admission. The properties under test are the two
/// signatures and what each refuses — the SEAL binds files to the verdict (swap the payload, the
/// seal breaks), and the RECORD's own signature is the verdict's authority (re-seal a doctored
/// record with your own key and the record check still refuses). Verification needs no local
/// keys and no network; opening a package admits nothing.
///
/// <para><b>Hermetic against the machine's key directory.</b> <see cref="GateStore"/>'s constructor
/// pins its signer set from <see cref="OperatorKey.TrustedPublicKeysBase64"/>, which resolves
/// <c>ASHLAR_KEY_DIR</c> / <c>~/.ashlar/keys</c>.
/// <see cref="A_record_signed_before_claims_existed_still_verifies_and_still_packs"/> re-reads
/// through a store constructed with no signer, standing in for a bundle consumer — which by
/// definition holds no key material — so the variable is pointed at a fresh EMPTY directory for the
/// lifetime of the class, set BEFORE the origin key is generated. Pointing it at the origin key's
/// own directory would make that consumer keyed and the fact would stop testing what it names.</para>
/// </summary>
[Collection("EnvironmentSensitive")]
public sealed class ExtensionPackagingTests : IDisposable
{
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    private readonly string _dir;
    private readonly SigningIdentity _origin;
    private readonly string? _previousKeyDir;

    public ExtensionPackagingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashpkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previousKeyDir = Environment.GetEnvironmentVariable(KeyDirVariable);
        var ambient = Path.Combine(_dir, "no-ambient-keys");
        Directory.CreateDirectory(ambient);
        Environment.SetEnvironmentVariable(KeyDirVariable, ambient);
        _origin = OperatorKey.Generate(Path.Combine(_dir, "origin-keys"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyDirVariable, _previousKeyDir);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 24, 6, 0, 0, TimeSpan.Zero);

    private static ExtensionProposal Proposal(string id) => new()
    {
        Id = id,
        Kind = "brick",
        Summary = "add brick shared.classify",
        ProposedBy = "night-agent",
        ProposedAt = Now,
        Courses =
        [
            new CourseResult { Name = "sandbox", Passed = true, Detail = "confined" },
            new CourseResult { Name = "tests", Passed = true, Detail = "14 passed" },
        ],
        ForgeProposalIds = ["forge-1"],
    };

    /// <summary>An Admitted record signed by the origin key — built through the real store so the
    /// signature is exactly what production writes. With <paramref name="claimed"/> files, the
    /// signed proposal carries their content claims (path + sha256), the shape every new producer
    /// records; with none, the proposal is claimless — Files normalizes to null, the pre-claims
    /// shape.</summary>
    private async Task<GateRecord> AdmittedRecordAsync(string id = "ext-share", params (string Path, string Content)[] claimed)
    {
        var proposal = Proposal(id) with
        {
            Files = claimed.Select(f => FileClaim.For(f.Path, f.Content)).ToList(),
        };
        var store = new GateStore(Path.Combine(_dir, ".ashlar-" + id), _origin);
        await store.RecordAsync(proposal, new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);
        return await store.DecideAsync(id, admit: true, "origin-operator", "reviewed", Now.AddMinutes(1));
    }

    private static IReadOnlyList<PackageFile> Files(string content = "// admitted code") =>
        [new PackageFile { Path = "src/Shared.cs", Content = content }];

    // ─────────────────────────── the happy path ───────────────────────────

    [Fact]
    public async Task Pack_then_open_round_trips_and_verifies_with_no_local_keys()
    {
        var record = await AdmittedRecordAsync();
        var json = ExtensionPackaging.Pack(record, Files(), _origin);

        // TryOpen is static and reads no key material from disk or env — intrinsic by construction.
        ExtensionPackaging.TryOpen(json, out var pkg, out var reason).Should().BeTrue(reason);
        pkg!.Record.Proposal.Id.Should().Be("ext-share");
        pkg.Record.State.Should().Be(ProposalState.Admitted);
        pkg.Files.Should().ContainSingle().Which.Content.Should().Be("// admitted code");
        pkg.SealSigner.Should().Be(_origin.PublicKeyBase64);
        pkg.Record.Signer.Should().Be(_origin.PublicKeyBase64);
    }

    // ─────────────────────────── content claims: the signature finally covers the bytes ───────────────────────────

    [Fact]
    public async Task Claimed_content_round_trips_and_the_claims_travel_inside_the_signed_record()
    {
        var record = await AdmittedRecordAsync("ext-claimed", ("src/Shared.cs", "// admitted code"));
        var json = ExtensionPackaging.Pack(record, Files(), _origin);

        ExtensionPackaging.TryOpen(json, out var pkg, out var reason).Should().BeTrue(reason);
        var claim = pkg!.Record.Proposal.Files.Should().ContainSingle().Which;
        claim.Path.Should().Be("src/Shared.cs");
        claim.Matches("// admitted code").Should().BeTrue("the claim rides inside the signed record");
    }

    [Fact]
    public async Task Pack_refuses_files_that_fail_the_records_signed_claims()
    {
        // THE slice-5 review finding. The record's signature covers the claims; the files are
        // re-read from the mutable forge store at pack time. Content edited after admission —
        // same path, different bytes — must not be sealed under the origin's signature.
        var record = await AdmittedRecordAsync("ext-drift", ("src/Shared.cs", "// admitted code"));

        var act = () => ExtensionPackaging.Pack(record, Files("// edited after admission"), _origin);

        act.Should().Throw<InvalidOperationException>().WithMessage("*does not match the signed claim*");
    }

    [Fact]
    public async Task Pack_refuses_when_the_gather_diverges_from_the_signed_claims()
    {
        var record = await AdmittedRecordAsync("ext-cover",
            ("src/Shared.cs", "// admitted code"), ("src/Other.cs", "// also admitted"));

        // A claimed file missing from the gather, or an extra file smuggled in beside the
        // claimed ones, is a count divergence: what travels must be exactly what the gate
        // decided over, nothing more and nothing less.
        var missing = () => ExtensionPackaging.Pack(record, Files(), _origin);
        missing.Should().Throw<InvalidOperationException>().WithMessage("*signed 2 content claim(s) but 1 file(s)*");

        var extra = () => ExtensionPackaging.Pack(record,
            [.. Files(), new PackageFile { Path = "src/Other.cs", Content = "// also admitted" },
             new PackageFile { Path = "src/Smuggled.cs", Content = "// never admitted" }], _origin);
        extra.Should().Throw<InvalidOperationException>().WithMessage("*signed 2 content claim(s) but 3 file(s)*");

        // Same count, wrong path at a position: the writes were replaced or reordered.
        var replaced = () => ExtensionPackaging.Pack(record,
            [.. Files(), new PackageFile { Path = "src/Elsewhere.cs", Content = "// also admitted" }], _origin);
        replaced.Should().Throw<InvalidOperationException>().WithMessage("*replaced or reordered*");
    }

    [Fact]
    public async Task Order_is_signed_two_admitted_writes_to_one_path_cannot_be_swapped()
    {
        // Apply is last-write-wins per path, so ORDER decides the final bytes. An order-blind
        // (multiset) check would verify a package whose two same-path files were swapped — every
        // hash still matches, but the receiver would end with the wrong final content. The
        // sequence-exact check refuses the swap.
        var record = await AdmittedRecordAsync("ext-order",
            ("src/Shared.cs", "// first write"), ("src/Shared.cs", "// final write"));

        var straight = ExtensionPackaging.Pack(record,
            [new PackageFile { Path = "src/Shared.cs", Content = "// first write" },
             new PackageFile { Path = "src/Shared.cs", Content = "// final write" }], _origin);
        ExtensionPackaging.TryOpen(straight, out _, out var openReason).Should().BeTrue(openReason);

        var swapped = () => ExtensionPackaging.Pack(record,
            [new PackageFile { Path = "src/Shared.cs", Content = "// final write" },
             new PackageFile { Path = "src/Shared.cs", Content = "// first write" }], _origin);
        swapped.Should().Throw<InvalidOperationException>().WithMessage("*does not match the signed claim*");
    }

    [Fact]
    public async Task Open_refuses_a_package_whose_files_fail_the_records_own_claims()
    {
        // Defense in depth for a BYPASSED packer: the origin operator's own key seals swapped
        // bytes under an untouched claims-bearing record. Seal verifies (their bytes), record
        // verifies (untouched), sealer == signer (same operator) — the claims must still refuse:
        // the sealed bytes are not the bytes the gate decided over.
        var record = await AdmittedRecordAsync("ext-bypass", ("src/Shared.cs", "// admitted code"));
        var unsealed = new ExtensionPackage
        {
            FormatVersion = ExtensionPackage.ExpectedFormatVersion,
            Record = record,
            Files = [new PackageFile { Path = "src/Shared.cs", Content = "// swapped at the origin" }],
        };
        var sealed_ = unsealed with { Seal = _origin.Sign(CanonicalJson.Bytes(unsealed)), SealSigner = _origin.PublicKeyBase64 };
        var json = System.Text.Json.JsonSerializer.Serialize(sealed_, new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });

        ExtensionPackaging.TryOpen(json, out _, out var reason).Should().BeFalse(
            "a receiver must not trust bytes the admission's own claims disown");
        reason.Should().Contain("does not match the signed claim");
    }

    [Fact]
    public async Task A_record_signed_before_claims_existed_still_verifies_and_still_packs()
    {
        // Wire-format compatibility, proven the honest way: persist a signed record, then strip
        // the Files property from the stored JSON — byte-for-byte what a pre-claims binary wrote.
        // The fail-closed reader must still verify it, and packaging must still accept it (its
        // rows simply travel unclaimed, exactly as they did before claims existed).
        var storeDir = Path.Combine(_dir, ".ashlar-legacy");
        var store = new GateStore(storeDir, _origin);
        await store.RecordAsync(Proposal("ext-legacy"), new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);
        await store.DecideAsync("ext-legacy", admit: true, "origin-operator", "reviewed", Now.AddMinutes(1));

        var path = Path.Combine(storeDir, "gates", "ext-legacy.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        ((JsonObject)node["Proposal"]!).Remove("Files").Should().BeTrue("the stored record carries the property to strip");
        File.WriteAllText(path, node.ToJsonString());

        var reread = await new GateStore(storeDir).GetAsync("ext-legacy");
        reread.Should().NotBeNull("a record signed without claims must keep verifying");
        reread!.Proposal.Files.Should().BeNull();

        var json = ExtensionPackaging.Pack(reread, Files(), _origin);
        ExtensionPackaging.TryOpen(json, out _, out var reason).Should().BeTrue(reason);
    }

    [Fact]
    public void Null_claims_stay_out_of_the_canonical_bytes_and_empty_normalizes_to_null()
    {
        // The compatibility mechanism itself (SPEC-006 S-5). Null must vanish from the signed
        // bytes — that is what keeps every pre-claims signature verifying — and the empty
        // spelling, which WOULD enter the canonical form and diverge, is unrepresentable: the
        // type normalizes it to null on construction and deserialization alike, so no producer
        // can ever sign it.
        var claimless = Proposal("ext-canon");
        System.Text.Encoding.UTF8.GetString(CanonicalJson.Bytes(claimless))
            .Should().NotContain("\"Files\"", "a null claim list must serialize exactly like a pre-claims record");

        var normalized = claimless with { Files = [] };
        normalized.Files.Should().BeNull("empty is not a spelling of 'no claims' — the type normalizes it away");
        System.Text.Encoding.UTF8.GetString(CanonicalJson.Bytes(normalized))
            .Should().NotContain("\"Files\"", "the normalized value must sign identically to the pre-claims shape");
    }

    // ─────────────────────────── pack refusals ───────────────────────────

    [Fact]
    public async Task Pack_refuses_a_record_that_is_not_admitted()
    {
        var store = new GateStore(Path.Combine(_dir, ".ashlar-held"), _origin);
        var held = await store.RecordAsync(Proposal("ext-held"), new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);

        var act = () => ExtensionPackaging.Pack(held, Files(), _origin);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ADMITTED*Held*");
    }

    [Fact]
    public async Task Pack_refuses_a_sealer_that_is_not_the_admissions_signer()
    {
        // TryOpen refuses SealSigner != Record.Signer, so packing with a different key would
        // mint a package no receiver can ever open — refused at the source instead.
        var record = await AdmittedRecordAsync("ext-otherkey");
        var otherKey = OperatorKey.Generate(Path.Combine(_dir, "other-keys"));

        var act = () => ExtensionPackaging.Pack(record, Files(), otherKey);

        act.Should().Throw<InvalidOperationException>().WithMessage("REFUSED*does not match*");
    }

    [Fact]
    public async Task Pack_refuses_an_unsigned_record()
    {
        // An unsigned admission proves nothing to a receiver — it must not travel.
        var store = new GateStore(Path.Combine(_dir, ".ashlar-unsigned"));   // no signer
        await store.RecordAsync(Proposal("ext-plain"), new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);
        var unsigned = await store.DecideAsync("ext-plain", admit: true, "op", "ok", Now.AddMinutes(1));

        var act = () => ExtensionPackaging.Pack(unsigned, Files(), _origin);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsigned*proves nothing*");
    }

    [Fact]
    public async Task Pack_refuses_escaping_or_rooted_paths()
    {
        var record = await AdmittedRecordAsync("ext-paths");
        foreach (var bad in new[] { "../outside.cs", "src/../../evil.cs", "/etc/passwd", "C:\\windows\\evil", "src\\..\\..\\evil", "a::stream" })
        {
            var act = () => ExtensionPackaging.Pack(record, [new PackageFile { Path = bad, Content = "x" }], _origin);
            act.Should().Throw<InvalidOperationException>($"path '{bad}' must be refused").WithMessage("*Illegal package path*");
        }
    }

    [Fact]
    public async Task Pack_refuses_an_empty_file_list()
    {
        var record = await AdmittedRecordAsync("ext-empty");
        var act = () => ExtensionPackaging.Pack(record, [], _origin);
        act.Should().Throw<InvalidOperationException>().WithMessage("*no files*");
    }

    // ─────────────────────────── open refusals: the attacks ───────────────────────────

    [Fact]
    public async Task Open_refuses_a_tampered_payload()
    {
        var record = await AdmittedRecordAsync("ext-tamper");
        var json = ExtensionPackaging.Pack(record, Files("// original"), _origin);

        var doctored = json.Replace("// original", "// backdoored");

        ExtensionPackaging.TryOpen(doctored, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("seal does not verify");
    }

    [Fact]
    public async Task Open_refuses_a_transplanted_verdict_re_sealed_by_an_attacker()
    {
        // THE key attack. An attacker takes a genuine package, doctors the RECORD (say, flips a
        // failing course to passing, or the summary to their code), and re-seals the whole thing
        // with their own key. Check 1 (the seal) now PASSES — the attacker sealed exactly these
        // bytes. Check 2 must still refuse: the record's embedded signature no longer covers the
        // doctored verdict, and the sealer's key is not the verdict's authority.
        var record = await AdmittedRecordAsync("ext-forge");
        var json = ExtensionPackaging.Pack(record, Files(), _origin);
        var attacker = OperatorKey.Generate(Path.Combine(_dir, "attacker-keys"));

        var node = JsonNode.Parse(json)!;
        node["Record"]!["Proposal"]!["Summary"] = "add brick attacker.payload";  // doctor the verdict's content
        var doctoredUnsealed = System.Text.Json.JsonSerializer.Deserialize<ExtensionPackage>(
            node.ToJsonString(), new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            })! with
        { Seal = null, SealSigner = null };
        var reSealed = doctoredUnsealed with
        {
            Seal = attacker.Sign(CanonicalJson.Bytes(doctoredUnsealed)),
            SealSigner = attacker.PublicKeyBase64,
        };
        var reSealedJson = System.Text.Json.JsonSerializer.Serialize(reSealed, new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });

        ExtensionPackaging.TryOpen(reSealedJson, out _, out var reason).Should().BeFalse(
            "a valid seal from the wrong authority must not carry a doctored verdict");
        reason.Should().Contain("gate record's signature does not verify");
    }

    [Fact]
    public async Task Open_refuses_files_swapped_under_a_pristine_record_and_re_sealed_by_an_attacker()
    {
        // THE critical attack the review caught. The record signature does NOT cover file bytes —
        // only the seal does. So an attacker keeps a GENUINE origin-signed Admitted record exactly
        // as-is, swaps the Files for their own code, and mints a fresh seal with their OWN key.
        // Check 1 (seal) passes: they sealed exactly these bytes. Check 2 (record sig) passes: the
        // record is untouched. The bind — sealer must be the verdict's signer — is what refuses.
        var record = await AdmittedRecordAsync("ext-swap");
        var json = ExtensionPackaging.Pack(record, Files("// innocent"), _origin);
        var attacker = OperatorKey.Generate(Path.Combine(_dir, "swap-attacker"));

        var jsonOpts = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var genuine = System.Text.Json.JsonSerializer.Deserialize<ExtensionPackage>(json, jsonOpts)!;
        var swapped = genuine with
        {
            Files = [new PackageFile { Path = "src/Backdoor.cs", Content = "// malware" }],
            Seal = null,
            SealSigner = null,
        };
        var reSealed = swapped with
        {
            Seal = attacker.Sign(CanonicalJson.Bytes(swapped)),
            SealSigner = attacker.PublicKeyBase64,
        };
        var attackJson = System.Text.Json.JsonSerializer.Serialize(reSealed, jsonOpts);

        // Sanity: the record is still the genuine origin's, untouched.
        reSealed.Record.Signer.Should().Be(_origin.PublicKeyBase64);
        reSealed.Record.Sig.Should().Be(genuine.Record.Sig);

        ExtensionPackaging.TryOpen(attackJson, out _, out var reason).Should().BeFalse(
            "a genuine verdict must not vouch for files a different key sealed");
        reason.Should().Contain("sealed by a different key");
    }

    [Fact]
    public async Task Open_refuses_a_stripped_seal()
    {
        var record = await AdmittedRecordAsync("ext-strip");
        var json = ExtensionPackaging.Pack(record, Files(), _origin);
        var node = JsonNode.Parse(json)!;
        node["Seal"] = null;
        node["SealSigner"] = null;

        ExtensionPackaging.TryOpen(node.ToJsonString(), out _, out var reason).Should().BeFalse();
        reason.Should().Contain("no seal");
    }

    [Fact]
    public async Task Open_refuses_a_non_admitted_verdict_even_when_correctly_sealed()
    {
        // A held verdict sealed by its own origin is internally consistent — and still refused:
        // only admissions travel. (Built by hand because Pack correctly refuses to build it.)
        var store = new GateStore(Path.Combine(_dir, ".ashlar-heldseal"), _origin);
        var held = await store.RecordAsync(Proposal("ext-heldpkg"), new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);
        var unsealed = new ExtensionPackage
        {
            FormatVersion = ExtensionPackage.ExpectedFormatVersion,
            Record = held,
            Files = Files(),
        };
        var sealed_ = unsealed with { Seal = _origin.Sign(CanonicalJson.Bytes(unsealed)), SealSigner = _origin.PublicKeyBase64 };
        var json = System.Text.Json.JsonSerializer.Serialize(sealed_, new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });

        ExtensionPackaging.TryOpen(json, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("not Admitted");
    }

    [Fact]
    public void Open_refuses_garbage_and_wrong_versions()
    {
        ExtensionPackaging.TryOpen("not json at all", out _, out var r1).Should().BeFalse();
        r1.Should().Contain("not valid ashpkg JSON");

        ExtensionPackaging.TryOpen("", out _, out var r2).Should().BeFalse();
        r2.Should().Contain("empty");

        ExtensionPackaging.TryOpen("""{"FormatVersion":"ashpkg/v99"}""", out _, out var r3).Should().BeFalse();
        // An unknown version may fail on shape before version — either way it is refused loudly.
    }

    [Fact]
    public async Task Open_refuses_an_escaping_path_in_a_hand_crafted_package()
    {
        // Pack refuses to create one; an attacker hand-crafts it and seals it themselves. The
        // receiving side must independently refuse the path — safety is checked at BOTH ends.
        var record = await AdmittedRecordAsync("ext-crafted");
        var unsealed = new ExtensionPackage
        {
            FormatVersion = ExtensionPackage.ExpectedFormatVersion,
            Record = record,
            Files = [new PackageFile { Path = "../../outside.cs", Content = "// escape" }],
        };
        var sealed_ = unsealed with { Seal = _origin.Sign(CanonicalJson.Bytes(unsealed)), SealSigner = _origin.PublicKeyBase64 };
        var json = System.Text.Json.JsonSerializer.Serialize(sealed_, new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });

        ExtensionPackaging.TryOpen(json, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("not a safe project-relative path");
    }

    // ─────────────────────────── path shape ───────────────────────────

    [Theory]
    [InlineData("src/Ok.cs", true)]
    [InlineData("docs\\note.md", true)]
    [InlineData("a/b/c.txt", true)]
    [InlineData("../x", false)]
    [InlineData("a/../../x", false)]
    [InlineData("/rooted", false)]
    [InlineData("\\rooted", false)]
    [InlineData("C:/win", false)]
    [InlineData("a::ads", false)]
    [InlineData("", false)]
    [InlineData("a//b", false)]
    [InlineData(".", false)]
    [InlineData("con", false)]
    [InlineData("src/nul.cs", false)]
    [InlineData("aux", false)]
    [InlineData("COM1", false)]
    [InlineData("src/lpt9.txt", false)]
    [InlineData("trailing.", false)]
    [InlineData("trailingspace ", false)]
    [InlineData("console.cs", true)]
    public void Path_safety_is_an_allowlist(string path, bool ok) =>
        ExtensionPackaging.IsSafeRelativePath(path).Should().Be(ok, $"'{path}'");

    [Fact]
    public void An_oversized_document_is_refused_before_the_deserializer_touches_it()
    {
        // An attacker-controlled peer file must REFUSE, not crash the verifier: an unbounded
        // JSON document handed straight to the reader is an OOM on the fail-closed path. The
        // cap sits above the deserializer so a giant document never reaches it.
        var giant = "{\"FormatVersion\":\"ashpkg/v1\",\"pad\":\""
                    + new string('x', ExtensionPackage.MaxPackageChars + 1) + "\"}";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ExtensionPackaging.TryOpen(giant, out var pkg, out var reason).Should().BeFalse();
        sw.Stop();

        pkg.Should().BeNull();
        reason.Should().Contain("limit");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the rejection is a length check, not a parse of the whole document");
    }
}
