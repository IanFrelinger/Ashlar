using System.Text.Json;
using System.Text.Json.Serialization;
using Ashlar.Abstractions.Security;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;

namespace Ashlar.Manifest.Packaging;

/// <summary>One file carried by a package: a project-relative path and its full content.</summary>
public sealed record PackageFile
{
    /// <summary>Project-relative path, forward or back slashes; never rooted, never escaping.</summary>
    public required string Path { get; init; }

    /// <summary>The complete new file content.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// A certified extension package (<c>.ashpkg</c>): an admitted extension, its evidence, and the
/// signatures that make both portable.
/// </summary>
public sealed record ExtensionPackage
{
    /// <summary>The only accepted format version.</summary>
    public const string ExpectedFormatVersion = "ashpkg/v1";

    /// <summary>Hard ceiling on the JSON document size accepted by ExtensionPackaging.TryOpen, in
    /// characters. Well above the 4 MB mesh wire limit (base64 file bodies inflate the JSON),
    /// far below anything that threatens the verifier. A package over this is refused before
    /// deserialization rather than crashing the fail-closed path.</summary>
    public const int MaxPackageChars = 16 * 1024 * 1024;

    /// <summary>Format tag, <c>ashpkg/v1</c>.</summary>
    public required string FormatVersion { get; init; }

    /// <summary>The origin gate record: the proposal (with its course evidence) plus the SIGNED
    /// Admitted verdict. Its own Ed25519 signature travels inside it and verifies intrinsically.</summary>
    public required GateRecord Record { get; init; }

    /// <summary>The extension's files — the parked writes the origin gate admitted.</summary>
    public required IReadOnlyList<PackageFile> Files { get; init; }

    /// <summary>Base64 Ed25519 seal over the canonical package with the two seal fields null.
    /// The record's signature covers the verdict but not the file bytes; the seal binds the two.
    /// A seal alone is not enough — an attacker can mint a fresh seal over swapped files with
    /// their own key — so the verifier additionally REQUIRES the sealer to be the same operator
    /// whose key signed the admission (<see cref="SealSigner"/> == <c>Record.Signer</c>). Only the
    /// operator who admitted may seal, which is what actually prevents transplanting a genuine
    /// verdict onto foreign code.</summary>
    public string? Seal { get; init; }

    /// <summary>Base64 raw public key of the sealer. Must equal the record's signer.</summary>
    public string? SealSigner { get; init; }

    /// <summary>
    /// The skill's highest level, as canonical label text (SPEC-007 PR 5). Null on a package sealed
    /// before levels existed. The seal covers this field; the signed gate record does not. Composition
    /// treats a missing or unparseable level as <c>SystemHigh</c>.
    /// </summary>
    public string? HighestLevel { get; init; }
}

/// <summary>
/// Packs and opens certified extension packages. The trust story, stated honestly:
///
/// <para>PACK requires an <em>Admitted</em>, <em>signed</em> gate record — an unsigned admission
/// proves nothing to a receiver, so it does not travel (run <c>ashlar keys init</c> at the origin
/// first). The exporter seals record+files together with their own key.</para>
///
/// <para>OPEN verifies fail-closed with NO local keys and NO network — both signatures are
/// checked against the public keys the package itself carries (SPEC-006 intrinsic verification).
/// What that proves: the verdict has not been altered since the key inside it signed it, and the
/// files are the ones the sealer sealed against that verdict. What it deliberately does NOT
/// prove (v1): WHO owns those keys — identity binding is the receiving operator's judgement, by
/// fingerprint, until v2 trust roots. Opening a package admits NOTHING: the receiver's own gate,
/// policy, and budget decide admission, exactly as for a local proposal.</para>
/// </summary>
public static class ExtensionPackaging
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Builds and seals a package. Throws <see cref="InvalidOperationException"/> with a precise
    /// reason when the record or files are not shareable — a package that cannot prove its claim
    /// is refused at the source, not discovered broken at the destination.
    /// </summary>
    public static string Pack(
        GateRecord record,
        IReadOnlyList<PackageFile> files,
        SigningIdentity sealer,
        string? highestLevel = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sealer);

