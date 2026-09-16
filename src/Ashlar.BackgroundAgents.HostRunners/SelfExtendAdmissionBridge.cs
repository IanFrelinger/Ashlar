using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Packaging;
using Ashlar.Manifest.Signing;

namespace Ashlar.BackgroundAgents.HostRunners;

/// <summary>
/// The producer wiring: when a self-extend cycle runs inside an ashlar project (a repo with
/// <c>ashlar.policy.yaml</c> at its root), the cycle's outcome is recorded as an
/// <see cref="ExtensionProposal"/> through the admission gate — the same store
/// <c>ashlar gates</c> reads, so a cycle's work shows up in the held queue for a person to
/// seat or refuse.
///
/// <para>Outside an ashlar project this is a no-op: the runner behaves exactly as before.
/// The courses attached to the proposal are ONLY what the cycle actually evidences — today
/// that is the <c>sandbox</c> course (from the policy engine's denial count). A project
/// whose policy requires more gates (tests, security) will therefore see the proposal
/// REJECTED with "gate did not run" — which is correct and fail-closed: the runtime may not
/// claim courses it did not run, and the operator's policy decides whether the evidence
/// suffices.</para>
///
/// <para>Honest scope note: v0 records AFTER the cycle's mediated writes, making the gate a
/// ledger of what happened plus the human queue for it. Moving admission BEFORE the write
/// lands (propose → hold → apply) is the M1 enforcement ordering, tracked in SPEC-004.</para>
/// </summary>
public static class SelfExtendAdmissionBridge
{
    /// <summary>
    /// Records the cycle as a proposal when running inside an ashlar project.
    /// </summary>
    /// <returns>A one-line gate outcome for the run summary, or null when not an ashlar
    /// project or nothing was written.</returns>
    /// <remarks><paramref name="autoShare"/> opts an ADMITTED cycle into sharing its sealed
    /// package to the mesh (null defaults from <c>ASHLAR_MESH_AUTOSHARE=1</c>);
    /// <paramref name="meshDir"/> overrides the store (null resolves via
    /// <see cref="MeshStore.Resolve"/>). Tests inject both; they never mutate env.</remarks>
    public static async Task<string?> TryRecordAsync(
        string repoRoot,
        string agentName,
        string? objective,
        IReadOnlyList<string> writePaths,
        int toolCallsExecuted,
        int toolCallsDenied,
        ILogger logger,
        CancellationToken ct = default,
        IReadOnlyList<string>? forgeProposalIds = null,
        SigningIdentity? signer = null,
        bool? autoShare = null,
        string? meshDir = null,
        IExtensionCompileCheck? compileCheck = null,
        IPostApplyVerification? verification = null)
    {
        var policyPath = Path.Combine(repoRoot, "ashlar.policy.yaml");
        if (!File.Exists(policyPath))
        {
            return null;   // not an ashlar project; the runner is unchanged
        }
        forgeProposalIds ??= [];
        if (writePaths.Count == 0 && forgeProposalIds.Count == 0)
        {
            return null;   // nothing to propose
        }

        string policyYaml;
        try
        {
            policyYaml = await File.ReadAllTextAsync(policyPath, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Self-extend gate: could not read ashlar.policy.yaml");
            return $"GATE ERROR: could not read ashlar.policy.yaml ({ex.Message})";
        }

        if (!PolicyLoader.TryLoad(policyYaml, out var policy, out var reason))
        {
            // Fail loud, never silent: an unreadable envelope is an error the operator must
            // see, not a skipped gate.
            logger.LogError("Self-extend gate: policy rejected: {Reason}", reason);
            return $"GATE ERROR: {reason}";
        }

        var forge = forgeProposalIds.Count > 0 ? AshlarProjectMediation.ProjectStore(repoRoot) : null;

        // A2 — executed evidence. Compile the proposed source (Roslyn, in-process) so the gate
        // admits on RUN evidence, not a self-reported claim. A change that does not compile earns a
        // FAILED build course and is rejected by any policy that requires `build`. Fail-closed: a
        // verifier error is a failed course, never a pass. Skipped only when no compile check is
        // wired or there is nothing mediated to compile (behaviour then matches pre-A2 exactly).
        CourseResult? buildCourse = null;
        if (compileCheck is not null && forge is not null && forgeProposalIds.Count > 0)
        {
            var proposedFiles = new List<ProposedFileContent>(forgeProposalIds.Count);
            foreach (var id in forgeProposalIds)
            {
                var row = forge.Find(id);
                if (row is not null)
                {
                    proposedFiles.Add(new ProposedFileContent(row.TargetPath, row.NewContent));
                }
            }
            try
            {
                var result = await compileCheck.CheckAsync(proposedFiles, ct).ConfigureAwait(false);
                buildCourse = new CourseResult { Name = "build", Passed = result.Passed, Detail = result.Detail };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Self-extend gate: build compile-check errored");
                buildCourse = new CourseResult { Name = "build", Passed = false, Detail = $"compile check errored: {ex.Message}" };
            }
        }

        ExtensionProposal proposal;
        try
        {
            proposal = BuildProposal(agentName, objective, writePaths, toolCallsExecuted, toolCallsDenied,
                forgeProposalIds, forge, buildCourse is null ? null : new[] { buildCourse });
        }
        catch (InvalidOperationException ex)
        {
            // A cycle that references a row the store does not hold cannot claim its content, and
            // a record signed without the claim is unrepairable (append-once). Same loud GATE
            // ERROR shape as an unreadable policy — never a silently under-claimed record.
            logger.LogError(ex, "Self-extend gate: cycle references a forge row that is not in the store");
            return $"GATE ERROR: {ex.Message}";
        }

        // Sign the runtime's own proposals with the operator identity, when one is present, so a
        // self-extend cycle's recorded verdict carries the same provenance as an operator's manual
        // decision (SPEC-006). Tests inject the identity; production loads the machine key. A
        // corrupt key fails the record LOUD — the same GATE ERROR shape as an unreadable policy —
        // rather than silently recording unsigned, which would hide that signing was ever expected.
        if (signer is null)
        {
            try
            {
                signer = OperatorKey.TryLoad();
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "Self-extend gate: operator key is corrupt");
                return $"GATE ERROR: {ex.Message}";
            }
        }

        var store = new GateStore(Path.Combine(repoRoot, ".ashlar"), signer);
        var record = await store.ProposeAsync(policy!, proposal, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Self-extend gate: proposal {Id} -> {State} ({Reason})",
            proposal.Id, record.State, record.Reason);

        // M1 propose → hold → APPLY: the gate's verdict decides what happens to the parked
        // forge proposals. Held leaves them parked for `gates --admit`; a rejection rejects
        // them (sealed thereby truly seals — nothing was on disk, nothing ever lands); an
        // automatic self-extending admission applies them now, within budget, as decided.
        var shareNote = string.Empty;
        string? rollbackNote = null;
        if (forge is not null)
        {
            var allowlist = policy!.Sandbox.EnforceWritableAllowlist ? policy.Sandbox.Writable : null;
            try
            {
            switch (record.State)
            {
                case ProposalState.Rejected:
                    ForgeApplier.RejectAll(forge, forgeProposalIds, "gate", record.Reason);
                    break;
                case ProposalState.Admitted when verification is not null:
                    // A4 — post-apply canary. Apply, verify the files as they landed, and revert if
                    // the canary fails: an auto-admitted change that does not survive verification
                    // must never be left on an unattended node. The gate record stands (append-once,
                    // the admission decision was made) but the writes are rolled back.
                    var outcome = await ForgeApplier.ApplyAllWithVerificationAsync(
                        forge, forgeProposalIds, repoRoot, "gate", verification, allowlist, ct).ConfigureAwait(false);
                    if (outcome.DidApply)
                    {
                        logger.LogInformation("Self-extend gate: applied {Count} mediated write(s), post-apply canary passed", outcome.AppliedPaths.Count);
                        shareNote = TryAutoShare(record, forge, forgeProposalIds, signer, autoShare, meshDir, logger);
                    }
                    else
                    {
                        rollbackNote = outcome.Reason;
                        if (outcome.UnrestoredPaths.Count == 0)
                        {
                            logger.LogWarning("Self-extend gate: post-apply canary REJECTED {Id} — reverted ({Reason})", proposal.Id, outcome.Reason);
                        }
                        else
                        {
                            // A rollback that could not fully restore is a node-integrity anomaly, not a routine revert.
                            logger.LogError("Self-extend gate: post-apply canary REJECTED {Id} but ROLLBACK WAS INCOMPLETE — these files remain changed: {Files}",
                                proposal.Id, string.Join(", ", outcome.UnrestoredPaths));
                        }
                    }
                    break;
                case ProposalState.Admitted:
                    // No canary wired: pre-A4 behaviour — apply and share. (The apply is still
                    // transactional against a mid-batch write failure.)
                    var applied = ForgeApplier.ApplyAll(forge, forgeProposalIds, repoRoot, "gate", allowlist);
                    logger.LogInformation("Self-extend gate: applied {Count} mediated write(s)", applied.Count);
                    shareNote = TryAutoShare(record, forge, forgeProposalIds, signer, autoShare, meshDir, logger);
                    break;
            }
            }
            catch (Exception ex)
            {
                // The apply/canary path may throw only in genuinely exceptional cases (a snapshot it
                // could not take, a batch that failed to write and rolled back, a store fault). Never
                // let that escape and kill the cycle silently — surface it LOUD, the same GATE ERROR
                // shape as an unreadable policy. Anything written was already rolled back by ForgeApplier.
                logger.LogError(ex, "Self-extend gate: applying admitted proposal {Id} failed", proposal.Id);
                return $"GATE ERROR: apply failed for {proposal.Id} — {ex.Message}";
            }
        }

        return record.State switch
        {
            ProposalState.Held => $"GATE: held as {proposal.Id} — review with `ashlar gates`",
            ProposalState.Admitted when rollbackNote is not null =>
                $"GATE: admitted as {proposal.Id} but REVERTED by post-apply canary — {rollbackNote}",
            ProposalState.Admitted => $"GATE: admitted as {proposal.Id} — {record.Reason}{shareNote}",
            _ => $"GATE: rejected — {record.Reason}",
        };
    }

