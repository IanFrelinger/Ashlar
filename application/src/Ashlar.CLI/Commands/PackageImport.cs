using System.Security.Cryptography;
using System.Text.Json;
using Ashlar.CLI.Packaging;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Packaging;
using Ashlar.Manifest.Signing;

namespace Ashlar.CLI.Commands;

/// <summary>How a submitted package fared at the receiving project's gate.</summary>
public enum PackageAdmission
{
    /// <summary>The package failed intrinsic verification, or the project's policy would not load —
    /// nothing was proposed.</summary>
    Refused,

    /// <summary>Verified, admitted, and applied to the project tree.</summary>
    Admitted,

    /// <summary>Verified and held for a human decision — nothing on disk.</summary>
    Held,

    /// <summary>Verified but rejected by the gate (e.g. sealed, over budget, outside the
    /// envelope) — nothing on disk.</summary>
    Rejected,

    /// <summary>The same verified package seal and signer are already recorded at this gate —
    /// a re-pull of something already decided here. Nothing is parked or changed (append-once history is not re-opened).</summary>
    AlreadyImported,

    /// <summary>The receiving node failed while processing the package. This is not a policy verdict.</summary>
    Failed,
}

/// <summary>The outcome of submitting one package, transport-agnostic. <see cref="Warning"/> is
/// set when the admission was recorded but the apply did not fully complete — a durable,
/// operator-visible fact that a green "admitted" line must not hide.</summary>
public sealed record PackageImportResult(
    PackageAdmission Outcome,
    string Message,
    ExtensionPackage? Package,
    string? LocalProposalId,
    IReadOnlyList<string> AppliedPaths,
    string? Warning = null);

