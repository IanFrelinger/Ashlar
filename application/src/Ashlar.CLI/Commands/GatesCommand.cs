using System.CommandLine;
using System.CommandLine.Invocation;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Packaging;
using Ashlar.Manifest.Signing;

namespace Ashlar.CLI.Commands;

/// <summary>
/// <c>ashlar gates</c> — the human half of admission: list what is held, inspect one
/// proposal, seat the stone or refuse with a reason.
///
/// <para>Thin over the kernel: every rule (who may transition what, refusals require
/// reasons, immutable history) lives in <see cref="GateStore"/> and is pinned by kernel
/// tests; this class reads the store under <c>.ashlar/</c> in the project directory and
/// renders in the fixed vocabulary. Gold appears only when a stone is seated.</para>
/// </summary>
public sealed class GatesCommand : Command
{
    /// <summary>
    /// The writable allowlist to enforce at apply time, or null when the project has not opted in.
    /// The governance floor is always enforced inside ForgeApplier regardless; this only adds the
    /// opt-in <c>sandbox.enforceWritableAllowlist</c> confinement so a manual seat honours the same
    /// policy the automatic admit paths do.
    /// </summary>
    private static IReadOnlyList<string>? LoadWritableAllowlist(DirectoryInfo directory)
    {
        var policyPath = Path.Combine(directory.FullName, "ashlar.policy.yaml");
        if (File.Exists(policyPath)
            && PolicyLoader.TryLoad(File.ReadAllText(policyPath), out var policy, out _)
            && policy!.Sandbox.EnforceWritableAllowlist)
        {
            return policy.Sandbox.Writable;
        }
        return null;
    }