    /// <summary>
    /// Best-effort auto-share of an admitted, applied extension into the mesh (co-production:
    /// peers pull what this cycle produced, through THEIR OWN gates). Opt-in — the autoShare
    /// parameter, or <c>ASHLAR_MESH_AUTOSHARE=1</c> when it is null. Never fails the cycle: a
    /// share failure logs a warning and annotates the outcome string, nothing more. The
    /// admission and its applied writes stand regardless.
    /// </summary>
    internal static string TryAutoShare(
        GateRecord record,
        Ashlar.BackgroundAgents.Forge.ChangeProposalStore forge,
        IReadOnlyList<string> forgeProposalIds,
        SigningIdentity? signer,
        bool? autoShare,
        string? meshDir,
        ILogger logger)
    {
        var enabled = autoShare ?? Environment.GetEnvironmentVariable("ASHLAR_MESH_AUTOSHARE") == "1";
        if (!enabled)
        {
            return string.Empty;
        }

        // A package's seal is a signature (SPEC-006): an unsigned admission cannot travel.
        // Skipping is honest and non-fatal — the annotation says why nothing reached the mesh.
        if (signer is null || record.Sig is null || record.Signer is null)
        {
            logger.LogWarning("Self-extend gate: auto-share skipped — the admission is unsigned (run `ashlar keys init`)");
            return "; auto-share skipped: unsigned admission (run `ashlar keys init`)";
        }

        try
        {
            var files = new List<PackageFile>();
            foreach (var id in forgeProposalIds)
            {
                var proposal = forge.Find(id)
                    ?? throw new InvalidOperationException($"forge proposal '{id}' is missing — cannot package an incomplete extension.");
                // ApplyAll just marked these rows Applied. Anything else under this id is a row
                // the gate did not admit-and-apply (a shadow, a replacement) — refuse to seal it.
                if (proposal.Status != Ashlar.BackgroundAgents.Forge.ChangeProposalStatus.Applied)
                {
                    throw new InvalidOperationException(
                        $"forge proposal '{id}' is {proposal.Status}, not Applied — only content the gate admitted AND applied may travel.");
                }
                files.Add(new PackageFile { Path = proposal.TargetPath, Content = proposal.NewContent });
            }
            // The rows just re-read from the mutable forge store must hash to the claims the
            // signed admission carries — a row edited since the gate decided must not travel
            // under the origin's signature. Refusing is still best-effort: the admission and
            // its applied writes stand, only the share is withheld.
            if (!ExtensionPackaging.VerifyFileClaims(record.Proposal, files, out var claimReason))
            {
                logger.LogWarning("Self-extend gate: auto-share refused — {Reason}", claimReason);
                return $"; auto-share refused: {claimReason}";
            }
            var json = ExtensionPackaging.Pack(record, files, signer);
            var dest = MeshStore.Publish(MeshStore.Resolve(meshDir), json);
            logger.LogInformation("Self-extend gate: shared to the mesh → {Dest}", dest);
            return $"; shared → {dest}";
        }
        catch (Exception ex)
        {
            // Best-effort means BEST-EFFORT: no exception class thrown in here may destroy a
            // successful cycle — an escape would replace the whole run result with a failure.
            logger.LogWarning(ex, "Self-extend gate: auto-share failed — the admission stands, the share did not happen");
            return $"; auto-share failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Maps cycle facts onto a proposal. Public and pure-over-the-store so tests pin the
    /// mapping. The courses claim ONLY what the cycle evidences: the sandbox course from the
    /// policy engine's own denial count, failed by any denial and by a mixed cycle (parked rows
    /// AND direct writes), with a detail that says exactly what was parked, written and denied.
    /// The diff names both the parked rows and the direct writes. When a store is supplied, each referenced row is
    /// resolved exactly once and yields BOTH its diff line and its content claim (path,
    /// sha256(NewContent)) — the claims are signed with the record, so packaging can later
    /// prove the rows it re-reads are the bytes this gate decided over. A referenced row
    /// missing from the store throws: the claim it would sign covers nothing, the record is
    /// append-once, and a silently under-claimed admission could never travel — refusing loud
    /// at propose is the only repairable shape.
    /// </summary>
    public static ExtensionProposal BuildProposal(
        string agentName,
        string? objective,
        IReadOnlyList<string> writePaths,
        int toolCallsExecuted,
        int toolCallsDenied,
        IReadOnlyList<string>? forgeProposalIds = null,
        Ashlar.BackgroundAgents.Forge.ChangeProposalStore? forge = null,
        IReadOnlyList<CourseResult>? extraCourses = null)
    {
        forgeProposalIds ??= [];
        var mediated = forgeProposalIds.Count > 0;

        List<Ashlar.BackgroundAgents.Forge.ChangeProposal>? rows = null;
        if (mediated && forge is not null)
        {
            rows = new List<Ashlar.BackgroundAgents.Forge.ChangeProposal>(forgeProposalIds.Count);
            foreach (var id in forgeProposalIds)
            {
                rows.Add(forge.Find(id) ?? throw new InvalidOperationException(
                    $"forge proposal '{id}' is not in the store — park the write before proposing; "
                    + "a record cannot claim content the store does not hold."));
            }
        }

        // The sandbox course claims what the cycle evidenced, and nothing more.
        //
        // The denial count is NOT a confinement signal in a mediated cycle, because mediation
        // works by denying: ForgeMediatedWritesPolicy refuses the direct write and redirects the
        // planner to forge.propose_change, so a healthy mediated cycle carries at least one
        // denial by construction — and AgentCycleResult carries only the count, not which policy
        // spoke. An earlier shape of this change counted them anyway and failed every mediated
        // cycle's sandbox course; M1EnforcementTests caught it. What IS the signal under
        // mediation is whether anything reached disk, because keeping the cycle off the disk is
        // what mediation is for. A MIXED cycle — parked rows AND direct writes — is therefore not
        // confined.
        //
        // In an unmediated cycle there is no redirect, so a denial is a refusal and one is
        // enough. Either way the count goes into the detail, so a reviewer sees it.
        var confined = mediated ? writePaths.Count == 0 : toolCallsDenied == 0;

        // One honest sentence in every branch. "nothing touched disk" is a claim about
        // writePaths, so it is said only when writePaths is empty.
        var sandboxDetail =
            $"{forgeProposalIds.Count} proposal(s) parked, {writePaths.Count} direct write(s), {toolCallsDenied} denied"
            + (writePaths.Count == 0 ? " — nothing touched disk" : string.Empty);

        // The diff names BOTH the parked rows and the direct writes. A record that listed only
        // the parked rows of a mixed cycle would sign away the writes that actually landed.
        var diffLines = (rows?.Select(p => $"~ {p.TargetPath}  ({Truncate(p.Summary, 60)})") ?? Enumerable.Empty<string>())
            .Concat(writePaths.Select(p => "~ " + p));

        return new ExtensionProposal
        {
            // Matches the store's id allowlist: alphanumeric start, [A-Za-z0-9_-].
            Id = "ext-" + Guid.NewGuid().ToString("N")[..12],
            Kind = "brick",
            Summary = string.IsNullOrWhiteSpace(objective)
                ? $"self-extend cycle by {agentName}: {forgeProposalIds.Count + writePaths.Count} change(s)"
                : Truncate(objective!, 120),
            ProposedBy = agentName,
            ProposedAt = DateTimeOffset.UtcNow,
            Diff = string.Join("\n", diffLines),
            ForgeProposalIds = forgeProposalIds,
            Files = rows?.Select(p => FileClaim.For(p.TargetPath, p.NewContent)).ToList(),
            Courses = BuildCourses(confined, sandboxDetail, extraCourses),
        };
    }

    private static List<CourseResult> BuildCourses(
        bool confined, string sandboxDetail, IReadOnlyList<CourseResult>? extraCourses)
    {
        var courses = new List<CourseResult>
        {
            new CourseResult { Name = "sandbox", Passed = confined, Detail = sandboxDetail },
        };
        if (extraCourses is not null)
        {
            courses.AddRange(extraCourses);
        }
        return courses;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