        if (record.State != ProposalState.Admitted)
        {
            throw new InvalidOperationException(
                $"Only an ADMITTED extension can be packaged; '{record.Proposal.Id}' is {record.State}. "
                + "Seat the stone first — a package is a portable admission, and there is no admission to carry.");
        }
        if (record.Sig is null || record.Signer is null)
        {
            throw new InvalidOperationException(
                "The gate record is unsigned, so it proves nothing to a receiver and cannot travel. "
                + "Create an operator key at the origin (ashlar keys init) and decide the proposal signed.");
        }
        if (!string.Equals(sealer.PublicKeyBase64, record.Signer, StringComparison.Ordinal))
        {
            // TryOpen refuses SealSigner != Record.Signer, so a mismatched sealer here would
            // produce a package no receiver can ever open — refuse at the source, and teach.
            throw new InvalidOperationException(
                "REFUSED: the operator key does not match the key that signed this admission — only the "
                + "operator who admitted may seal. Re-admit under the current key, or restore the admitting key.");
        }
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Proposal '{record.Proposal.Id}' carries no files — nothing to package.");
        }
        foreach (var file in files)
        {
            if (!IsSafeRelativePath(file.Path))
            {
                throw new InvalidOperationException(
                    $"Illegal package path '{file.Path}': paths must be project-relative and must not escape the project root.");
            }
        }
        // The record's signature covers the CLAIMS, and the seal binds THESE bytes to the record —
        // sealing content that fails the record's own claims would mint a package every honest
        // receiver refuses. Refuse at the source, whatever path fed the files in.
        if (!VerifyFileClaims(record.Proposal, files, out var claimReason))
        {
            throw new InvalidOperationException(claimReason);
        }
        if (highestLevel is not null && !SecurityLabel.TryParse(highestLevel, out _))
        {
            throw new InvalidOperationException(
                "REFUSED: the skill's highest level '" + highestLevel
                + "' is not a canonical security label. Omit it, or write the label exactly as it prints.");
        }

        var unsealed = new ExtensionPackage
        {
            FormatVersion = ExtensionPackage.ExpectedFormatVersion,
            Record = record,
            Files = files,
            HighestLevel = highestLevel,
        };
        var sealed_ = unsealed with
        {
            Seal = sealer.Sign(CanonicalJson.Bytes(unsealed)),
            SealSigner = sealer.PublicKeyBase64,
        };
        return JsonSerializer.Serialize(sealed_, Json);
    }

    /// <summary>
    /// Parses and VERIFIES a package, fail-closed. True only when the seal covers exactly these
    /// record+files, the record's own signature verifies, and the verdict is Admitted. Requires
    /// no local keys: verification is intrinsic. On false, <paramref name="reason"/> says
    /// precisely what failed — a package that fails any check is treated as forged, not as
    /// slightly damaged.
    /// </summary>
    public static bool TryOpen(string? json, out ExtensionPackage? package, out string reason)
    {
        package = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            reason = "REFUSED: the package is empty.";
            return false;
        }

        // Bound the work before the deserializer touches it. A .ashpkg is a signed unit of
        // certified extension, not a data blob; the mesh serve/pull path already caps files at
        // 4 MB, and an unbounded document handed straight to the JSON reader is an OOM on the
        // verification path (an attacker-controlled peer file must never crash the verifier —
        // it must REFUSE, fail-closed). The ceiling is generous over the 4 MB wire limit to
        // leave headroom for base64-embedded file bodies.
        if (json.Length > ExtensionPackage.MaxPackageChars)
        {
            reason = $"REFUSED: the package is {json.Length:N0} characters; the limit is {ExtensionPackage.MaxPackageChars:N0}. "
                   + "A package this large is not a certified extension — refusing before parsing it.";
            return false;
        }

        ExtensionPackage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ExtensionPackage>(json, Json);
        }
        catch (JsonException ex)
        {
            reason = $"REFUSED: the package is not valid ashpkg JSON ({ex.Message}).";
            return false;
        }
        if (parsed is null)
        {
            reason = "REFUSED: the package contains no document.";
            return false;
        }

        if (!string.Equals(parsed.FormatVersion, ExtensionPackage.ExpectedFormatVersion, StringComparison.Ordinal))
        {
            reason = $"REFUSED: unsupported package format '{parsed.FormatVersion}'; expected '{ExtensionPackage.ExpectedFormatVersion}'.";
            return false;
        }

        // 1. The seal: binds files to the record. Without it, a valid verdict could be
        //    transplanted onto arbitrary code.
        if (parsed.Seal is null || parsed.SealSigner is null)
        {
            reason = "REFUSED: the package carries no seal — record and files are not bound. Refusing an unsealed package.";
            return false;
        }
        var unsealed = parsed with { Seal = null, SealSigner = null };
        if (!OperatorKey.Verify(parsed.SealSigner, CanonicalJson.Bytes(unsealed), parsed.Seal))
        {
            reason = "REFUSED: the seal does not verify — the files or the record were altered after sealing. "
                   + "A package that fails its seal is treated as forged.";
            return false;
        }

        // 2. The record's own signature: the seal proves the sealer sealed THIS content, but the
        //    sealer's key is not the verdict's authority. The admission must verify under the key
        //    embedded in the record itself — an attacker re-sealing a doctored record with their
        //    own key passes check 1 and fails here.
        if (parsed.Record.Sig is null || parsed.Record.Signer is null)
        {
            reason = "REFUSED: the packaged gate record is unsigned — there is no admission to trust.";
            return false;
        }
        var unsignedRecord = parsed.Record with { Sig = null, Signer = null };
        if (!OperatorKey.Verify(parsed.Record.Signer, CanonicalJson.Bytes(unsignedRecord), parsed.Record.Sig))
        {
            reason = "REFUSED: the gate record's signature does not verify — the verdict was altered. "
                   + "A forged admission is worse than a missing one.";
            return false;
        }

        // 3. Bind the two: the SEALER must be the operator who ADMITTED. The record signature does
        //    not cover the file bytes — only the seal does — so a seal by any other key would let
        //    an attacker keep a genuine, untouched verdict and re-seal it over their OWN files with
        //    their OWN key (both checks above pass). Requiring seal-signer == record-signer closes
        //    that: only the admitting operator's key can vouch for the payload. (This is an
        //    intrinsic equality of two keys already in the package — it needs no trust root.)
        if (!string.Equals(parsed.SealSigner, parsed.Record.Signer, StringComparison.Ordinal))
        {
            reason = "REFUSED: the package was sealed by a different key than the one that signed the admission. "
                   + "Only the operator who admitted an extension may seal it — a seal from another key over a "
                   + "genuine verdict is exactly how foreign code is smuggled under a real admission.";
            return false;
        }

        // 3. The verdict itself.
        if (parsed.Record.State != ProposalState.Admitted)
        {
            reason = $"REFUSED: the packaged verdict is {parsed.Record.State}, not Admitted — only admitted extensions travel.";
            return false;
        }

        // 4. Payload shape: files exist and stay inside a project root.
        if (parsed.Files.Count == 0)
        {
            reason = "REFUSED: the package carries no files.";
            return false;
        }
        foreach (var file in parsed.Files)
        {
            if (!IsSafeRelativePath(file.Path))
            {
                reason = $"REFUSED: package path '{file.Path}' is not a safe project-relative path.";
                return false;
            }
        }

        // 5. The record's own content claims. The seal proves the sealer sealed THESE bytes and
        //    check 3 proves the sealer had admission authority — but neither proves the bytes are
        //    the ones the GATE decided over. When the signed proposal carries claims, the files
        //    must hash to them: an origin whose forge rows were edited between admission and
        //    packaging is refused here even if its packer was bypassed. (Absent claims are a
        //    pre-claims record — nothing was claimed, and the signature guarantees claims were
        //    never stripped to get here.)
        if (!VerifyFileClaims(parsed.Record.Proposal, parsed.Files, out var claimReason))
        {
            reason = claimReason;
            return false;
        }

        package = parsed;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Verifies file content against the proposal's signed claims, SEQUENCE-exact: file i must
    /// match claim i on both path and content hash. Order is part of what the gate decided —
    /// claims are recorded in forge-id order and every consumer gathers, seals, parks, and
    /// applies in that same order with last-write-wins per path, so an order-blind check would
    /// let two admitted writes to one path be swapped, flipping the final applied content while
    /// every hash still matches. A null claim list is a record from before claims existed —
    /// nothing was claimed, so nothing verifies and the result is true; the field sits under
    /// the record's signature, so that path is unreachable by stripping claims off a
    /// claims-bearing record (SPEC-006 S-5). On false, <paramref name="reason"/> names the
    /// offense precisely.
    /// </summary>
    public static bool VerifyFileClaims(ExtensionProposal proposal, IReadOnlyList<PackageFile> files, out string reason)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(files);

        reason = string.Empty;
        if (proposal.Files is not { } claims)
        {
            return true;
        }

        if (claims.Count != files.Count)
        {
            reason = $"REFUSED: the admission signed {claims.Count} content claim(s) but {files.Count} file(s) were gathered — "
                   + "what travels must be exactly what the gate decided over, nothing more and nothing less.";
            return false;
        }
        for (var i = 0; i < claims.Count; i++)
        {
            if (!string.Equals(claims[i].Path, files[i].Path, StringComparison.Ordinal))
            {
                reason = $"REFUSED: file {i + 1} of {files.Count} is '{files[i].Path}' where the signed claim names "
                       + $"'{claims[i].Path}' — the admitted writes were replaced or reordered.";
                return false;
            }
            if (!claims[i].Matches(files[i].Content))
            {
                reason = $"REFUSED: content of '{files[i].Path}' does not match the signed claim — "
                       + "the bytes are not the ones the gate admitted.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A path is safe when it is relative, contains no <c>..</c> segment under either separator,
    /// names no NTFS alternate stream, and is not rooted on any OS's spelling (a leading slash or
    /// a drive letter). Checked at BOTH ends — pack refuses to create an escaping package, and
    /// open refuses to accept one, so a hand-crafted package gains nothing.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (path.Contains(':', StringComparison.Ordinal))
        {
            return false;   // drive letters and NTFS alternate data streams
        }
        if (path[0] is '/' or '\\')
        {
            return false;   // rooted on any OS's spelling
        }
        var segments = path.Split('/', '\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
            // A trailing dot/space is stripped by Win32 (so 'x.' and 'x' collide), and a reserved
            // device name (con, nul, aux, com1…) is not a real file on Windows even inside a
            // subdirectory. Deny both on EVERY OS so a package that is legal at the origin cannot
            // become an unwritable or aliased path the moment it is applied on Windows.
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return false;
            }
            var stem = segment.Split('.')[0];
            if (Win32Reserved.Contains(stem))
            {
                return false;
            }
        }
        return true;
    }

    private static readonly HashSet<string> Win32Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
}