    /// <summary>Creates a new GatesCommand instance.</summary>
    public GatesCommand() : base("gates", "List held proposals; seat the stone or refuse, with a reason.")
    {
        var pathOpt = new Option<DirectoryInfo>(
            name: "--path",
            description: "Project directory (defaults to current).",
            getDefaultValue: () => new DirectoryInfo(Environment.CurrentDirectory));
        var showOpt = new Option<string?>("--show", "Show one proposal in full.");
        var admitOpt = new Option<string?>("--admit", "Seat the stone on a held proposal.");
        var refuseOpt = new Option<string?>("--refuse", "Refuse a held proposal.");
        var reasonOpt = new Option<string?>("--reason", "Why (required with --refuse; recorded and fed back to the proposer).");
        var actorOpt = new Option<string>(
            name: "--as",
            description: "Who is deciding. Recorded in the ledger.",
            getDefaultValue: () => Environment.UserName);

        AddGlobalOption(pathOpt); AddOption(showOpt); AddOption(admitOpt);
        AddOption(refuseOpt); AddOption(reasonOpt); AddOption(actorOpt);

        // Hidden: the runtime's entry. A proposal arrives as JSON, the policy decides its
        // fate (AdmissionGate rule order), the outcome is recorded. The SelfExtendRunner
        // calls this path when it wires in; until then it is how tests and demos feed the
        // queue. Hidden because users never propose — applications do.
        var proposeFileOpt = new Option<FileInfo>("--file", "Proposal JSON.") { IsRequired = true };
        var proposeCmd = new Command("propose", "Submit an evaluated proposal (runtime entry).")
        {
            proposeFileOpt,
        };
        proposeCmd.IsHidden = true;
        proposeCmd.SetHandler(async (InvocationContext ctx) =>
        {
            ctx.ExitCode = await ProposeAsync(
                ctx.ParseResult.GetValueForOption(pathOpt) ?? new DirectoryInfo(Environment.CurrentDirectory),
                ctx.ParseResult.GetValueForOption(proposeFileOpt)!);
        });
        AddCommand(proposeCmd);

        // Explicit activation of signed gate records for THIS project (SPEC-006 S-6). `keys init`
        // does this in the project it is run in; this verb covers a project set up after the key
        // existed. The activation INSTANT never moves — not by re-running this, not by rotating
        // the key, not under --repair — so the grace window cannot be re-opened. Two things this
        // verb may still do that the kernel's automatic write path refuses: replace a marker
        // signed by a key this machine does not vouch for, and, under --repair, re-mint the
        // inventory of unsigned records over what is on disk now. Both print what they
        // grandfathered, because both are a blessing and a blessing nobody counted is not one.
        var repairOpt = new Option<bool>(
            "--repair",
            "Re-mint the grandfather inventory over the store as it stands now, keeping the original activation instant. "
            + "For a store whose marker was deleted or predates the inventory. It re-blesses whatever is on disk: read the count.");
        var activateCmd = new Command(
            "sign-activate",
            "Activate signed gate records here: from now on an unsigned verdict in this store is refused as a stripped signature.")
        { repairOpt };
        activateCmd.SetHandler(async (InvocationContext ctx) =>
        {
            ctx.ExitCode = await SignActivateAsync(
                ctx.ParseResult.GetValueForOption(pathOpt) ?? new DirectoryInfo(Environment.CurrentDirectory),
                ctx.ParseResult.GetValueForOption(repairOpt));
        });
        AddCommand(activateCmd);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            ctx.ExitCode = await ExecuteAsync(
                ctx.ParseResult.GetValueForOption(pathOpt)!,
                ctx.ParseResult.GetValueForOption(showOpt),
                ctx.ParseResult.GetValueForOption(admitOpt),
                ctx.ParseResult.GetValueForOption(refuseOpt),
                ctx.ParseResult.GetValueForOption(reasonOpt),
                ctx.ParseResult.GetValueForOption(actorOpt)!);
        });
    }

    private static string StateRoot(DirectoryInfo directory) => Path.Combine(directory.FullName, ".ashlar");

    /// <summary>
    /// Opens the store for a WRITE (admit / refuse / propose / sign-activate) with the operator's
    /// local signing identity, when one exists. Presence-activated (SPEC-006): a key means the
    /// verdict this command records is signed; no key means unsigned, exactly as before — zero-setup
    /// keeps working — UNLESS the store is already signed, in which case the kernel refuses the
    /// unsigned write by name (rule S-6) rather than downgrade the store. The identity is the
    /// machine-global operator key (<c>ASHLAR_KEY_DIR</c> / <c>~/.ashlar/keys</c>), not a per-project
    /// one. <see cref="OperatorKey.TryLoad(string?)"/> throws on a corrupt key rather than sign with a
    /// mismatched or unreadable identity; callers run this inside their try so refusal reaches the
    /// user as a clean message, not a stack trace. Spelled <c>new GateStore(</c> so the read-funnel
    /// convention test can see it.
    /// </summary>
    private static GateStore OpenSigningStore(DirectoryInfo directory) =>
        new GateStore(StateRoot(directory), OperatorKey.TryLoad());

    /// <summary>
    /// Opens the store for a READ (list / show). A read needs PUBLIC key material, not the private
    /// identity: the store loads the signer-pinning set (<c>operator.pub</c>, <c>trusted/*.pub</c>)
    /// and the activation marker itself, so this reader resolves the full signature expectation and
    /// refuses a stripped or re-signed record exactly as it would with the identity loaded. What it
    /// deliberately does not load is <c>operator.key</c>: a mangled private seed must never block an
    /// operator from seeing the queue (the e2e loop pins <c>read-survives-a-corrupt-operator-key</c>).
    /// Only writes, which have to sign, load the seed and so fail closed when it is corrupt.
    /// </summary>
    private static GateStore OpenReadStore(DirectoryInfo directory) =>
        new GateStore(StateRoot(directory));

    private static async Task<int> ExecuteAsync(
        DirectoryInfo directory, string? show, string? admit, string? refuse, string? reason, string actor)
    {
        if (!File.Exists(Path.Combine(directory.FullName, "ashlar.yaml")))
        {
            Console.Error.WriteLine($"not an ashlar project: no ashlar.yaml in {directory.FullName}");
            return 1;
        }

        try
        {
            if (admit is not null)
            {
                // Pre-flight the signed content claims BEFORE any decision is recorded: the
                // forge rows are mutable disk and the proposal may have sat Held for days — a
                // row edited in that window must not be seated, let alone applied. Refusing
                // HERE leaves the record Held and the tree untouched; discovering the mismatch
                // after DecideAsync would strand an admitted-but-unapplied record. Only a HELD
                // record is pre-flighted — anything else falls through so the kernel's
                // transition-authority wording stays the contract for re-decides.
                var held = await OpenReadStore(directory).GetAsync(admit);
                if (held is { State: ProposalState.Held, Proposal: { Files: not null, ForgeProposalIds.Count: > 0 } })
                {
                    var preflightForge = Ashlar.BackgroundAgents.HostRunners.AshlarProjectMediation.ProjectStore(directory.FullName);
                    var rows = new List<PackageFile>(held.Proposal.ForgeProposalIds.Count);
                    string? missing = null;
                    foreach (var id in held.Proposal.ForgeProposalIds)
                    {
                        var row = preflightForge.Find(id);
                        if (row is null) { missing = id; break; }
                        rows.Add(new PackageFile { Path = row.TargetPath, Content = row.NewContent });
                    }
                    if (missing is not null)
                    {
                        Console.Error.WriteLine(
                            $"REFUSED: forge proposal '{missing}' referenced by '{admit}' is missing from the forge store. "
                            + "The proposal stays Held, nothing was decided, and nothing is on disk.");
                        return 65;
                    }
                    if (!ExtensionPackaging.VerifyFileClaims(held.Proposal, rows, out var claimReason))
                    {
                        Console.Error.WriteLine(claimReason);
                        Console.Error.WriteLine(
                            "  The parked rows are not the bytes this admission's signature claims — the proposal "
                            + "stays Held, nothing was decided, and nothing is on disk.");
                        return 65;
                    }
                }

                var decided = await OpenSigningStore(directory).DecideAsync(admit, admit: true, actor, reason ?? "seated after review", DateTimeOffset.UtcNow);
                Console.WriteLine($"  {Gold("✓ seated")}  {decided.Proposal.Summary}");
                Console.WriteLine($"  {Dim($"admitted by {decided.Actor} · recorded")}");

                // M1 apply: seating the stone is what puts the held writes on disk. The
                // decision is already recorded above; an apply failure is reported loudly
                // and the operator re-runs the apply — it must never look like the writes
                // landed when they did not.
                if (decided.Proposal.ForgeProposalIds.Count > 0)
                {
                    try
                    {
                        var forge = Ashlar.BackgroundAgents.HostRunners.AshlarProjectMediation.ProjectStore(directory.FullName);
                        var applied = Ashlar.BackgroundAgents.HostRunners.ForgeApplier.ApplyAll(
                            forge, decided.Proposal.ForgeProposalIds, directory.FullName, actor,
                            LoadWritableAllowlist(directory));
                        foreach (var path in applied)
                        {
                            Console.WriteLine($"  {Ok("✓ applied")}  {path}");
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.Error.WriteLine($"  APPLY FAILED — the admission is recorded but the writes are NOT on disk: {ex.Message}");
                        return 1;
                    }
                }
                return 0;
            }
            if (refuse is not null)
            {
                var decided = await OpenSigningStore(directory).DecideAsync(refuse, admit: false, actor, reason ?? string.Empty, DateTimeOffset.UtcNow);
                Console.WriteLine($"  {Clay("! refused")}  {decided.Proposal.Summary}");
                Console.WriteLine($"  {Dim($"reason recorded and fed back: {decided.Reason}")}");

                // The refusal rejects the parked writes too — no orphaned pending work.
                if (decided.Proposal.ForgeProposalIds.Count > 0)
                {
                    var forge = Ashlar.BackgroundAgents.HostRunners.AshlarProjectMediation.ProjectStore(directory.FullName);
                    Ashlar.BackgroundAgents.HostRunners.ForgeApplier.RejectAll(
                        forge, decided.Proposal.ForgeProposalIds, actor, decided.Reason);
                    Console.WriteLine($"  {Dim($"{decided.Proposal.ForgeProposalIds.Count} parked write(s) rejected — disk untouched")}");
                }
                return 0;
            }
            if (show is not null)
            {
                return await ShowAsync(OpenReadStore(directory), show);
            }
            return await ListAsync(OpenReadStore(directory));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The kernel's rules speaking (only Held can be decided; refusals need reasons;
            // unknown ids) — or store I/O failing (an unreadable .ashlar or forge tree while
            // pre-flighting claims). Their wording is the contract; pass it through.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ProposeAsync(DirectoryInfo directory, FileInfo file)
    {
        var policyPath = Path.Combine(directory.FullName, "ashlar.policy.yaml");
        if (!File.Exists(policyPath))
        {
            Console.Error.WriteLine($"not an ashlar project: no ashlar.policy.yaml in {directory.FullName}");
            return 1;
        }
        if (!PolicyLoader.TryLoad(await File.ReadAllTextAsync(policyPath), out var policy, out var reason))
        {
            Console.Error.WriteLine(reason);
            return 1;
        }

        ExtensionProposal? proposal;
        try
        {
            proposal = System.Text.Json.JsonSerializer.Deserialize<ExtensionProposal>(
                await File.ReadAllTextAsync(file.FullName),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"REJECTED: proposal could not be parsed: {ex.Message}");
            return 1;
        }
        if (proposal is null)
        {
            Console.Error.WriteLine("REJECTED: proposal file was empty.");
            return 1;
        }

        GateRecord record;
        try
        {
            // Settle the content claims BEFORE the record is minted — records are append-once,
            // so a claim wrong at propose time is wrong forever (the admission could never
            // export). Every referenced row must exist NOW: park-then-propose is the M1 order,
            // and a record referencing content the store does not hold would sign a claim over
            // nothing. Inside the try: the store constructor and row reads are I/O, and their
            // failures must reach the user as the command's clean message, not a stack trace.
            if (proposal.ForgeProposalIds.Count > 0)
            {
                var forge = Ashlar.BackgroundAgents.HostRunners.AshlarProjectMediation.ProjectStore(directory.FullName);
                var rows = new List<PackageFile>(proposal.ForgeProposalIds.Count);
                foreach (var id in proposal.ForgeProposalIds)
                {
                    var row = forge.Find(id);
                    if (row is null)
                    {
                        Console.Error.WriteLine(
                            $"REJECTED: forge proposal '{id}' is not in the store — park the write first, then propose. "
                            + "A record that referenced missing content would sign a claim over nothing, and records are append-once.");
                        return 1;
                    }
                    rows.Add(new PackageFile { Path = row.TargetPath, Content = row.NewContent });
                }
                if (proposal.Files is null)
                {
                    // Claim the content being admitted, the same way a self-extend cycle does:
                    // (path, sha256) per row, in id order, signed into the record.
                    proposal = proposal with
                    {
                        Files = rows.Select(r => FileClaim.For(r.Path, r.Content)).ToList(),
                    };
                }
                else if (!ExtensionPackaging.VerifyFileClaims(proposal, rows, out var claimReason))
                {
                    // A file that authors its own claims must author them TRUE: signing claims
                    // the parked rows already fail would mint a permanently unexportable record.
                    Console.Error.WriteLine("REJECTED: the proposal's own file claims do not match the parked rows. " + claimReason);
                    return 1;
                }
            }

            var store = OpenSigningStore(directory);

            // One call, one transaction. The count-decide-record sequence lives kernel-side
            // under the store lock (#373) so a racing process can never separate the budget
            // check from the recording — this command must not reassemble that race by calling
            // the pieces itself.
            record = await store.ProposeAsync(policy!, proposal, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The kernel's rules speaking (append-once ids, illegal id shapes), a corrupt
            // operator key refusing to sign, or forge-store I/O failing (an unreadable or
            // read-only .ashlar while resolving the referenced rows). Their wording is the
            // contract; pass it through as the command's clean refusal shape.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var line = record.State switch
        {
            ProposalState.Held => Clay($"! HELD — {record.Reason}"),
            ProposalState.Admitted => Gold($"✓ ADMITTED — {record.Reason}"),
            _ => Bad($"× REJECTED — {record.Reason}"),
        };
        Console.WriteLine($"  {line}");
        return record.State == ProposalState.Rejected ? 65 : 0;
    }

    private static async Task<int> SignActivateAsync(DirectoryInfo directory, bool repair)
    {
        if (!File.Exists(Path.Combine(directory.FullName, "ashlar.yaml")))
        {
            Console.Error.WriteLine($"not an ashlar project: no ashlar.yaml in {directory.FullName}");
            return 1;
        }

        try
        {
            var signer = OperatorKey.TryLoad();
            if (signer is null)
            {
                Console.Error.WriteLine("activating signed gate records requires an operator key — the activation marker is itself signed.");
                Console.Error.WriteLine("create one with:  ashlar keys init");
                return 1;
            }

            var outcome = await new GateStore(StateRoot(directory), signer)
                .ActivateSigningAsync(DateTimeOffset.UtcNow, repair);
            var marker = outcome.Marker;

            if (outcome.ReplacedUnvouchedMarker)
            {
                // The one outcome here that is a security event rather than a setup step.
                Console.WriteLine($"  {Clay("! replaced a signing marker this machine does not vouch for")}");
                Console.WriteLine($"  {Dim("somebody else's declaration about this store was in force. It has been overwritten with this operator's own — if you did not expect that, investigate who wrote it before trusting anything here.")}");
            }
            else if (outcome.WasAlreadyActive && !repair)
            {
                Console.WriteLine($"  {Dim($"signing already active here since {marker.ActivatedAt:u} — the instant never moves")}");
                RenderGrandfathered(outcome, blessing: false);
                return 0;
            }

            // BEFORE the success line, never after: this count is what the operator is being asked
            // to authorize, and an authorization printed under a tick has already been granted.
            RenderGrandfathered(outcome, blessing: true);

            // The marker's signature was verified on the way back, so its fingerprint may be printed (S-3).
            var verb = outcome.WasAlreadyActive && repair ? "✓ inventory re-minted" : "✓ signing activated";
            Console.WriteLine($"  {Gold(verb)}  {Dim($"{marker.ActivatedAt:u} · {OperatorKey.Fingerprint(Convert.FromBase64String(marker.Signer!))}")}");
            Console.WriteLine($"  {Dim("records decided before this instant stay readable unsigned; an unsigned verdict after it is refused as stripped.")}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A corrupt operator key, a corrupt marker already on disk, or an unwritable .ashlar.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// The number the operator is authorizing, and the only control on the grandfather mechanism.
    ///
    /// <para>An unsigned record in the inventory is trusted for the bytes it had at this instant
    /// and is never signed, so a fabricated <c>Admitted</c> record planted BEFORE activation is
    /// blessed permanently and spends self-extension budget forever. Nothing downstream can tell
    /// that record from an honest one — the inventory records that the operator said yes, not that
    /// the record is true. So the count is printed before the success line, the admitted subset is
    /// broken out because that is the half that costs budget, and the operator is told plainly what
    /// to do if the number surprises them. Every other mechanism in S-6 is downstream of this line
    /// being read.</para>
    /// </summary>
    private static void RenderGrandfathered(GateSigningOutcome outcome, bool blessing)
    {
        var n = outcome.Marker.Grandfathered?.Count ?? 0;
        if (n == 0)
        {
            Console.WriteLine($"  {Dim("grandfathering nothing — every record in this store is signed.")}");
            return;
        }

        var verb = blessing ? "grandfathering" : "grandfathered here:";
        Console.WriteLine(
            $"  {Clay($"{verb} {n} unsigned record(s), {outcome.GrandfatheredAdmitted} of them admitted")}"
            + $"  {Dim("— trusted as-is, and NOT signed")}");
        Console.WriteLine(
            $"  {Dim("if that is more than you decided yourself, stop: this authorizes every one of them, and an admitted record spends self-extension budget. inspect with: ashlar gates")}");
    }

    /// <summary>
    /// One line on the store's signature posture, above any listing: whether signatures are
    /// EXPECTED here and on what basis, and a warning when this machine holds a key but the
    /// store has never been signed — the one configuration where rule S-6 is inert and the
    /// operator would not otherwise know. Names anchors, never keys (S-3).
    /// </summary>
    private static void RenderTrust(GateSignatureExpectation? trust)
    {
        if (trust is null)
        {
            return;
        }
        if (trust.Expected)
        {
            Console.WriteLine($"  {Dim($"signing: expected — {trust.Basis}")}");
        }
        else if (trust.TrustedSigners.Count > 0)
        {
            Console.WriteLine($"  {Clay("! signing: not yet expected")}  {Dim("an operator key is present but this store has never been signed, so unsigned records here carry no promise. activate with: ashlar gates sign-activate")}");
        }
        else
        {
            Console.WriteLine($"  {Dim($"signing: not expected — {trust.Basis}")}");
        }
    }

    /// <summary>The record's signature, for a listing. A signed record that reached a renderer
    /// was verified by the store (it refuses otherwise), so its fingerprint may be printed; an
    /// unsigned record gets the word and nothing more (S-3).</summary>
    private static string Signature(GateRecord r) =>
        r.Sig is null || r.Signer is null
            ? "unsigned"
            : $"signed {OperatorKey.Fingerprint(Convert.FromBase64String(r.Signer))}";

    private static async Task<int> ListAsync(GateStore store)
    {
        var held = await store.ListAsync(ProposalState.Held);
        RenderTrust(store.SignatureTrust);
        if (held.Count == 0)
        {
            Console.WriteLine($"  {Dim("nothing held — the wall is quiet")}");
            return 0;
        }

        Console.WriteLine();
        foreach (var r in held)
        {
            Console.WriteLine($"  {Clay("!")} {r.Proposal.Id,-12} {r.Proposal.Summary}");
            Console.WriteLine($"    {Dim($"by {r.Proposal.ProposedBy} · {r.Proposal.ProposedAt:HH:mm 'UTC'} · {Signature(r)} · {r.Reason}")}");
        }
        Console.WriteLine();
        Console.WriteLine($"  {Dim("inspect:")} ashlar gates --show <id>   {Dim("seat:")} ashlar gates --admit <id>");
        return 0;
    }

    private static async Task<int> ShowAsync(GateStore store, string id)
    {
        var r = await store.GetAsync(id);
        if (r is null)
        {
            Console.Error.WriteLine($"no proposal '{id}' in the store.");
            return 1;
        }

        RenderTrust(store.SignatureTrust);
        Console.WriteLine();
        Console.WriteLine($"  {r.Proposal.Summary}");
        Console.WriteLine($"  {Dim(Signature(r))}");
        Console.WriteLine($"  {Dim($"{r.Proposal.Id} · kind {r.Proposal.Kind} · by {r.Proposal.ProposedBy} · {r.Proposal.ProposedAt:u}")}");
        Console.WriteLine();
        foreach (var c in r.Proposal.Courses)
        {
            var glyph = c.Passed ? Ok("✓") : Bad("×");
            Console.WriteLine($"  {glyph} {c.Name,-12} {Dim(c.Detail)}");
        }
        Console.WriteLine();
        var state = r.State switch
        {
            ProposalState.Held => Clay($"! HELD — {r.Reason}"),
            ProposalState.Admitted => Gold($"✓ ADMITTED by {r.Actor}"),
            ProposalState.Refused => Dim($"REFUSED by {r.Actor}: {r.Reason}"),
            _ => Bad($"× REJECTED — {r.Reason}"),
        };
        Console.WriteLine($"  {state}");
        if (!string.IsNullOrWhiteSpace(r.Proposal.Diff))
        {
            Console.WriteLine();
            Console.WriteLine($"  {Dim("change:")}");
            foreach (var line in r.Proposal.Diff.Split('\n'))
            {
                Console.WriteLine($"    {Dim(line.TrimEnd())}");
            }
        }
        return 0;
    }

    // Same colour discipline as VerifyCommand: gold for a seated stone only.
    private static readonly bool Color =
        Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsOutputRedirected;
    private static string Paint(string ansi, string t) => Color ? $"\x1b[{ansi}m{t}\x1b[0m" : t;
    private static string Ok(string t) => Paint("32", t);
    private static string Bad(string t) => Paint("31", t);
    private static string Gold(string t) => Paint("33", t);
    private static string Clay(string t) => Paint("38;5;173", t);
    private static string Dim(string t) => Paint("90", t);
}