/// <summary>
/// The receiver-sovereign import: verify a certified package INTRINSICALLY (no local keys, no
/// network), then submit it to THIS project's gate under THIS project's policy. Shared by
/// <c>ashlar pkg import</c> (one file), <c>ashlar pkg pull</c> (many from a folder), and the daemon's
/// mesh auto-pull (many from peers), because
/// how a package arrived must never change how it is admitted — the transport is not the trust.
///
/// <para>Never throws for an expected outcome: a forged package, a full budget, a sealed project,
/// or an already-imported id all come back as a <see cref="PackageImportResult"/> so a batch
/// puller can report each and continue. Origin certification travels as EVIDENCE; the local gate
/// is the AUTHORITY.</para>
/// </summary>
public static class PackageImport
{
    public static async Task<PackageImportResult> SubmitAsync(string projectDir, string packageJson)
    {
        if (!ExtensionPackaging.TryOpen(packageJson, out var pkg, out var reason))
        {
            return new(PackageAdmission.Refused, reason, null, null, []);
        }

        try { GateStore.ValidateProposalId(pkg!.Record.Proposal.Id); }
        catch (ArgumentException ex) { return new(PackageAdmission.Refused, ex.Message, pkg, null, []); }

        ChangeProposalStore? forge = null;
        var localForgeIds = new List<string>();
        string? recordedId = null;
        ProposalState? recordedState = null;
        try
        {
            var policyPath = Path.Combine(projectDir, "ashlar.policy.yaml");
            if (!File.Exists(policyPath))
            {
                return new(PackageAdmission.Refused, "not an ashlar project (no ashlar.policy.yaml)", pkg, null, []);
            }
            if (!PolicyLoader.TryLoad(await File.ReadAllTextAsync(policyPath), out var policy, out var policyReason))
            {
                return new(PackageAdmission.Refused, policyReason, pkg, null, []);
            }

            // TRUST ROOT (Phase 3): a package sealed by a key this project does not trust never parks
            // anything — the refusal comes BEFORE the forge queue, so a stranger cannot fill the disk by
            // spamming pulls, and `× REFUSED` names the fingerprint to trust. The check is the sealer's
            // fingerprint against the project policy's trustedSigners UNION the operator's local peers
            // keychain (`ashlar keys trust <fp>`). Empty trust set ⇒ every import refused (fail-closed).
            var sealer = Fingerprint(pkg!.SealSigner);
            if (!OperatorKey.IsSignerTrusted(sealer, policy!.SelfExtend.TrustedSigners))
            {
                return new(PackageAdmission.Refused,
                    $"sealed by {sealer}, which is not a trusted signer. Read the fingerprint off the origin "
                    + "box's `ashlar keys show`, then `ashlar keys trust " + sealer + "` here — or list it under "
                    + "selfExtend.trustedSigners in ashlar.policy.yaml.",
                    pkg, null, []);
            }

            forge = AshlarProjectMediation.ProjectStore(projectDir);
            var store = new GateStore(Path.Combine(projectDir, ".ashlar"), OperatorKey.TryLoad());
            // Only a receipt for this exact sealed package proves a re-import. Author-chosen ids
            // alone cannot distinguish independent origins or changed files from the same origin.
            if (await store.GetAsync(pkg!.Record.Proposal.Id) is { } existing)
            {
                var receipt = await ReadReceiptAsync(projectDir, existing.Proposal);
                if (receipt?.Seal == pkg.Seal && receipt?.Signer == pkg.SealSigner)
                {
                    return new(PackageAdmission.AlreadyImported,
                        $"already decided at this gate under id '{pkg.Record.Proposal.Id}'", pkg, pkg.Record.Proposal.Id, []);
                }

                var previousOrigin = receipt is not null
                    ? Fingerprint(receipt.Signer) : "unknown (local or legacy record)";
                return new(PackageAdmission.Refused,
                    $"proposal id '{pkg.Record.Proposal.Id}' collides: recorded origin {previousOrigin}; "
                    + $"incoming origin {sealer}. The recorded package is different or cannot be identified; use a new proposal id.",
                    pkg, null, []);
            }

            // Park the files as LOCAL forge proposals — nothing touches the tree until THIS gate
            // admits. Propose → hold → apply holds for remote code exactly as for a local cycle.
            foreach (var pf in pkg.Files)
            {
                var localId = "pkg-" + Guid.NewGuid().ToString("N")[..12];
                // Track before Add: even a store failure after the durable write needs cleanup.
                localForgeIds.Add(localId);
                forge.Add(new ChangeProposal
                {
                    Id = localId,
                    TargetPath = pf.Path.Replace('\\', '/'),
                    NewContent = pf.Content,
                    Summary = $"imported: {pkg.Record.Proposal.Summary}",
                    Reason = $"package sealed by {Fingerprint(pkg.SealSigner)}",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            var proposal = pkg.Record.Proposal with { ForgeProposalIds = localForgeIds };
            var record = await store.ProposeAsync(policy!, proposal, DateTimeOffset.UtcNow);
            recordedId = record.Proposal.Id;
            recordedState = record.State;
            // Receiver-only metadata stays outside the signed package wire schema. A crash before
            // the atomic receipt write leaves an unknown collision, never a false duplicate.
            await WriteReceiptAsync(projectDir, record.Proposal, pkg);

            switch (record.State)
            {
                case ProposalState.Admitted:
                    // The apply can still fail closed (a governance path, a locked target). The
                    // admission is durably recorded either way; surface a partial apply as a
                    // WARNING that no green "admitted" line may swallow.
                    try
                    {
                        var applied = ForgeApplier.ApplyAll(forge, localForgeIds, projectDir, "gate",
                            policy!.Sandbox.EnforceWritableAllowlist ? policy.Sandbox.Writable : null);
                        return new(PackageAdmission.Admitted, record.Reason, pkg, record.Proposal.Id, applied);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                    {
                        return new(PackageAdmission.Admitted, record.Reason, pkg, record.Proposal.Id, [],
                            Warning: $"apply did not complete: {ex.Message}");
                    }
                case ProposalState.Held:
                    return new(PackageAdmission.Held, record.Reason, pkg, record.Proposal.Id, []);
                default:
                    ForgeApplier.RejectAll(forge, localForgeIds, "gate", record.Reason);
                    return new(PackageAdmission.Rejected, record.Reason, pkg, record.Proposal.Id, []);
            }
        }
        catch (Exception ex)
        {
            // Every failure, including I/O, timeout and cancellation, must attempt cleanup. A
            // second failure is reported explicitly; it must not hide the original node fault.
            var cleanupErrors = new List<string>();
            // Once a durable held/admitted decision owns the rows, preserve them for review or
            // recovery. Only rows with no owning decision (or a rejection) need abort cleanup.
            if (forge is not null && recordedState is not (ProposalState.Held or ProposalState.Admitted))
            {
                foreach (var id in localForgeIds)
                {
                    try { ForgeApplier.RejectAll(forge, [id], "gate", "import aborted: " + ex.Message); }
                    catch (Exception cleanup) { cleanupErrors.Add($"{id}: {cleanup.Message}"); }
                }
            }
            if (ex is OperationCanceledException)
            {
                if (cleanupErrors.Count > 0)
                    throw new OperationCanceledException("Import cancelled; cleanup also failed: "
                        + string.Join("; ", cleanupErrors), ex);
                throw;
            }
            var message = "node failed while importing: " + ex.Message;
            if (recordedId is not null)
                message += $". Gate decision '{recordedId}' was recorded; inspect its apply state.";
            if (cleanupErrors.Count > 0)
                message += ". Cleanup also failed: " + string.Join("; ", cleanupErrors);
            return new(PackageAdmission.Failed, message, pkg, recordedId, []);
        }
    }

    private sealed record ImportReceipt(string ProposalSha256, string Seal, string Signer);

    private static string ProposalHash(ExtensionProposal proposal) =>
        Convert.ToHexString(SHA256.HashData(CanonicalJson.Bytes(proposal)));

    private static string ReceiptPath(string projectDir, string id) =>
        Path.Combine(projectDir, ".ashlar", "import-receipts", id + ".json");

    private static async Task<ImportReceipt?> ReadReceiptAsync(string projectDir, ExtensionProposal proposal)
    {
        var read = await SafePackageRead.TryReadTextAsync(ReceiptPath(projectDir, proposal.Id), 4096);
        if (!read.Ok) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<ImportReceipt>(read.Text!);
            if (receipt is null || receipt.ProposalSha256 != ProposalHash(proposal)
                || string.IsNullOrEmpty(receipt.Seal) || string.IsNullOrEmpty(receipt.Signer)) return null;
            // Reject malformed sidecars rather than letting their display fingerprint throw.
            if (Convert.FromBase64String(receipt.Signer).Length != 32
                || Convert.FromBase64String(receipt.Seal).Length != 64) return null;
            return receipt;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }

    private static async Task WriteReceiptAsync(string projectDir, ExtensionProposal proposal, ExtensionPackage package)
    {
        var path = ReceiptPath(projectDir, proposal.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var receipt = new ImportReceipt(ProposalHash(proposal), package.Seal!, package.SealSigner!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt));
            File.Move(temporary, path); // append-once, never replace a conflicting receipt
        }
        catch (Exception writeError)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Import receipt write and temporary-file cleanup both failed.",
                    writeError, cleanupError);
            }
            throw;
        }
    }

    /// <summary>Display fingerprint for a base64 public key, or a plain note when absent.</summary>
    public static string Fingerprint(string? publicKeyBase64) =>
        publicKeyBase64 is null ? "(unsigned)" : OperatorKey.Fingerprint(Convert.FromBase64String(publicKeyBase64));
}
